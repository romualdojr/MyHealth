using FluentValidation;
using MyHealth.Application.Abstractions.Administration;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Exercises;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Exercises;

namespace MyHealth.Application.Administration;

/// <summary>Who holds the platform-operator role (RF-50 "manage permissions").</summary>
public sealed record ListAdministratorsQuery : IQuery<IReadOnlyList<AdministratorItem>>;

internal sealed class ListAdministratorsHandler(IAdministratorDirectory directory) : IQueryHandler<ListAdministratorsQuery, IReadOnlyList<AdministratorItem>>
{
    public async Task<Result<IReadOnlyList<AdministratorItem>>> Handle(ListAdministratorsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await directory.ListAsync(cancellationToken));
}

public sealed record ChangeAdministratorCommand(Guid UserId, bool Grant) : ICommand;

internal sealed class ChangeAdministratorHandler(IUserContext user, IAdministratorDirectory directory, IAuditLog audit, IUnitOfWork unitOfWork)
    : ICommandHandler<ChangeAdministratorCommand>
{
    public async Task<Result> Handle(ChangeAdministratorCommand command, CancellationToken cancellationToken)
    {
        if (!command.Grant && command.UserId == user.UserId)
        {
            return AdministrationErrors.CannotRevokeSelf;
        }

        var result = command.Grant
            ? await directory.GrantAsync(command.UserId, cancellationToken)
            : await directory.RevokeAsync(command.UserId, cancellationToken);
        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(command.Grant ? AuditActions.Grant : AuditActions.Revoke, "Role.Administrator", command.UserId, command.UserId, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

/// <summary>Suspends a professional's credentials, e.g. when the council registration lapses; they can no longer provide care.</summary>
public sealed record SuspendProfessionalCommand(Guid ProfileId, string Reason) : ICommand;

internal sealed class SuspendProfessionalValidator : AbstractValidator<SuspendProfessionalCommand>
{
    public SuspendProfessionalValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(1000);
}

internal sealed class SuspendProfessionalHandler(
    IUserContext user, IProfessionalProfileRepository professionals, IAuditLog audit, IUnitOfWork unitOfWork)
    : ICommandHandler<SuspendProfessionalCommand>
{
    public async Task<Result> Handle(SuspendProfessionalCommand command, CancellationToken cancellationToken)
    {
        var profile = await professionals.GetByIdAsync(command.ProfileId, cancellationToken);
        if (profile is null)
        {
            return ProfessionalErrors.NotFound;
        }

        var result = profile.Suspend(user.UserId, command.Reason);
        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(AuditActions.Suspend, nameof(ProfessionalProfile), profile.Id, profile.UserId, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

/// <summary>Platform operators add to the curated exercise library available to every tenant (RF-50 "manage catalogs").</summary>
public sealed record CreateSystemExerciseCommand(ExerciseInput Exercise) : ICommand<Guid>;

internal sealed class CreateSystemExerciseValidator : AbstractValidator<CreateSystemExerciseCommand>
{
    public CreateSystemExerciseValidator() => RuleFor(c => c.Exercise).NotNull().SetValidator(new ExerciseInputValidator());
}

internal sealed class CreateSystemExerciseHandler(IExerciseRepository exercises, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CreateSystemExerciseCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateSystemExerciseCommand command, CancellationToken cancellationToken)
    {
        var exercise = Exercise.CreateSystem(command.Exercise.ToDetails(), clock.GetUtcNow());
        exercises.Add(exercise);
        audit.Record(AuditActions.Create, nameof(Exercise), exercise.Id, subjectUserId: null, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return exercise.Id;
    }
}

public sealed record UpdateSystemExerciseCommand(Guid ExerciseId, ExerciseInput Exercise) : ICommand;

internal sealed class UpdateSystemExerciseValidator : AbstractValidator<UpdateSystemExerciseCommand>
{
    public UpdateSystemExerciseValidator() => RuleFor(c => c.Exercise).NotNull().SetValidator(new ExerciseInputValidator());
}

internal sealed class UpdateSystemExerciseHandler(IExerciseRepository exercises, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<UpdateSystemExerciseCommand>
{
    public async Task<Result> Handle(UpdateSystemExerciseCommand command, CancellationToken cancellationToken)
    {
        var exercise = await exercises.GetByIdAsync(command.ExerciseId, cancellationToken);
        if (exercise is null || !exercise.IsSystem)
        {
            return ExerciseErrors.NotFound;
        }

        var result = exercise.UpdateSystem(command.Exercise.ToDetails(), clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Update, nameof(Exercise), exercise.Id, subjectUserId: null, tenantId: null);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>Hides (or restores) a curated entry for new prescriptions; existing plans keep their snapshot.</summary>
public sealed record ArchiveSystemExerciseCommand(Guid ExerciseId, bool Archived) : ICommand;

internal sealed class ArchiveSystemExerciseHandler(IExerciseRepository exercises, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ArchiveSystemExerciseCommand>
{
    public async Task<Result> Handle(ArchiveSystemExerciseCommand command, CancellationToken cancellationToken)
    {
        var exercise = await exercises.GetByIdAsync(command.ExerciseId, cancellationToken);
        if (exercise is null || !exercise.IsSystem)
        {
            return ExerciseErrors.NotFound;
        }

        var result = exercise.SetArchived(command.Archived, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Archive, nameof(Exercise), exercise.Id, subjectUserId: null, tenantId: null);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}
