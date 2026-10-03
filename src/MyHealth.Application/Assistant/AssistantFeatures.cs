using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Assistant;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Nutrition;
using MyHealth.Application.Workouts;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Assistant;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;

namespace MyHealth.Application.Assistant;

/// <summary>The assistant terms the client accepts. A new version needs a new acceptance.</summary>
public static class AssistantTerms
{
    public const int CurrentVersion = 1;

    public const string Text = """
        O assistente ajuda você a registrar refeições, séries e desconfortos e a tirar dúvidas sobre os seus planos.
        Ele prepara o registro e você confirma antes de salvar.

        O que ele nunca faz: não dá diagnóstico nem avalia sintomas, não muda o plano feito pelo seu profissional e não assina
        termos de consentimento por você.

        Seus dados: para responder, enviamos ao provedor de inteligência artificial apenas os dados das áreas que você escolher
        (treinos, alimentação, terapias) e só o necessário para cada pedido. O provedor não pode usar seus dados para outros fins.
        Você pode desativar o assistente quando quiser; o app continua funcionando sem ele.
        """;
}

public sealed record AssistantTermsResponse(int Version, string Text);

public sealed record AssistantConsentResponse(bool Active, int? TermsVersion, IReadOnlyList<AssistantScope> Scopes, DateTimeOffset? AcceptedAt, DateTimeOffset? WithdrawnAt);

public sealed record AssistantStatusResponse(AssistantTermsResponse Terms, AssistantConsentResponse? Consent);

public sealed record GetMyAssistantQuery : IQuery<AssistantStatusResponse>;

internal sealed class GetMyAssistantHandler(IUserContext user, IReadDbContext db) : IQueryHandler<GetMyAssistantQuery, AssistantStatusResponse>
{
    public async Task<Result<AssistantStatusResponse>> Handle(GetMyAssistantQuery query, CancellationToken cancellationToken)
    {
        var consent = await db.AssistantConsents.FirstOrDefaultAsync(c => c.UserId == user.UserId, cancellationToken);
        return new AssistantStatusResponse(new AssistantTermsResponse(AssistantTerms.CurrentVersion, AssistantTerms.Text), AssistantMapping.ToResponse(consent));
    }
}

/// <summary>The client accepts the current terms and chooses what the assistant may read, or changes that choice.</summary>
public sealed record AcceptAssistantTermsCommand(int TermsVersion, IReadOnlyList<AssistantScope> Scopes) : ICommand<AssistantConsentResponse>;

internal sealed class AcceptAssistantTermsValidator : AbstractValidator<AcceptAssistantTermsCommand>
{
    public AcceptAssistantTermsValidator()
    {
        RuleFor(c => c.Scopes).NotNull();
        RuleForEach(c => c.Scopes).IsInEnum();
    }
}

internal sealed class AcceptAssistantTermsHandler(IUserContext user, IAssistantConsentRepository consents, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<AcceptAssistantTermsCommand, AssistantConsentResponse>
{
    public async Task<Result<AssistantConsentResponse>> Handle(AcceptAssistantTermsCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var consent = await consents.GetByUserAsync(user.UserId, cancellationToken);
        if (consent is null)
        {
            var created = AssistantConsent.Accept(user.UserId, command.TermsVersion, AssistantTerms.CurrentVersion, command.Scopes, now);
            if (created.IsFailure)
            {
                return created.Error;
            }

            consent = created.Value;
            consents.Add(consent);
        }
        else
        {
            var renewed = consent.Renew(command.TermsVersion, AssistantTerms.CurrentVersion, command.Scopes, now);
            if (renewed.IsFailure)
            {
                return renewed.Error;
            }
        }

        audit.Record(AuditActions.Grant, nameof(AssistantConsent), consent.Id, subjectUserId: null, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return AssistantMapping.ToResponse(consent)!;
    }
}

public sealed record WithdrawAssistantConsentCommand : ICommand;

internal sealed class WithdrawAssistantConsentHandler(IUserContext user, IAssistantConsentRepository consents, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<WithdrawAssistantConsentCommand>
{
    public async Task<Result> Handle(WithdrawAssistantConsentCommand command, CancellationToken cancellationToken)
    {
        var consent = await consents.GetByUserAsync(user.UserId, cancellationToken);
        if (consent is null || consent.WithdrawnAt is not null)
        {
            return Result.Success();
        }

        consent.Withdraw(clock.GetUtcNow());
        audit.Record(AuditActions.Revoke, nameof(AssistantConsent), consent.Id, subjectUserId: null, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record ProposalField(string Label, string Value);

/// <summary>A card the client confirms or discards. <see cref="Notice"/> is fixed safety text set by the server, not the model.</summary>
public sealed record ProposalResponse(Guid Id, ProposalKind Kind, string Title, IReadOnlyList<ProposalField> Fields, string? Notice, DateTimeOffset ExpiresAt);

public sealed record AssistantReplyResponse(string Reply, IReadOnlyList<ProposalResponse> Proposals);

/// <summary>
/// The client writes or speaks to the assistant. It answers from the records the consent allows and prepares cards
/// for anything to record; nothing is saved here. <paramref name="WorkoutSessionId"/>, <paramref name="ExerciseId"/> and
/// <paramref name="SetNumber"/> come from the workout screen, so "feito, dez com quarenta" is enough.
/// </summary>
public sealed record SendAssistantMessageCommand(
    string Message, IReadOnlyList<AssistantTurn>? History, Guid? WorkoutSessionId, Guid? ExerciseId, int? SetNumber) : ICommand<AssistantReplyResponse>
{
    public const int MaxHistory = 10;
}

internal sealed class SendAssistantMessageValidator : AbstractValidator<SendAssistantMessageCommand>
{
    public SendAssistantMessageValidator()
    {
        RuleFor(c => c.Message).NotEmpty().MaximumLength(2000);
        RuleFor(c => c.History).Must(h => h is null || h.Count <= SendAssistantMessageCommand.MaxHistory)
            .WithMessage($"Send at most {SendAssistantMessageCommand.MaxHistory} earlier messages.");
        RuleForEach(c => c.History).ChildRules(turn =>
        {
            turn.RuleFor(t => t.Role).Must(r => r is AssistantTurn.User or AssistantTurn.Assistant).WithMessage("Role is user or assistant.");
            turn.RuleFor(t => t.Text).NotEmpty().MaximumLength(2000);
        });
        RuleFor(c => c.SetNumber).InclusiveBetween(1, 100);
    }
}

internal sealed class SendAssistantMessageHandler(
    IUserContext user, IUserProfileRepository users, IAssistantConsentRepository consents, IAssistantProposalRepository proposals,
    AssistantContextBuilder contexts, IAssistantModel model, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<SendAssistantMessageCommand, AssistantReplyResponse>
{
    public async Task<Result<AssistantReplyResponse>> Handle(SendAssistantMessageCommand command, CancellationToken cancellationToken)
    {
        var consent = await consents.GetByUserAsync(user.UserId, cancellationToken);
        if (consent is null || !consent.IsActiveFor(AssistantTerms.CurrentVersion))
        {
            return AssistantErrors.ConsentRequired;
        }

        var profile = await users.GetByIdAsync(user.UserId, cancellationToken);
        if (profile is null)
        {
            return UserErrors.ProfileNotFound;
        }

        var now = clock.GetUtcNow();
        await proposals.DeleteStaleAsync(user.UserId, now.AddDays(-1), cancellationToken);

        var context = await contexts.BuildAsync(profile, consent.Scopes, new AssistantScreenContext(command.WorkoutSessionId, command.ExerciseId, command.SetNumber), cancellationToken);
        var request = new AssistantModelRequest(
            AssistantTools.SystemPrompt, context, JsonSerializer.Serialize(context, AssistantMapping.Json), command.History ?? [], command.Message.Trim(), AssistantTools.All);

        var reply = await model.RespondAsync(request, cancellationToken);
        if (reply is null)
        {
            return AssistantErrors.Unavailable;
        }

        var cards = new List<ProposalResponse>();
        foreach (var call in reply.ToolCalls)
        {
            if (ProposalFactory.Create(call, context, now) is not { } prepared)
            {
                continue;
            }

            var proposal = AssistantProposal.Create(user.UserId, prepared.Kind, prepared.Payload, now);
            proposals.Add(proposal);
            cards.Add(new ProposalResponse(proposal.Id, proposal.Kind, prepared.Title, prepared.Fields, prepared.Notice, proposal.ExpiresAt));
        }

        // What reached the AI provider is a disclosure of personal data: audited, without content.
        audit.Record(AuditActions.Assist, model.Name, Guid.CreateVersion7(now), subjectUserId: null, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var text = string.IsNullOrWhiteSpace(reply.Text) ? (cards.Count > 0 ? "Preparei o registro. Confere?" : "Não entendi. Pode repetir?") : reply.Text.Trim();
        return new AssistantReplyResponse(text, cards);
    }
}

public sealed record ConfirmProposalResponse(ProposalKind Kind, Guid ResourceId);

/// <summary>
/// The client confirms a card. The same command the forms use runs as the client, so access rules, sharing and audit
/// are unchanged. Confirming again returns the same record.
/// </summary>
public sealed record ConfirmAssistantProposalCommand(Guid ProposalId) : ICommand<ConfirmProposalResponse>;

internal sealed class ConfirmAssistantProposalHandler(
    IUserContext user, IAssistantConsentRepository consents, IAssistantProposalRepository proposals,
    ICommandHandler<SaveFoodDiaryEntryCommand> saveMeal, ICommandHandler<RecordBodyCheckCommand> recordBodyCheck, ICommandHandler<LogSetCommand> logSet,
    IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ConfirmAssistantProposalCommand, ConfirmProposalResponse>
{
    public async Task<Result<ConfirmProposalResponse>> Handle(ConfirmAssistantProposalCommand command, CancellationToken cancellationToken)
    {
        var proposal = await proposals.GetByIdAsync(command.ProposalId, cancellationToken);
        if (proposal is null || proposal.UserId != user.UserId)
        {
            return AssistantErrors.ProposalNotFound;
        }

        var consent = await consents.GetByUserAsync(user.UserId, cancellationToken);
        if (consent is null || !consent.IsActiveFor(AssistantTerms.CurrentVersion))
        {
            return AssistantErrors.ConsentRequired;
        }

        var now = clock.GetUtcNow();
        if (proposal.Status != ProposalStatus.Confirmed && !proposal.IsOpen(now))
        {
            return AssistantErrors.ProposalClosed;
        }

        Result result;
        Guid resourceId;
        switch (proposal.Kind)
        {
            case ProposalKind.FoodDiaryEntry:
                var meal = JsonSerializer.Deserialize<FoodDiaryProposal>(proposal.Payload, AssistantMapping.Json)!;
                resourceId = meal.EntryId;
                result = await saveMeal.Handle(meal.ToCommand(), cancellationToken);
                break;
            case ProposalKind.BodyCheck:
                var check = JsonSerializer.Deserialize<BodyCheckProposal>(proposal.Payload, AssistantMapping.Json)!;
                resourceId = check.CheckId;
                result = await recordBodyCheck.Handle(check.ToCommand(), cancellationToken);
                break;
            default:
                var set = JsonSerializer.Deserialize<SetLogProposal>(proposal.Payload, AssistantMapping.Json)!;
                resourceId = set.SetLogId;
                result = await logSet.Handle(set.ToCommand(), cancellationToken);
                break;
        }

        if (result.IsFailure)
        {
            return result.Error;
        }

        proposal.Confirm(now);
        audit.Record(AuditActions.Confirm, nameof(AssistantProposal), proposal.Id, subjectUserId: null, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ConfirmProposalResponse(proposal.Kind, resourceId);
    }
}

public sealed record DiscardAssistantProposalCommand(Guid ProposalId) : ICommand;

internal sealed class DiscardAssistantProposalHandler(IUserContext user, IAssistantProposalRepository proposals, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<DiscardAssistantProposalCommand>
{
    public async Task<Result> Handle(DiscardAssistantProposalCommand command, CancellationToken cancellationToken)
    {
        var proposal = await proposals.GetByIdAsync(command.ProposalId, cancellationToken);
        if (proposal is null || proposal.UserId != user.UserId)
        {
            return AssistantErrors.ProposalNotFound;
        }

        var result = proposal.Discard(clock.GetUtcNow());
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

internal sealed record FoodDiaryProposal(
    Guid EntryId, Guid? MealPlanId, Guid? PlannedMealId, string MealName, MealAdherence Adherence, string? Description, DateTimeOffset EatenAt)
{
    public SaveFoodDiaryEntryCommand ToCommand() =>
        new(EntryId, MealPlanId, PlannedMealId, RelationshipId: null, MealName, EatenAt, TimeZoneId: null, Description, WaterMl: null, Adherence, ClientNotes: null);
}

internal sealed record BodyCheckProposal(Guid SessionId, Guid CheckId, string Subject, BodyCheckAnswer Answer, string? Details, bool ReportToTrainer, DateTimeOffset At)
{
    public RecordBodyCheckCommand ToCommand() => new(SessionId, CheckId, Subject, Answer, Details, ReportToTrainer, At);
}

internal sealed record SetLogProposal(
    Guid SessionId, Guid SetLogId, Guid ExerciseId, Guid? PlannedExerciseId, int SetNumber, int? Repetitions, decimal? LoadKg, DateTimeOffset LoggedAt)
{
    public LogSetCommand ToCommand() =>
        new(SessionId, SetLogId, ExerciseId, PlannedExerciseId, SetNumber, Repetitions, LoadKg, DurationSeconds: null, DistanceMeters: null, Difficulty: null, Notes: null, LoggedAt);
}

internal sealed record PreparedProposal(ProposalKind Kind, string Payload, string Title, IReadOnlyList<ProposalField> Fields, string? Notice);

/// <summary>
/// Turns a tool call into a card. Every id must come from the context the model was given, so a made-up or foreign id
/// is dropped here; confirming then runs the regular command, which checks ownership again.
/// </summary>
internal static class ProposalFactory
{
    public const string PainNotice = "Se a dor for forte, com inchaço ou o membro falhar, pare e procure atendimento médico.";

    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    public static PreparedProposal? Create(AssistantToolCall call, AssistantContext context, DateTimeOffset now) =>
        call.Name switch
        {
            AssistantTools.LogMeal => Meal(call.Input, context, now),
            AssistantTools.ReportBodyCheck => BodyCheck(call.Input, context, now),
            AssistantTools.LogSet => Set(call.Input, context, now),
            _ => null,
        };

    private static PreparedProposal? Meal(JsonElement input, AssistantContext context, DateTimeOffset now)
    {
        if (context.Nutrition is null || !Enum.TryParse<MealAdherence>(String(input, "adherence"), out var adherence))
        {
            return null;
        }

        var meals = context.Nutrition.TodaysMeals;
        var planned = Guid(input, "planned_meal_id") is { } id ? meals.FirstOrDefault(m => m.PlannedMealId == id) : null;
        var name = String(input, "meal_name") ?? planned?.Name;
        planned ??= meals.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Shared entries go to the dietitian of the plan; with no plan match and a single plan, logged as an unplanned meal of it.
        var share = Bool(input, "share_with_dietitian") ?? true;
        var plans = meals.Select(m => m.MealPlanId).Distinct().ToList();
        var planId = share ? planned?.MealPlanId ?? (plans.Count == 1 ? plans[0] : (Guid?)null) : null;
        var dietitian = planId is null ? null : meals.First(m => m.MealPlanId == planId);
        var description = String(input, "description");

        var payload = new FoodDiaryProposal(
            System.Guid.CreateVersion7(now), planId, share ? planned?.PlannedMealId : null, name.Trim(), adherence, description?.Trim(), now);
        List<ProposalField> fields =
        [
            new("Refeição", $"{payload.MealName} · hoje, {context.LocalTime}"),
            new("Plano", Adherence(adherence)),
        ];
        if (payload.Description is not null)
        {
            fields.Add(new("O que comi", payload.Description));
        }

        fields.Add(new("Quem vê", dietitian is null ? "Só você" : $"{dietitian.Dietitian} · {dietitian.Organization}"));
        return new PreparedProposal(ProposalKind.FoodDiaryEntry, JsonSerializer.Serialize(payload, AssistantMapping.Json), "Diário alimentar", fields, null);
    }

    private static PreparedProposal? BodyCheck(JsonElement input, AssistantContext context, DateTimeOffset now)
    {
        var sessionId = Guid(input, "workout_session_id");
        var workout = context.Training?.RecentWorkouts.FirstOrDefault(w => w.WorkoutSessionId == sessionId);
        var subject = String(input, "subject");
        if (workout is null || string.IsNullOrWhiteSpace(subject) || !Enum.TryParse<BodyCheckAnswer>(String(input, "answer"), out var answer))
        {
            return null;
        }

        var report = Bool(input, "report_to_trainer") ?? true;
        var details = String(input, "details");
        var payload = new BodyCheckProposal(workout.WorkoutSessionId, System.Guid.CreateVersion7(now), subject.Trim(), answer, details?.Trim(), report, now);
        List<ProposalField> fields =
        [
            new("Treino", $"{workout.SessionName} · {workout.Date.ToString("ddd dd/MM", Brazil)}"),
            new("Relato", payload.Details is null ? payload.Subject : $"{payload.Subject}: {payload.Details}"),
        ];
        if (report)
        {
            fields.Add(new("Para", workout.Trainer));
        }

        return new PreparedProposal(ProposalKind.BodyCheck, JsonSerializer.Serialize(payload, AssistantMapping.Json), "Relato de desconforto", fields, PainNotice);
    }

    private static PreparedProposal? Set(JsonElement input, AssistantContext context, DateTimeOffset now)
    {
        var workout = context.Training?.CurrentWorkout;
        var exerciseId = Guid(input, "exercise_id") ?? workout?.CurrentExerciseId;
        var exercise = workout?.Exercises.FirstOrDefault(e => e.ExerciseId == exerciseId);
        var setNumber = Int(input, "set_number") ?? workout?.CurrentSetNumber;
        var repetitions = Int(input, "repetitions");
        var load = Decimal(input, "load_kg");
        if (workout is null || exercise is null || setNumber is not (>= 1 and <= 100) || (repetitions is null && load is null))
        {
            return null;
        }

        var payload = new SetLogProposal(workout.WorkoutSessionId, System.Guid.CreateVersion7(now), exercise.ExerciseId, exercise.PlannedExerciseId, setNumber.Value, repetitions, load, now);
        List<ProposalField> fields = [new("Exercício", exercise.Name), new("Série", setNumber.Value.ToString(CultureInfo.InvariantCulture))];
        if (repetitions is { } reps)
        {
            fields.Add(new("Repetições", reps.ToString(CultureInfo.InvariantCulture)));
        }

        if (load is { } kg)
        {
            fields.Add(new("Carga", $"{kg.ToString("0.##", Brazil)} kg"));
        }

        return new PreparedProposal(ProposalKind.SetLog, JsonSerializer.Serialize(payload, AssistantMapping.Json), "Série", fields, null);
    }

    private static string Adherence(MealAdherence adherence) => adherence switch
    {
        MealAdherence.AsPlanned => "Segui o plano",
        MealAdherence.Partly => "Segui em parte",
        MealAdherence.Different => "Comi outra coisa",
        _ => "Fora do plano",
    };

    private static string? String(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Guid? Guid(JsonElement input, string name) => System.Guid.TryParse(String(input, name), out var id) ? id : null;

    private static bool? Bool(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int? Int(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static decimal? Decimal(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : null;
}

internal static class AssistantMapping
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static AssistantConsentResponse? ToResponse(AssistantConsent? consent) =>
        consent is null
            ? null
            : new AssistantConsentResponse(
                consent.IsActiveFor(AssistantTerms.CurrentVersion), consent.TermsVersion, consent.Scopes, consent.AcceptedAt, consent.WithdrawnAt);
}
