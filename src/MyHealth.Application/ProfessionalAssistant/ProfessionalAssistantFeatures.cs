using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.ProfessionalAssistant;
using MyHealth.Application.Coaching;
using MyHealth.Application.Common;
using MyHealth.Application.Nutrition;
using MyHealth.Application.TrainerAssistant;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Coaching;
using MyHealth.Domain.Professionals;

namespace MyHealth.Application.ProfessionalAssistant;

/// <summary>
/// A patient as the professional sees them on the assistant page: computed from the records, no AI involved.
/// <paramref name="Detail"/> is a one-line status ("5 de 14 dias com diário · retorno 12/10").
/// </summary>
public sealed record PatientSummary(
    Guid ClientId,
    string Name,
    Guid RelationshipId,
    bool AiConsent,
    Guid? PlanId,
    string? PlanName,
    string Detail,
    string? Attention);

/// <param name="DailyLimit">Messages the organization may send to the assistants per 24 hours; <paramref name="UsedToday"/> already sent.</param>
public sealed record ProfessionalAssistantOverview(string Model, Discipline Discipline, IReadOnlyList<PatientSummary> Patients, int DailyLimit, int UsedToday);

/// <summary>What the model knows about one patient. <see cref="Alias"/> replaces the name ("Paciente 1"); no identifiers are sent.</summary>
public sealed record AssistantPatient(string Alias, NutritionPatientContext? Nutrition, TherapyPatientContext? Therapy);

/// <summary>A modality the practitioner is qualified for and may start care plans in.</summary>
public sealed record AssistantModality(string Code, string Name);

public sealed record ProfessionalAssistantContext(
    Discipline Discipline,
    DateOnly Today,
    string? FocusAlias,
    IReadOnlyList<AssistantPatient> Patients,
    int PatientsWithoutConsent,
    bool Truncated,
    IReadOnlyList<AssistantModality>? Modalities);

/// <summary>A meal plan the assistant proposes. Nothing is stored: the dietitian creates the draft from it (POST /meal-plans, then its meals).</summary>
public sealed record MealPlanProposal(Guid ClientId, string ClientName, MealPlanDetailsInput Details, IReadOnlyList<PlannedMealInput> Meals, string? Reason);

/// <summary>A care plan the assistant proposes. Nothing is stored: the practitioner reviews it and starts it (POST /therapy/care-plans).</summary>
public sealed record CarePlanProposal(
    Guid ClientId, string ClientName, string ModalityCode, string ModalityName, string Goals, int? PlannedSessions, string? Frequency, DateOnly? NextReviewOn,
    string? Reason);

public sealed record ProfessionalAssistantReplyResponse(
    string Reply, IReadOnlyList<MealPlanProposal> MealPlans, IReadOnlyList<CarePlanProposal> CarePlans, int PatientsIncluded, int PatientsWithoutConsent);

public static class ProfessionalAssistantErrors
{
    public static readonly Error SharingRequired = Error.Forbidden(
        "Assistant.SharingRequired", "The client has not allowed AI analysis of their records. They can turn it on in the app's sharing settings.");
}

public sealed record GetProfessionalAssistantOverviewQuery(Discipline Discipline) : IQuery<ProfessionalAssistantOverview>;

internal sealed class GetProfessionalAssistantOverviewHandler(
    ICareAccess access, IEnumerable<AssistantWorkspace> workspaces, IProfessionalAssistantModel model, IReadDbContext db, CoachSettings settings, TimeProvider clock)
    : IQueryHandler<GetProfessionalAssistantOverviewQuery, ProfessionalAssistantOverview>
{
    public async Task<Result<ProfessionalAssistantOverview>> Handle(GetProfessionalAssistantOverviewQuery query, CancellationToken cancellationToken)
    {
        var workspace = workspaces.Single(w => w.Discipline == query.Discipline);
        var professional = await access.GetCurrentProfessionalAsync(query.Discipline, requireVerified: false, cancellationToken);
        if (professional.IsFailure)
        {
            return professional.Error;
        }

        var data = await workspace.LoadAsync(professional.Value, cancellationToken);
        var used = await TrainerChatUsage.CountAsync(db, professional.Value.TenantId, clock.GetUtcNow(), cancellationToken);
        return new ProfessionalAssistantOverview(
            model.Name, query.Discipline, [.. data.Patients.Select(p => p.Summary)], settings.DailyChatMessagesPerTenant, used);
    }
}

/// <summary>
/// A dietitian or a therapy practitioner writes to the assistant about all their patients, or about one
/// (<paramref name="ClientId"/>). Only patients who allow AI analysis (<see cref="DataCategory.AiCoaching"/>) are sent to
/// the model, under aliases. Plans come back as proposals the professional turns into a draft (meal plan) or starts
/// (care plan) through the usual commands, so nothing reaches the patient without the professional.
/// </summary>
public sealed record SendProfessionalAssistantMessageCommand(Discipline Discipline, string Message, IReadOnlyList<AssistantTurn>? History, Guid? ClientId)
    : ICommand<ProfessionalAssistantReplyResponse>;

internal sealed class SendProfessionalAssistantMessageValidator : AbstractValidator<SendProfessionalAssistantMessageCommand>
{
    public SendProfessionalAssistantMessageValidator()
    {
        RuleFor(c => c.Discipline).Must(d => d is Discipline.Dietitian or Discipline.TherapyPractitioner)
            .WithMessage("The assistant is available to dietitians and therapy practitioners here; trainers use /trainer/assistant.");
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

internal sealed class SendProfessionalAssistantMessageHandler(
    ICareAccess access, IEnumerable<AssistantWorkspace> workspaces, IProfessionalAssistantModel model, IAuditLog audit, IUnitOfWork unitOfWork,
    TimeProvider clock, IReadDbContext db, CoachSettings settings)
    : ICommandHandler<SendProfessionalAssistantMessageCommand, ProfessionalAssistantReplyResponse>
{
    public const int MaxPatients = 25;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<Result<ProfessionalAssistantReplyResponse>> Handle(SendProfessionalAssistantMessageCommand command, CancellationToken cancellationToken)
    {
        var workspace = workspaces.Single(w => w.Discipline == command.Discipline);
        var professional = await access.GetCurrentProfessionalAsync(command.Discipline, requireVerified: true, cancellationToken);
        if (professional.IsFailure)
        {
            return professional.Error;
        }

        var me = professional.Value;
        var now = clock.GetUtcNow();
        if (await TrainerChatUsage.CountAsync(db, me.TenantId, now, cancellationToken) >= settings.DailyChatMessagesPerTenant)
        {
            return CoachErrors.DailyChatLimitReached;
        }

        var data = await workspace.LoadAsync(me, cancellationToken);
        PatientEntry? focus = null;
        if (command.ClientId is { } clientId)
        {
            focus = data.Patients.FirstOrDefault(p => p.Summary.ClientId == clientId);
            if (focus is null)
            {
                // Not this professional's patient here: not found, without revealing whether the person exists (MT-09).
                return CareRelationshipErrors.NotFound;
            }

            if (!focus.Summary.AiConsent)
            {
                return ProfessionalAssistantErrors.SharingRequired;
            }
        }

        // The focused patient first, then the others who allow AI analysis.
        var consenting = data.Patients.Where(p => p.Summary.AiConsent).OrderBy(p => p == focus ? 0 : 1).ToList();
        var included = consenting.Take(MaxPatients).Select((p, i) => new IncludedPatient($"Paciente {i + 1}", p)).ToList();
        var aliases = new StudentAliases(included.Select(p => (p.Alias, p.Entry.Summary.Name)));

        var context = new ProfessionalAssistantContext(
            command.Discipline,
            DateOnly.FromDateTime(now.UtcDateTime),
            focus is null ? null : included[0].Alias,
            [.. included.Select(p => new AssistantPatient(p.Alias, p.Entry.Nutrition, p.Entry.Therapy))],
            data.Patients.Count - consenting.Count,
            consenting.Count > included.Count,
            data.Modalities);

        // Free text in the records (assessment notes, diary, session records) may carry a name: hidden like the messages.
        var request = new ProfessionalAssistantModelRequest(
            workspace.SystemPrompt, context, aliases.Hide(JsonSerializer.Serialize(context, Json)),
            [.. (command.History ?? []).Select(t => t with { Text = aliases.Hide(t.Text) })], aliases.Hide(command.Message.Trim()), workspace.Tools);

        var reply = await model.RespondAsync(request, cancellationToken);
        if (reply is null)
        {
            return CoachErrors.Unavailable;
        }

        var proposals = workspace.Proposals(reply.ToolCalls, included, context, data, aliases);

        // One entry per answered message counts toward the organization's daily limit, shared with the trainers' chat.
        audit.Record(TrainerChatUsage.Action, TrainerChatUsage.ProfessionalResourceType, me.Profile.Id, subjectUserId: null, me.TenantId);

        // Each patient's records reached the AI provider: audited per patient, without content.
        foreach (var patient in included)
        {
            audit.Record(AuditActions.Assist, workspace.AuditResourceType, patient.Entry.Summary.RelationshipId, patient.Entry.Summary.ClientId, me.TenantId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var text = aliases.Reveal(reply.Text.Trim());
        if (text.Length == 0)
        {
            text = proposals.MealPlans.Count + proposals.CarePlans.Count > 0
                ? "Preparei uma proposta. Revise abaixo antes de criar o plano."
                : "Não tenho uma resposta para isso. Pode reformular?";
        }

        return new ProfessionalAssistantReplyResponse(text, proposals.MealPlans, proposals.CarePlans, included.Count, context.PatientsWithoutConsent);
    }
}

/// <summary>A patient of the professional with what the assistant page and the chat need.</summary>
internal sealed record PatientEntry(PatientSummary Summary, NutritionPatientContext? Nutrition = null, TherapyPatientContext? Therapy = null);

internal sealed record IncludedPatient(string Alias, PatientEntry Entry);

internal sealed record WorkspaceData(IReadOnlyList<PatientEntry> Patients, IReadOnlyList<AssistantModality>? Modalities = null);

internal sealed record AssistantProposals(IReadOnlyList<MealPlanProposal> MealPlans, IReadOnlyList<CarePlanProposal> CarePlans)
{
    public static readonly AssistantProposals None = new([], []);
}

/// <summary>What one discipline brings to the assistant: its patients' records, the rules for the model and how proposals are checked.</summary>
internal abstract class AssistantWorkspace
{
    /// <summary>Plans proposed in one answer, at most.</summary>
    public const int MaxProposals = 3;

    internal static readonly JsonSerializerOptions ToolJson = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public abstract Discipline Discipline { get; }

    public abstract string SystemPrompt { get; }

    public abstract IReadOnlyList<AssistantToolDefinition> Tools { get; }

    public abstract string AuditResourceType { get; }

    public abstract Task<WorkspaceData> LoadAsync(ActingProfessional professional, CancellationToken cancellationToken);

    /// <summary>Turns the model's tool calls into proposals that pass the same rules as the commands they feed; the rest is dropped.</summary>
    public abstract AssistantProposals Proposals(
        IReadOnlyList<AssistantToolCall> calls, IReadOnlyList<IncludedPatient> included, ProfessionalAssistantContext context, WorkspaceData data,
        StudentAliases aliases);

    /// <summary>The patient a proposal is for: named by alias, else the focused one, else the only one in the context.</summary>
    protected static IncludedPatient? PatientOf(string? alias, IReadOnlyList<IncludedPatient> included, ProfessionalAssistantContext context)
    {
        if (!string.IsNullOrWhiteSpace(alias))
        {
            return included.FirstOrDefault(p => string.Equals(p.Alias, alias.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (context.FocusAlias is { } focus)
        {
            return included.FirstOrDefault(p => p.Alias == focus);
        }

        return included.Count == 1 ? included[0] : null;
    }

    protected static T? Read<T>(AssistantToolCall call)
        where T : class
    {
        if (call.Input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return call.Input.Deserialize<T>(ToolJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    protected static AssistantToolDefinition Define(string name, string description, string schema) =>
        new(name, description, JsonDocument.Parse(schema).RootElement.Clone());
}

/// <summary>The tools the dietitian's and the therapy practitioner's assistant may call: each only prepares a proposal.</summary>
public static class ProfessionalAssistantTools
{
    public const string DraftMealPlan = "draft_meal_plan";
    public const string ProposeCarePlan = "propose_care_plan";
}
