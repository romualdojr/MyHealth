using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Coaching;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Common;
using MyHealth.Application.TrainingPlans;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Coaching;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.Plans;

namespace MyHealth.Application.Coaching;

/// <summary>
/// Limits of the AI coach and the trainer chat per organization and day, set by the host
/// (<c>Coach:DailyAnalysesPerTenant</c>, <c>Coach:DailyChatMessagesPerTenant</c>). Each one is a call to the AI provider.
/// </summary>
public sealed record CoachSettings(int DailyAnalysesPerTenant, int DailyChatMessagesPerTenant = 300);

/// <summary>A suggestion as the trainer sees it: <see cref="Title"/> and <see cref="Change"/> are display texts set by the server.</summary>
public sealed record CoachSuggestionResponse(
    Guid Id,
    CoachSuggestionKind Kind,
    Guid? PlannedExerciseId,
    string? ExerciseName,
    string Title,
    string? Change,
    string Reason,
    decimal? LoadKg,
    int? Sets,
    int? MinRepetitions,
    int? MaxRepetitions,
    int? RestSeconds,
    Guid? ReplacementExerciseId,
    string? Instruction,
    CoachSuggestionStatus Status,
    DateTimeOffset? ResolvedAt);

public sealed record CoachAnalysisResponse(
    Guid Id, Guid PlanId, int PlanVersionNumber, string Model, string? Focus, string Summary, IReadOnlyList<string> Notices,
    IReadOnlyList<CoachSuggestionResponse> Suggestions, DateTimeOffset CreatedAt);

/// <summary>Whether the coach can run for this plan's client, today's usage, and the latest analyses.</summary>
public sealed record CoachOverviewResponse(bool SharingGranted, int DailyLimit, int UsedToday, IReadOnlyList<CoachAnalysisResponse> Analyses);

public sealed record GetCoachOverviewQuery(Guid PlanId) : IQuery<CoachOverviewResponse>;

internal sealed class GetCoachOverviewHandler(IReadDbContext db, ICareAccess access, CoachSettings settings, TimeProvider clock)
    : IQueryHandler<GetCoachOverviewQuery, CoachOverviewResponse>
{
    public async Task<Result<CoachOverviewResponse>> Handle(GetCoachOverviewQuery query, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: false, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var plan = await db.TrainingPlans.FirstOrDefaultAsync(
            p => p.Id == query.PlanId && p.TrainerProfileId == trainer.Value.Profile.Id && p.TenantId == trainer.Value.TenantId, cancellationToken);
        if (plan is null)
        {
            return PlanErrors.NotFound;
        }

        var relationship = await access.GetActiveRelationshipAsync(trainer.Value, plan.ClientId, cancellationToken);
        var granted = relationship.IsSuccess && relationship.Value.HasActiveGrant(DataCategory.AiCoaching);

        var since = clock.GetUtcNow().AddDays(-1);
        var used = await db.CoachAnalyses.CountAsync(a => a.TenantId == trainer.Value.TenantId && a.CreatedAt >= since, cancellationToken);
        var analyses = await db.CoachAnalyses
            .Where(a => a.TrainingPlanId == plan.Id && a.TenantId == trainer.Value.TenantId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(5)
            .ToListAsync(cancellationToken);

        return new CoachOverviewResponse(granted, settings.DailyAnalysesPerTenant, used, [.. analyses.Select(CoachMapping.ToResponse)]);
    }
}

/// <summary>
/// The trainer asks the AI coach to review how the client is responding to this plan. Needs the client's
/// <see cref="DataCategory.AiCoaching"/> grant. Suggestions are stored for the trainer to apply or dismiss; nothing
/// changes in the plan here.
/// </summary>
public sealed record RequestCoachAnalysisCommand(Guid PlanId, string? Focus) : ICommand<CoachAnalysisResponse>;

internal sealed class RequestCoachAnalysisValidator : AbstractValidator<RequestCoachAnalysisCommand>
{
    public RequestCoachAnalysisValidator() => RuleFor(c => c.Focus).MaximumLength(500);
}

internal sealed class RequestCoachAnalysisHandler(
    ICareAccess access, ITrainingPlanRepository plans, ICoachAnalysisRepository analyses, CoachContextBuilder contexts, ITrainingCoachModel model,
    CoachSettings settings, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<RequestCoachAnalysisCommand, CoachAnalysisResponse>
{
    private const int MaxSummaryLength = 2000;

    public async Task<Result<CoachAnalysisResponse>> Handle(RequestCoachAnalysisCommand command, CancellationToken cancellationToken)
    {
        var loaded = await TrainerPlanLoader.LoadOwnedAsync(access, plans, audit, command.PlanId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (trainer, plan) = loaded.Value;
        if (plan.Status == PlanStatus.Ended)
        {
            return PlanErrors.Ended;
        }

        var relationship = await access.GetActiveRelationshipAsync(trainer, plan.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        if (!relationship.Value.HasActiveGrant(DataCategory.AiCoaching))
        {
            return CoachErrors.SharingRequired;
        }

        var now = clock.GetUtcNow();
        if (await analyses.CountForTenantSinceAsync(trainer.TenantId, now.AddDays(-1), cancellationToken) >= settings.DailyAnalysesPerTenant)
        {
            return CoachErrors.DailyLimitReached;
        }

        var focus = string.IsNullOrWhiteSpace(command.Focus) ? null : command.Focus.Trim();
        var context = await contexts.BuildAsync(plan, relationship.Value.HasActiveGrant(DataCategory.Wellbeing), cancellationToken);
        var request = new CoachModelRequest(CoachTools.SystemPrompt, context, JsonSerializer.Serialize(context, CoachMapping.Json), focus, CoachTools.All);

        var reply = await model.AnalyzeAsync(request, cancellationToken);
        if (reply is null)
        {
            return CoachErrors.Unavailable;
        }

        var outcome = CoachSuggestionPolicy.Apply(reply.ToolCalls, context, now);
        var summary = reply.Summary.Trim() switch
        {
            "" => outcome.Suggestions.Count > 0 ? "Veja as sugestões abaixo." : "Nenhum ajuste sugerido no momento.",
            var text when text.Length > MaxSummaryLength => text[..MaxSummaryLength],
            var text => text,
        };

        var analysis = CoachAnalysis.Create(plan, model.Name, focus, summary, outcome.Notices, outcome.Suggestions, now);
        analyses.Add(analysis);

        // The client's records reached the AI provider: a disclosure of personal data, audited without content.
        audit.Record(AuditActions.Assist, nameof(CoachAnalysis), analysis.Id, plan.ClientId, plan.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return CoachMapping.ToResponse(analysis);
    }
}

/// <summary>The trainer applies a suggestion to the plan's draft (still to be published) or dismisses it.</summary>
public sealed record ResolveCoachSuggestionCommand(Guid AnalysisId, Guid SuggestionId, bool Apply) : ICommand<CoachAnalysisResponse>;

internal sealed class ResolveCoachSuggestionHandler(
    ICareAccess access, ITrainingPlanRepository plans, ICoachAnalysisRepository analyses, IExerciseRepository exercises, IAuditLog audit,
    IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ResolveCoachSuggestionCommand, CoachAnalysisResponse>
{
    public async Task<Result<CoachAnalysisResponse>> Handle(ResolveCoachSuggestionCommand command, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: true, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var analysis = await analyses.GetByIdAsync(command.AnalysisId, cancellationToken);
        if (analysis is null || analysis.TenantId != trainer.Value.TenantId || analysis.TrainerProfileId != trainer.Value.Profile.Id)
        {
            if (analysis is not null)
            {
                await audit.RecordDeniedAsync(nameof(CoachAnalysis), analysis.Id, trainer.Value.TenantId, cancellationToken);
            }

            return CoachErrors.AnalysisNotFound;
        }

        var now = clock.GetUtcNow();
        if (!command.Apply)
        {
            var dismissed = analysis.Dismiss(command.SuggestionId, now);
            if (dismissed.IsFailure)
            {
                return dismissed.Error;
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return CoachMapping.ToResponse(analysis);
        }

        var loaded = await TrainerPlanLoader.LoadOwnedAsync(access, plans, audit, analysis.TrainingPlanId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var plan = loaded.Value.Plan;
        Exercise? replacement = null;
        if (analysis.FindSuggestion(command.SuggestionId) is { Kind: CoachSuggestionKind.SwapExercise, ReplacementExerciseId: { } replacementId })
        {
            replacement = await exercises.GetByIdAsync(replacementId, cancellationToken);
            if (replacement is null || replacement.IsArchived || !replacement.IsAvailableIn(plan.TenantId))
            {
                return CoachErrors.InvalidSuggestion;
            }
        }

        var applied = analysis.Apply(command.SuggestionId, plan, replacement, now);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        audit.Record(AuditActions.Update, nameof(TrainingPlan), plan.Id, plan.ClientId, plan.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return CoachMapping.ToResponse(analysis);
    }
}

internal static class CoachMapping
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static CoachAnalysisResponse ToResponse(CoachAnalysis analysis) =>
        new(analysis.Id, analysis.TrainingPlanId, analysis.PlanVersionNumber, analysis.Model, analysis.Focus, analysis.Summary, analysis.Notices,
            [.. analysis.Suggestions.Select(ToResponse)], analysis.CreatedAt);

    public static CoachSuggestionResponse ToResponse(CoachSuggestion s) =>
        new(s.Id, s.Kind, s.PlannedExerciseId, s.ExerciseName, Title(s), Change(s), s.Reason, s.LoadKg, s.Sets, s.MinRepetitions, s.MaxRepetitions,
            s.RestSeconds, s.ReplacementExerciseId, s.Instruction, s.Status, s.ResolvedAt);

    private static string Title(CoachSuggestion s) => s.Kind switch
    {
        CoachSuggestionKind.ChangeLoad => $"Ajustar carga — {s.ExerciseName}",
        CoachSuggestionKind.ChangeSets => $"Ajustar número de séries — {s.ExerciseName}",
        CoachSuggestionKind.ChangeRepetitions => $"Ajustar repetições — {s.ExerciseName}",
        CoachSuggestionKind.ChangeRest => $"Ajustar descanso — {s.ExerciseName}",
        CoachSuggestionKind.SwapExercise => $"Trocar exercício — {s.ExerciseName}",
        CoachSuggestionKind.AddInstruction => $"Orientação de execução — {s.ExerciseName}",
        _ => s.ExerciseName is null ? "Conversar com o cliente" : $"Conversar com o cliente — {s.ExerciseName}",
    };

    private static string? Change(CoachSuggestion s) => s.Kind switch
    {
        CoachSuggestionKind.ChangeLoad => $"Nova carga: {s.LoadKg!.Value.ToString("0.#", Brazil)} kg em todas as séries",
        CoachSuggestionKind.ChangeSets => $"{s.Sets} séries",
        CoachSuggestionKind.ChangeRepetitions => s.MinRepetitions == s.MaxRepetitions ? $"{s.MinRepetitions} repetições" : $"{s.MinRepetitions} a {s.MaxRepetitions} repetições",
        CoachSuggestionKind.ChangeRest => $"Descanso de {s.RestSeconds} s",
        CoachSuggestionKind.SwapExercise => $"Substituir por {s.ReplacementExerciseName}",
        CoachSuggestionKind.AddInstruction => $"“{s.Instruction}”",
        _ => null,
    };
}
