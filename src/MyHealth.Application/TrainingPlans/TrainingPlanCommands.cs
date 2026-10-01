using FluentValidation;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.Plans;

namespace MyHealth.Application.TrainingPlans;

/// <summary>Creates a draft in the active tenant for a client the trainer is linked to in that tenant.</summary>
public sealed record CreateTrainingPlanCommand(Guid ClientId, PlanDetailsInput Details) : ICommand<Guid>;

internal sealed class CreateTrainingPlanValidator : AbstractValidator<CreateTrainingPlanCommand>
{
    public CreateTrainingPlanValidator()
    {
        RuleFor(c => c.ClientId).NotEmpty();
        RuleFor(c => c.Details).NotNull().SetValidator(new PlanDetailsInputValidator());
    }
}

internal sealed class CreateTrainingPlanHandler(
    ICareAccess access, ITrainingPlanRepository plans, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CreateTrainingPlanCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateTrainingPlanCommand command, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: true, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var relationship = await access.GetActiveRelationshipAsync(trainer.Value, command.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var plan = TrainingPlan.Create(trainer.Value.TenantId, trainer.Value.Profile, command.ClientId, command.Details.ToDetails(), clock.GetUtcNow());
        if (plan.IsFailure)
        {
            return plan.Error;
        }

        plans.Add(plan.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return plan.Value.Id;
    }
}

public sealed record UpdateTrainingPlanDetailsCommand(Guid PlanId, PlanDetailsInput Details) : ICommand;

internal sealed class UpdateTrainingPlanDetailsValidator : AbstractValidator<UpdateTrainingPlanDetailsCommand>
{
    public UpdateTrainingPlanDetailsValidator() => RuleFor(c => c.Details).NotNull().SetValidator(new PlanDetailsInputValidator());
}

internal sealed class UpdateTrainingPlanDetailsHandler(
    ICareAccess access, ITrainingPlanRepository plans, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<UpdateTrainingPlanDetailsCommand>
{
    public async Task<Result> Handle(UpdateTrainingPlanDetailsCommand command, CancellationToken cancellationToken)
    {
        var plan = await TrainerPlanLoader.LoadOwnedAsync(access, plans, audit, command.PlanId, cancellationToken);
        if (plan.IsFailure)
        {
            return plan.Error;
        }

        var result = plan.Value.Plan.UpdateDetails(command.Details.ToDetails(), clock.GetUtcNow());
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>Replaces the draft sessions. Exercise names and versions are snapshotted from the tenant's library.</summary>
public sealed record UpdateTrainingPlanSessionsCommand(Guid PlanId, IReadOnlyList<PlannedSessionInput> Sessions) : ICommand;

internal sealed class UpdateTrainingPlanSessionsValidator : AbstractValidator<UpdateTrainingPlanSessionsCommand>
{
    public UpdateTrainingPlanSessionsValidator()
    {
        RuleFor(c => c.Sessions).NotNull().Must(s => s.Count <= 50).WithMessage("A plan can have at most 50 sessions.");
        RuleForEach(c => c.Sessions).SetValidator(new PlannedSessionInputValidator());
    }
}

internal sealed class UpdateTrainingPlanSessionsHandler(
    ICareAccess access, ITrainingPlanRepository plans, IExerciseRepository exercises, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<UpdateTrainingPlanSessionsCommand>
{
    public async Task<Result> Handle(UpdateTrainingPlanSessionsCommand command, CancellationToken cancellationToken)
    {
        var loaded = await TrainerPlanLoader.LoadOwnedAsync(access, plans, audit, command.PlanId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (_, plan) = loaded.Value;
        var now = clock.GetUtcNow();

        var referencedIds = command.Sessions
            .SelectMany(s => s.Exercises)
            .SelectMany(e => (e.AlternativeExerciseIds ?? []).Prepend(e.ExerciseId))
            .Distinct()
            .ToList();

        // Another tenant's exercises are reported as unknown, exactly like ids that do not exist.
        var library = (await exercises.GetByIdsAsync(referencedIds, cancellationToken))
            .Where(e => !e.IsArchived && e.IsAvailableIn(plan.TenantId))
            .ToDictionary(e => e.Id);

        var unknown = referencedIds.Where(id => !library.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
        {
            return ExerciseErrors.Unknown(unknown);
        }

        var content = new PlanContent(command.Sessions.Select((session, sessionIndex) => new PlannedSession(
            session.Id ?? Guid.CreateVersion7(now),
            session.Name.Trim(),
            session.Kind,
            sessionIndex + 1,
            session.Notes,
            session.Exercises.Select((exercise, exerciseIndex) => new PlannedExercise(
                exercise.Id ?? Guid.CreateVersion7(now),
                exercise.ExerciseId,
                library[exercise.ExerciseId].Name,
                library[exercise.ExerciseId].Version,
                exerciseIndex + 1,
                exercise.Instructions,
                exercise.RestSeconds,
                exercise.AlternativeExerciseIds ?? [],
                exercise.Sets.Select((set, setIndex) => new SetTarget(
                    setIndex + 1, set.MinRepetitions, set.MaxRepetitions ?? set.MinRepetitions, set.DurationSeconds, set.SuggestedLoadKg)).ToList()))
                .ToList(),
            session.Aerobic)).ToList());

        var result = plan.UpdateDraft(content, now);
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

public sealed record PublishedVersionResponse(Guid VersionId, int VersionNumber, DateTimeOffset PublishedAt);

/// <summary>Freezes the draft into a new immutable version that the client will perform next (RF-13).</summary>
public sealed record PublishTrainingPlanCommand(Guid PlanId, string ChangeDescription) : ICommand<PublishedVersionResponse>;

internal sealed class PublishTrainingPlanValidator : AbstractValidator<PublishTrainingPlanCommand>
{
    public PublishTrainingPlanValidator() => RuleFor(c => c.ChangeDescription).NotEmpty().MaximumLength(1000);
}

internal sealed class PublishTrainingPlanHandler(
    ICareAccess access, ITrainingPlanRepository plans, ITrainingPlanVersionRepository versions, IAuditLog audit,
    IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<PublishTrainingPlanCommand, PublishedVersionResponse>
{
    public async Task<Result<PublishedVersionResponse>> Handle(PublishTrainingPlanCommand command, CancellationToken cancellationToken)
    {
        var loaded = await TrainerPlanLoader.LoadOwnedAsync(access, plans, audit, command.PlanId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (trainer, plan) = loaded.Value;
        var relationship = await access.GetActiveRelationshipAsync(trainer, plan.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var version = plan.Publish(trainer.Profile, command.ChangeDescription, clock.GetUtcNow());
        if (version.IsFailure)
        {
            return version.Error;
        }

        versions.Add(version.Value);
        audit.Record(AuditActions.Publish, nameof(TrainingPlanVersion), version.Value.Id, plan.ClientId, plan.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new PublishedVersionResponse(version.Value.Id, version.Value.VersionNumber, version.Value.PublishedAt);
    }
}

public sealed record EndTrainingPlanCommand(Guid PlanId) : ICommand;

internal sealed class EndTrainingPlanHandler(
    ICareAccess access, ITrainingPlanRepository plans, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<EndTrainingPlanCommand>
{
    public async Task<Result> Handle(EndTrainingPlanCommand command, CancellationToken cancellationToken)
    {
        var loaded = await TrainerPlanLoader.LoadOwnedAsync(access, plans, audit, command.PlanId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var result = loaded.Value.Plan.End(clock.GetUtcNow());
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>
/// The trainer confirms the plan is still appropriate after a flagged change (e.g. a new injury in the health history),
/// without publishing a new version. Publishing a new version also clears the flag.
/// </summary>
public sealed record AcknowledgeTrainingPlanReviewCommand(Guid PlanId) : ICommand;

internal sealed class AcknowledgeTrainingPlanReviewHandler(
    ICareAccess access, ITrainingPlanRepository plans, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<AcknowledgeTrainingPlanReviewCommand>
{
    public async Task<Result> Handle(AcknowledgeTrainingPlanReviewCommand command, CancellationToken cancellationToken)
    {
        var loaded = await TrainerPlanLoader.LoadOwnedAsync(access, plans, audit, command.PlanId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var plan = loaded.Value.Plan;
        var result = plan.AcknowledgeReview(clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Review, nameof(TrainingPlan), plan.Id, plan.ClientId, plan.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>
/// Copies a plan as a new draft for the same or another client of the same tenant (RF-10). History of the source is
/// untouched, and a plan can never be copied into another organization.
/// </summary>
public sealed record CopyTrainingPlanCommand(Guid PlanId, Guid TargetClientId, string? NewName) : ICommand<Guid>;

internal sealed class CopyTrainingPlanValidator : AbstractValidator<CopyTrainingPlanCommand>
{
    public CopyTrainingPlanValidator()
    {
        RuleFor(c => c.TargetClientId).NotEmpty();
        RuleFor(c => c.NewName).MaximumLength(200);
    }
}

internal sealed class CopyTrainingPlanHandler(
    ICareAccess access, ITrainingPlanRepository plans, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CopyTrainingPlanCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CopyTrainingPlanCommand command, CancellationToken cancellationToken)
    {
        var loaded = await TrainerPlanLoader.LoadOwnedAsync(access, plans, audit, command.PlanId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var (trainer, plan) = loaded.Value;
        var relationship = await access.GetActiveRelationshipAsync(trainer, command.TargetClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var copy = plan.CopyAsDraft(trainer.Profile, command.TargetClientId, command.NewName, clock.GetUtcNow());
        if (copy.IsFailure)
        {
            return copy.Error;
        }

        plans.Add(copy.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return copy.Value.Id;
    }
}

internal static class TrainerPlanLoader
{
    /// <summary>
    /// Loads a plan only if it belongs to the calling trainer in the active tenant; otherwise reports it as not found.
    /// A known id outside that scope is audited as a denied attempt (MT-09).
    /// </summary>
    public static async Task<Result<(ActingProfessional Trainer, TrainingPlan Plan)>> LoadOwnedAsync(
        ICareAccess access, ITrainingPlanRepository plans, IAuditLog audit, Guid planId, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: true, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var plan = await plans.GetByIdAsync(planId, cancellationToken);
        if (plan is null || !plan.IsOwnedBy(trainer.Value.Profile.Id) || plan.TenantId != trainer.Value.TenantId)
        {
            if (plan is not null)
            {
                await audit.RecordDeniedAsync(nameof(TrainingPlan), planId, trainer.Value.TenantId, cancellationToken);
            }

            return PlanErrors.NotFound;
        }

        return (trainer.Value, plan);
    }
}
