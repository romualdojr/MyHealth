using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.Plans;

namespace MyHealth.Domain.Coaching;

/// <summary>What the coach proposes to change in one planned exercise (or, for <see cref="TalkToClient"/>, in the follow-up).</summary>
public enum CoachSuggestionKind
{
    /// <summary>New suggested load for every set of the exercise.</summary>
    ChangeLoad = 1,

    /// <summary>New number of sets (copies the last set or removes from the end).</summary>
    ChangeSets = 2,

    /// <summary>New repetition range for every repetition-based set.</summary>
    ChangeRepetitions = 3,

    ChangeRest = 4,

    /// <summary>Another exercise from the library in the same place, keeping the sets.</summary>
    SwapExercise = 5,

    /// <summary>A technique or execution cue added to the exercise's instructions.</summary>
    AddInstruction = 6,

    /// <summary>Nothing changes in the plan: the trainer should talk to the client first (e.g. after discomfort).</summary>
    TalkToClient = 7,
}

public enum CoachSuggestionStatus
{
    Pending = 1,
    Applied = 2,
    Dismissed = 3,
}

/// <summary>
/// One change the coach proposes, with the reason it gives from the client's records. Only the fields of its
/// <see cref="Kind"/> are set. Nothing changes until the trainer applies it, and applying only changes the draft.
/// </summary>
public sealed record CoachSuggestion(
    Guid Id,
    CoachSuggestionKind Kind,
    Guid? PlannedSessionId,
    Guid? PlannedExerciseId,
    string? ExerciseName,
    decimal? LoadKg,
    int? Sets,
    int? MinRepetitions,
    int? MaxRepetitions,
    int? RestSeconds,
    Guid? ReplacementExerciseId,
    string? ReplacementExerciseName,
    string? Instruction,
    string Reason,
    CoachSuggestionStatus Status = CoachSuggestionStatus.Pending,
    DateTimeOffset? ResolvedAt = null);

/// <summary>
/// One request of a trainer to the AI coach about a client's plan: what was analysed, by which model, the summary and
/// the suggestions with their outcome. Kept with the plan's tenant as part of the trainer's records (who decided what,
/// and why); the client's data sent to the model is not stored here.
/// </summary>
public sealed class CoachAnalysis : AggregateRoot<Guid>
{
    public const int MaxSuggestions = 12;

    private CoachAnalysis(Guid id) : base(id) { }

    private CoachAnalysis() { }

    public Guid TenantId { get; private set; }

    public Guid TrainingPlanId { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid TrainerProfileId { get; private set; }

    public Guid TrainerUserId { get; private set; }

    /// <summary>The plan version the client was performing when the analysis ran (0 = never published).</summary>
    public int PlanVersionNumber { get; private set; }

    public string Model { get; private set; } = null!;

    public string? Focus { get; private set; }

    public string Summary { get; private set; } = null!;

    /// <summary>Fixed texts set by the server (e.g. why no progression was proposed), never by the model.</summary>
    public List<string> Notices { get; private set; } = [];

    public List<CoachSuggestion> Suggestions { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public static CoachAnalysis Create(
        TrainingPlan plan, string model, string? focus, string summary, IReadOnlyList<string> notices, IReadOnlyList<CoachSuggestion> suggestions,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7(now))
        {
            TenantId = plan.TenantId,
            TrainingPlanId = plan.Id,
            ClientId = plan.ClientId,
            TrainerProfileId = plan.TrainerProfileId,
            TrainerUserId = plan.TrainerUserId,
            PlanVersionNumber = plan.CurrentVersionNumber,
            Model = model,
            Focus = focus,
            Summary = summary,
            Notices = [.. notices],
            Suggestions = [.. suggestions.Take(MaxSuggestions)],
            CreatedAt = now,
        };

    public CoachSuggestion? FindSuggestion(Guid suggestionId) => Suggestions.FirstOrDefault(s => s.Id == suggestionId);

    /// <summary>Applies one suggestion to the plan's draft. The trainer still reviews and publishes the draft.</summary>
    public Result Apply(Guid suggestionId, TrainingPlan plan, Exercise? replacement, DateTimeOffset now)
    {
        var suggestion = FindSuggestion(suggestionId);
        if (suggestion is null)
        {
            return CoachErrors.SuggestionNotFound;
        }

        if (suggestion.Status == CoachSuggestionStatus.Applied)
        {
            return Result.Success();
        }

        if (suggestion.Status != CoachSuggestionStatus.Pending)
        {
            return CoachErrors.SuggestionClosed;
        }

        if (plan.Id != TrainingPlanId)
        {
            return CoachErrors.SuggestionNotFound;
        }

        if (suggestion.Kind != CoachSuggestionKind.TalkToClient)
        {
            var changed = CoachSuggestionApplier.Apply(plan.Draft, suggestion, replacement);
            if (changed.IsFailure)
            {
                return changed.Error;
            }

            var updated = plan.UpdateDraft(changed.Value, now);
            if (updated.IsFailure)
            {
                return updated;
            }
        }

        Replace(suggestion with { Status = CoachSuggestionStatus.Applied, ResolvedAt = now });
        return Result.Success();
    }

    public Result Dismiss(Guid suggestionId, DateTimeOffset now)
    {
        var suggestion = FindSuggestion(suggestionId);
        if (suggestion is null)
        {
            return CoachErrors.SuggestionNotFound;
        }

        if (suggestion.Status == CoachSuggestionStatus.Dismissed)
        {
            return Result.Success();
        }

        if (suggestion.Status != CoachSuggestionStatus.Pending)
        {
            return CoachErrors.SuggestionClosed;
        }

        Replace(suggestion with { Status = CoachSuggestionStatus.Dismissed, ResolvedAt = now });
        return Result.Success();
    }

    private void Replace(CoachSuggestion suggestion) =>
        Suggestions = [.. Suggestions.Select(s => s.Id == suggestion.Id ? suggestion : s)];
}

/// <summary>Turns a suggestion into a new draft. Fails when the draft no longer has the exercise it was made for.</summary>
public static class CoachSuggestionApplier
{
    public static Result<PlanContent> Apply(PlanContent draft, CoachSuggestion suggestion, Exercise? replacement)
    {
        var session = draft.Sessions.FirstOrDefault(s => s.Exercises.Any(e => e.Id == suggestion.PlannedExerciseId));
        var current = session?.Exercises.First(e => e.Id == suggestion.PlannedExerciseId);
        if (session is null || current is null)
        {
            return CoachErrors.PlanChanged;
        }

        PlannedExercise? changed = suggestion.Kind switch
        {
            CoachSuggestionKind.ChangeLoad when suggestion.LoadKg is { } load =>
                current with { Sets = [.. current.Sets.Select(s => s with { SuggestedLoadKg = load })] },
            CoachSuggestionKind.ChangeSets when suggestion.Sets is { } count && current.Sets.Count > 0 =>
                current with { Sets = Resize(current.Sets, count) },
            CoachSuggestionKind.ChangeRepetitions when suggestion.MinRepetitions is { } min =>
                current with
                {
                    Sets = [.. current.Sets.Select(s => s.MinRepetitions is null ? s : s with { MinRepetitions = min, MaxRepetitions = suggestion.MaxRepetitions ?? min })],
                },
            CoachSuggestionKind.ChangeRest when suggestion.RestSeconds is { } rest => current with { RestSeconds = rest },
            CoachSuggestionKind.AddInstruction when !string.IsNullOrWhiteSpace(suggestion.Instruction) =>
                current with
                {
                    Instructions = string.IsNullOrWhiteSpace(current.Instructions)
                        ? suggestion.Instruction.Trim()
                        : $"{current.Instructions.Trim()}\n{suggestion.Instruction.Trim()}",
                },
            CoachSuggestionKind.SwapExercise when replacement is not null && replacement.Id == suggestion.ReplacementExerciseId =>
                current with
                {
                    // A different exercise is a new line of progress, so it gets a new id.
                    Id = Guid.CreateVersion7(),
                    ExerciseId = replacement.Id,
                    ExerciseName = replacement.Name,
                    ExerciseVersion = replacement.Version,
                    AlternativeExerciseIds = [.. current.AlternativeExerciseIds.Where(id => id != replacement.Id)],
                },
            _ => null,
        };

        if (changed is null)
        {
            return CoachErrors.InvalidSuggestion;
        }

        var sessions = draft.Sessions
            .Select(s => s.Id != session.Id ? s : s with { Exercises = [.. s.Exercises.Select(e => e.Id == current.Id ? changed : e)] })
            .ToList();
        return new PlanContent(sessions);
    }

    private static List<SetTarget> Resize(IReadOnlyList<SetTarget> sets, int count)
    {
        var ordered = sets.OrderBy(s => s.Order).ToList();
        if (count <= ordered.Count)
        {
            return ordered.Take(count).ToList();
        }

        var last = ordered[^1];
        return [.. ordered, .. Enumerable.Range(ordered.Count + 1, count - ordered.Count).Select(order => last with { Order = order })];
    }
}

public interface ICoachAnalysisRepository : IRepository<CoachAnalysis, Guid>
{
    /// <summary>How many analyses the tenant requested since <paramref name="since"/> (daily cost limit).</summary>
    Task<int> CountForTenantSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken = default);
}

public static class CoachErrors
{
    public static readonly Error SharingRequired = Error.Forbidden(
        "Coach.SharingRequired", "The client has not allowed AI analysis of their training data. They can turn it on in the app's sharing settings.");
    public static readonly Error Unavailable = Error.Failure("Coach.Unavailable", "The AI coach is not available right now. Try again later.");
    public static readonly Error DailyLimitReached = Error.Conflict("Coach.DailyLimitReached", "Your organization reached today's limit of AI analyses.");
    public static readonly Error DailyChatLimitReached = Error.Conflict(
        "Coach.DailyChatLimitReached", "Your organization reached today's limit of messages to the AI assistant. It resets within 24 hours.");
    public static readonly Error AnalysisNotFound = Error.NotFound("Coach.AnalysisNotFound", "The analysis was not found.");
    public static readonly Error SuggestionNotFound = Error.NotFound("Coach.SuggestionNotFound", "The suggestion was not found.");
    public static readonly Error SuggestionClosed = Error.Conflict("Coach.SuggestionClosed", "This suggestion was already applied or dismissed.");
    public static readonly Error PlanChanged = Error.Conflict(
        "Coach.PlanChanged", "The draft no longer has the exercise this suggestion was made for. Request a new analysis.");
    public static readonly Error InvalidSuggestion = Error.Validation("Coach.InvalidSuggestion", "This suggestion cannot be applied to the draft.");
}
