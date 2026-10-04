using System.Text.Json;
using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.TrainerAssistant;
using MyHealth.Application.Coaching;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Coaching;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Application.TrainerAssistant;

/// <summary>A student as the trainer sees them on the assistant page: computed from the records, no AI involved.</summary>
public sealed record StudentSummary(
    Guid ClientId,
    string Name,
    Guid RelationshipId,
    bool AiConsent,
    Guid? PlanId,
    string? PlanName,
    int PlanVersion,
    int CompletedLast28Days,
    int ExpectedLast28Days,
    DateTimeOffset? LastWorkoutAt,
    double? AverageExertion14Days,
    bool RecentDiscomfort,
    int PendingFeedback);

/// <param name="DailyLimit">Messages the organization may send to the assistant per 24 hours; <paramref name="UsedToday"/> already sent.</param>
public sealed record TrainerAssistantOverview(string Model, IReadOnlyList<StudentSummary> Students, int DailyLimit, int UsedToday);

/// <summary>What the model knows about one student. <see cref="Alias"/> replaces the name ("Aluno 1"); no identifiers are sent.</summary>
public sealed record TrainerAssistantStudent(
    string Alias,
    string? PlanName,
    int CompletedLast28Days,
    int ExpectedLast28Days,
    DateOnly? LastWorkout,
    double? AverageExertion14Days,
    bool RecentDiscomfort,
    int PendingFeedback,
    CoachContext? Training);

public sealed record TrainerAssistantContext(
    DateOnly Today, string? FocusAlias, IReadOnlyList<TrainerAssistantStudent> Students, int StudentsWithoutConsent, bool Truncated);

public sealed record TrainerChatSuggestion(Guid AnalysisId, Guid PlanId, Guid ClientId, string ClientName, string PlanName, CoachSuggestionResponse Suggestion);

public sealed record TrainerAssistantReplyResponse(
    string Reply, IReadOnlyList<TrainerChatSuggestion> Suggestions, int StudentsIncluded, int StudentsWithoutConsent);

public sealed record GetTrainerAssistantOverviewQuery : IQuery<TrainerAssistantOverview>;

internal sealed class GetTrainerAssistantOverviewHandler(
    ICareAccess access, TrainerRoster roster, ITrainerAssistantModel model, IReadDbContext db, CoachSettings settings, TimeProvider clock)
    : IQueryHandler<GetTrainerAssistantOverviewQuery, TrainerAssistantOverview>
{
    public async Task<Result<TrainerAssistantOverview>> Handle(GetTrainerAssistantOverviewQuery query, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: false, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var students = await roster.BuildAsync(trainer.Value, cancellationToken);
        var used = await TrainerChatUsage.CountAsync(db, trainer.Value.TenantId, clock.GetUtcNow(), cancellationToken);
        return new TrainerAssistantOverview(model.Name, [.. students.Select(s => s.Summary)], settings.DailyChatMessagesPerTenant, used);
    }
}

/// <summary>
/// The trainer writes to the assistant about all their students, or about one (<paramref name="ClientId"/>). Only
/// students who allow AI analysis (<see cref="DataCategory.AiCoaching"/>) are sent to the model, under aliases; plan
/// changes come back as suggestions stored like the coach's, which the trainer applies to the draft.
/// </summary>
public sealed record SendTrainerAssistantMessageCommand(string Message, IReadOnlyList<AssistantTurn>? History, Guid? ClientId)
    : ICommand<TrainerAssistantReplyResponse>
{
    public const int MaxHistory = 10;
}

internal sealed class SendTrainerAssistantMessageValidator : AbstractValidator<SendTrainerAssistantMessageCommand>
{
    public SendTrainerAssistantMessageValidator()
    {
        RuleFor(c => c.Message).NotEmpty().MaximumLength(2000);
        RuleFor(c => c.History).Must(h => h is null || h.Count <= SendTrainerAssistantMessageCommand.MaxHistory)
            .WithMessage($"Send at most {SendTrainerAssistantMessageCommand.MaxHistory} earlier messages.");
        RuleForEach(c => c.History).ChildRules(turn =>
        {
            turn.RuleFor(t => t.Role).Must(r => r is AssistantTurn.User or AssistantTurn.Assistant).WithMessage("Role is user or assistant.");
            turn.RuleFor(t => t.Text).NotEmpty().MaximumLength(4000);
        });
    }
}

internal sealed class SendTrainerAssistantMessageHandler(
    ICareAccess access, TrainerRoster roster, CoachContextBuilder contexts, ITrainerAssistantModel model, ITrainingPlanRepository plans,
    ICoachAnalysisRepository analyses, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock, IReadDbContext db, CoachSettings settings)
    : ICommandHandler<SendTrainerAssistantMessageCommand, TrainerAssistantReplyResponse>
{
    public const int MaxStudents = 25;

    public async Task<Result<TrainerAssistantReplyResponse>> Handle(SendTrainerAssistantMessageCommand command, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: true, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        if (await TrainerChatUsage.CountAsync(db, trainer.Value.TenantId, clock.GetUtcNow(), cancellationToken) >= settings.DailyChatMessagesPerTenant)
        {
            return CoachErrors.DailyChatLimitReached;
        }

        var everyone = await roster.BuildAsync(trainer.Value, cancellationToken);
        RosterEntry? focus = null;
        if (command.ClientId is { } clientId)
        {
            focus = everyone.FirstOrDefault(s => s.Summary.ClientId == clientId);
            if (focus is null)
            {
                // Not this trainer's student here: not found, without revealing whether the person exists (MT-09).
                return CareRelationshipErrors.NotFound;
            }

            if (!focus.Summary.AiConsent)
            {
                return CoachErrors.SharingRequired;
            }
        }

        // The focused student first, then the others who allow AI analysis.
        var consenting = everyone.Where(s => s.Summary.AiConsent).OrderBy(s => s == focus ? 0 : 1).ToList();
        var included = consenting.Take(MaxStudents).ToList();
        var aliases = new StudentAliases(included.Select((s, i) => (Alias: $"Aluno {i + 1}", s.Summary.Name)));
        var now = clock.GetUtcNow();

        var students = new List<(RosterEntry Entry, TrainerAssistantStudent Student)>();
        for (var i = 0; i < included.Count; i++)
        {
            var entry = included[i];
            var training = entry.Plan is null ? null : await contexts.BuildAsync(entry.Plan, entry.WellbeingShared, cancellationToken);
            var s = entry.Summary;
            students.Add((entry, new TrainerAssistantStudent(
                $"Aluno {i + 1}", s.PlanName, s.CompletedLast28Days, s.ExpectedLast28Days, s.LastWorkoutAt is { } at ? DateOnly.FromDateTime(at.UtcDateTime) : null,
                s.AverageExertion14Days, s.RecentDiscomfort, s.PendingFeedback, training)));
        }

        var context = new TrainerAssistantContext(
            DateOnly.FromDateTime(now.UtcDateTime), focus is null ? null : "Aluno 1", [.. students.Select(s => s.Student)],
            everyone.Count - consenting.Count, consenting.Count > included.Count);

        var request = new TrainerAssistantModelRequest(
            TrainerAssistantPrompt.System, context, JsonSerializer.Serialize(context, CoachMapping.Json),
            [.. (command.History ?? []).Select(t => t with { Text = aliases.Hide(t.Text) })], aliases.Hide(command.Message.Trim()), CoachTools.All);

        var reply = await model.RespondAsync(request, cancellationToken);
        if (reply is null)
        {
            return CoachErrors.Unavailable;
        }

        var cards = await SuggestionsAsync(reply.ToolCalls, students, focus, aliases, command.Message, now, cancellationToken);

        // One entry per answered message counts toward the organization's daily limit (TrainerChatUsage).
        audit.Record(TrainerChatUsage.Action, TrainerChatUsage.ResourceType, trainer.Value.Profile.Id, subjectUserId: null, trainer.Value.TenantId);

        // Each student's records reached the AI provider: audited per student, without content.
        foreach (var (entry, _) in students)
        {
            audit.Record(AuditActions.Assist, "TrainerAssistant", entry.Summary.RelationshipId, entry.Summary.ClientId, trainer.Value.TenantId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var text = aliases.Reveal(reply.Text.Trim());
        if (text.Length == 0)
        {
            text = cards.Count > 0 ? "Preparei algumas sugestões. Confira abaixo." : "Não tenho uma resposta para isso. Pode reformular?";
        }

        return new TrainerAssistantReplyResponse(text, cards, included.Count, context.StudentsWithoutConsent);
    }

    /// <summary>Validates the model's plan changes per student (the coach's rules) and stores them for the trainer to apply.</summary>
    private async Task<List<TrainerChatSuggestion>> SuggestionsAsync(
        IReadOnlyList<AssistantToolCall> calls, List<(RosterEntry Entry, TrainerAssistantStudent Student)> students, RosterEntry? focus,
        StudentAliases aliases, string message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cards = new List<TrainerChatSuggestion>();
        var byStudent = calls
            .Select(call => (Call: call, Owner: OwnerOf(call, students, focus)))
            .Where(x => x.Owner is not null)
            .GroupBy(x => x.Owner!.Value.Entry);

        foreach (var group in byStudent)
        {
            var entry = group.Key;
            var training = group.First().Owner!.Value.Student.Training!;
            var outcome = CoachSuggestionPolicy.Apply([.. group.Select(x => x.Call)], training, now);
            if (outcome.Suggestions.Count == 0)
            {
                continue;
            }

            var plan = await plans.GetByIdAsync(entry.Plan!.Id, cancellationToken);
            if (plan is null || plan.Status == PlanStatus.Ended)
            {
                continue;
            }

            var suggestions = outcome.Suggestions.Select(s => s with { Reason = aliases.Reveal(s.Reason) }).ToList();
            var focusText = $"Chat: {message.Trim()}";
            var analysis = CoachAnalysis.Create(
                plan, model.Name, focusText.Length > 500 ? focusText[..500] : focusText, "Sugestões do assistente no chat.",
                [.. outcome.Notices], suggestions, now);
            analyses.Add(analysis);
            cards.AddRange(analysis.Suggestions.Select(s => new TrainerChatSuggestion(
                analysis.Id, plan.Id, entry.Summary.ClientId, entry.Summary.Name, plan.Details.Name, CoachMapping.ToResponse(s))));
        }

        return cards;
    }

    /// <summary>The student whose plan a tool call targets: by its planned exercise, or the focused student.</summary>
    private static (RosterEntry Entry, TrainerAssistantStudent Student)? OwnerOf(
        AssistantToolCall call, List<(RosterEntry Entry, TrainerAssistantStudent Student)> students, RosterEntry? focus)
    {
        if (call.Input.ValueKind == JsonValueKind.Object
            && call.Input.TryGetProperty("planned_exercise_id", out var value)
            && Guid.TryParse(value.GetString(), out var plannedExerciseId))
        {
            foreach (var student in students)
            {
                if (student.Student.Training?.FindPlannedExercise(plannedExerciseId) is not null)
                {
                    return student;
                }
            }

            return null;
        }

        if (call.Name != CoachTools.TalkToClient)
        {
            return null;
        }

        // A conversation is about a student named by alias in the chat, or about the focused one.
        var alias = call.Input.ValueKind == JsonValueKind.Object && call.Input.TryGetProperty("student", out var named) && named.ValueKind == JsonValueKind.String
            ? named.GetString()
            : null;
        var target = students.FirstOrDefault(s => alias is not null ? string.Equals(s.Student.Alias, alias, StringComparison.OrdinalIgnoreCase) : s.Entry == focus);
        return target.Student?.Training is not null ? target : null;
    }
}

/// <summary>A student of the trainer with what the assistant page and the chat need.</summary>
internal sealed record RosterEntry(StudentSummary Summary, TrainingPlan? Plan, bool WellbeingShared);

/// <summary>The trainer's active students in the selected organization, with summaries computed from their records.</summary>
internal sealed class TrainerRoster(IReadDbContext db, TimeProvider clock)
{
    public async Task<List<RosterEntry>> BuildAsync(ActingProfessional trainer, CancellationToken cancellationToken)
    {
        var tenantId = trainer.TenantId;
        var profileId = trainer.Profile.Id;
        var now = clock.GetUtcNow();

        var relationships = await db.CareRelationships
            .Where(r => r.TenantId == tenantId && r.ProfessionalProfileId == profileId && r.Status == RelationshipStatus.Active && r.ClientId != null)
            .ToListAsync(cancellationToken);
        var clientIds = relationships.Select(r => r.ClientId!.Value).ToList();
        if (clientIds.Count == 0)
        {
            return [];
        }

        var names = await db.UserProfiles.Where(u => clientIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
        var plans = (await db.TrainingPlans
                .Where(p => p.TenantId == tenantId && p.TrainerProfileId == profileId && clientIds.Contains(p.ClientId) && p.Status != PlanStatus.Ended)
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.ClientId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Status == PlanStatus.Published).ThenByDescending(p => p.UpdatedAt).First());

        var since = now.AddDays(-28);
        var workouts = (await db.WorkoutSessions
                .Where(w => w.TenantId == tenantId && w.TrainerProfileId == profileId && clientIds.Contains(w.ClientId) && w.StartedAt >= since)
                .ToListAsync(cancellationToken))
            .ToLookup(w => w.ClientId);

        return
        [
            .. relationships
                .Select(r =>
                {
                    var clientId = r.ClientId!.Value;
                    var plan = plans.GetValueOrDefault(clientId);
                    var done = workouts[clientId].Where(w => w.Status == WorkoutStatus.Completed).ToList();
                    var recent = done.Where(w => w.CompletedAt >= now.AddDays(-14) && w.Feedback is not null).ToList();
                    var discomfort = workouts[clientId].Any(w =>
                        (w.StartedAt >= now.AddDays(-14) && w.Feedback?.ReportedDiscomfort == true)
                        || w.BodyChecks.Any(b => b.ReportedToTrainer && b.At >= now.AddDays(-14) && b.Answer is BodyCheckAnswer.Worse or BodyCheckAnswer.New));
                    var summary = new StudentSummary(
                        clientId,
                        names.GetValueOrDefault(clientId, "Aluno"),
                        r.Id,
                        r.HasActiveGrant(DataCategory.AiCoaching),
                        plan?.Id,
                        plan?.Details.Name,
                        plan?.CurrentVersionNumber ?? 0,
                        done.Count,
                        plan is null ? 0 : CoachContextBuilder.ExpectedSessions(plan.Details, DateOnly.FromDateTime(now.UtcDateTime)),
                        done.Max(w => w.CompletedAt),
                        recent.Count > 0 ? Math.Round(recent.Average(w => w.Feedback!.PerceivedExertion), 1) : null,
                        discomfort,
                        workouts[clientId].Count(w => w.Feedback is not null && w.Reply is null));
                    return new RosterEntry(summary, plan, r.HasActiveGrant(DataCategory.Wellbeing));
                })
                .OrderBy(e => e.Summary.Name, StringComparer.CurrentCulture),
        ];
    }
}

/// <summary>
/// Replaces students' names with aliases ("Aluno 1") before anything reaches the AI provider, and turns aliases back
/// into names in what comes back. Full names first, then first names that only one student has.
/// </summary>
public sealed class StudentAliases
{
    private readonly List<(string Alias, string Name)> _students;

    public StudentAliases(IEnumerable<(string Alias, string Name)> students) => _students = [.. students];

    public string Hide(string text)
    {
        var firstNames = _students
            .Select(s => (s.Alias, First: s.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty))
            .Where(s => s.First.Length >= 3)
            .GroupBy(s => s.First, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .Select(g => g.Single());

        foreach (var (alias, name) in _students.Where(s => s.Name.Trim().Length > 0).OrderByDescending(s => s.Name.Length))
        {
            text = Replace(text, name.Trim(), alias);
        }

        foreach (var (alias, first) in firstNames)
        {
            text = Replace(text, first, alias);
        }

        return text;
    }

    public string Reveal(string text)
    {
        foreach (var (alias, name) in _students.OrderByDescending(s => s.Alias.Length))
        {
            text = Replace(text, alias, name);
        }

        return text;
    }

    private static string Replace(string text, string find, string replacement) =>
        Regex.Replace(text, $@"(?<![\w]){Regex.Escape(find)}(?![\w])", replacement.Replace("$", "$$", StringComparison.Ordinal), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>The rules given to the trainer's chat model.</summary>
public static class TrainerAssistantPrompt
{
    public const string System = """
        You are the MyHealth assistant for a licensed personal trainer (CREF) in Brazil. Answer in Brazilian Portuguese, concisely,
        for a fitness professional. You see the trainer's students who allow AI analysis, in the JSON context: each with an alias
        ("Aluno 1", "Aluno 2"…), a summary (workouts in the last 28 days vs expected, last workout, average RPE, recent discomfort,
        feedback awaiting reply) and, when they have a plan, the plan draft with the sets they logged ("training").
        "focusAlias" is the student the trainer is looking at, if any. "studentsWithoutConsent" students exist but are not shared
        with you: if asked about someone not in the context, say they have not allowed AI analysis in the app.

        What you do:
        - Summaries: of one student or of all, with numbers from the context (adherence, loads, RPE, discomfort), and what needs attention first.
        - Evolving exercises: apply progressive overload when sets are completed at the top of the range with RPE ≤ 7, reduce load or volume when the
          client falls short or RPE ≥ 9, prefer fewer sets when adherence is low. To propose a change, call a tool with a planned_exercise_id from that
          student's training; the trainer reviews and applies it. Use swap_exercise only with an exerciseId from that student's alternatives.
        - Answer questions about the plans and the logged data.

        Rules you must never break:
        - Always refer to students by their alias exactly as written ("Aluno 2"); never invent or ask for names.
        - No diagnosis, no assessment of pain or injuries, no medical, nutritional or medication advice.
        - After discomfort or a body part getting worse, never propose a progression for that student: call talk_to_client.
        - Use only ids and numbers from the context. If the data is not there, say so. At most 8 tool calls per answer.
        - Do not say a change was made: the trainer applies suggestions.
        """;
}

/// <summary>
/// Messages a tenant's professionals sent to the assistants (the trainers' chat and the dietitians' and therapy
/// practitioners' chat share one limit) in the last 24 hours, counted from the audit log.
/// </summary>
internal static class TrainerChatUsage
{
    public const string Action = "message";
    public const string ResourceType = "TrainerAssistant.Message";
    public const string ProfessionalResourceType = "ProfessionalAssistant.Message";

    public static Task<int> CountAsync(IReadDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now.AddDays(-1);
        return db.AuditEntries.CountAsync(
            e => e.TenantId == tenantId && (e.ResourceType == ResourceType || e.ResourceType == ProfessionalResourceType) && e.Action == Action && e.OccurredAt >= since,
            cancellationToken);
    }
}
