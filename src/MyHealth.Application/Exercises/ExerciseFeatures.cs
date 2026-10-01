using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Exercises;

namespace MyHealth.Application.Exercises;

public sealed record ExerciseInput(
    string Name,
    string Description,
    IReadOnlyCollection<string> MuscleGroups,
    IReadOnlyCollection<string> Equipment,
    string? Purpose,
    string? Precautions,
    string? MediaUrl,
    string? MediaCaption)
{
    internal ExerciseDetails ToDetails() =>
        new(Name, Description, MuscleGroups, Equipment, Purpose, Precautions, MediaUrl, MediaCaption);
}

internal sealed class ExerciseInputValidator : AbstractValidator<ExerciseInput>
{
    public ExerciseInputValidator()
    {
        RuleFor(e => e.Name).NotEmpty().MaximumLength(200);
        RuleFor(e => e.Description).NotEmpty().MaximumLength(4000);
        RuleFor(e => e.MuscleGroups).NotNull();
        RuleFor(e => e.Equipment).NotNull();
        RuleFor(e => e.Purpose).MaximumLength(200);
        RuleFor(e => e.Precautions).MaximumLength(4000);
        RuleFor(e => e.MediaUrl).MaximumLength(2048)
            .Must(url => url is null || Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .WithMessage("Media must be an absolute https URL.");
        RuleFor(e => e.MediaCaption).MaximumLength(4000)
            .NotEmpty().When(e => e.MediaUrl is not null).WithMessage("Media needs instructional text or captions.");
    }
}

/// <summary>A verified trainer adds a custom exercise to the active tenant's library (RF-12).</summary>
public sealed record CreateExerciseCommand(ExerciseInput Exercise) : ICommand<Guid>;

internal sealed class CreateExerciseValidator : AbstractValidator<CreateExerciseCommand>
{
    public CreateExerciseValidator() => RuleFor(c => c.Exercise).NotNull().SetValidator(new ExerciseInputValidator());
}

internal sealed class CreateExerciseHandler(ICareAccess access, IExerciseRepository exercises, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CreateExerciseCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateExerciseCommand command, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: true, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var exercise = Exercise.CreateCustom(trainer.Value.TenantId, trainer.Value.Profile.Id, command.Exercise.ToDetails(), clock.GetUtcNow());
        exercises.Add(exercise);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return exercise.Id;
    }
}

public sealed record UpdateExerciseCommand(Guid ExerciseId, ExerciseInput Exercise) : ICommand;

internal sealed class UpdateExerciseValidator : AbstractValidator<UpdateExerciseCommand>
{
    public UpdateExerciseValidator() => RuleFor(c => c.Exercise).NotNull().SetValidator(new ExerciseInputValidator());
}

internal sealed class UpdateExerciseHandler(ICareAccess access, IExerciseRepository exercises, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<UpdateExerciseCommand>
{
    public async Task<Result> Handle(UpdateExerciseCommand command, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: true, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var exercise = await exercises.GetByIdAsync(command.ExerciseId, cancellationToken);
        if (exercise is null || !exercise.IsAvailableIn(trainer.Value.TenantId))
        {
            return ExerciseErrors.NotFound;
        }

        var result = exercise.Update(trainer.Value.Profile.Id, command.Exercise.ToDetails(), clock.GetUtcNow());
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

public sealed record ExerciseResponse(
    Guid Id,
    string Name,
    string Description,
    IReadOnlyList<string> MuscleGroups,
    IReadOnlyList<string> Equipment,
    string? Purpose,
    string? Precautions,
    string? MediaUrl,
    string? MediaCaption,
    int Version,
    bool IsCustom);

public sealed record SearchExercisesQuery(string? Search, string? MuscleGroup, string? Equipment, string? Purpose, int Page = 1, int PageSize = 50)
    : IQuery<PagedResponse<ExerciseResponse>>;

internal sealed class SearchExercisesValidator : AbstractValidator<SearchExercisesQuery>
{
    public SearchExercisesValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}

/// <summary>Searches the curated library plus the selected tenant's own exercises (when a tenant is selected).</summary>
internal sealed class SearchExercisesHandler(IReadDbContext db, ITenantContext tenant)
    : IQueryHandler<SearchExercisesQuery, PagedResponse<ExerciseResponse>>
{
    public async Task<Result<PagedResponse<ExerciseResponse>>> Handle(SearchExercisesQuery query, CancellationToken cancellationToken)
    {
        var membership = await tenant.FindAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        var tenantId = membership.Value?.TenantId;
        var exercises = db.Exercises.Where(e => !e.IsArchived && (e.TenantId == null || e.TenantId == tenantId));

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            exercises = exercises.Where(e => e.Name.ToLower().Contains(term) || e.Description.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(query.MuscleGroup))
        {
            var muscle = query.MuscleGroup.Trim().ToLowerInvariant();
            exercises = exercises.Where(e => e.MuscleGroups.Contains(muscle));
        }

        if (!string.IsNullOrWhiteSpace(query.Equipment))
        {
            var equipment = query.Equipment.Trim().ToLowerInvariant();
            exercises = exercises.Where(e => e.Equipment.Contains(equipment));
        }

        if (!string.IsNullOrWhiteSpace(query.Purpose))
        {
            var purpose = query.Purpose.Trim().ToLowerInvariant();
            exercises = exercises.Where(e => e.Purpose != null && e.Purpose.ToLower().Contains(purpose));
        }

        var total = await exercises.CountAsync(cancellationToken);
        var items = await exercises
            .OrderBy(e => e.Name)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(e => new ExerciseResponse(
                e.Id, e.Name, e.Description, e.MuscleGroups, e.Equipment, e.Purpose, e.Precautions, e.MediaUrl, e.MediaCaption,
                e.Version, e.OwnerProfileId != null))
            .ToListAsync(cancellationToken);

        return new PagedResponse<ExerciseResponse>(items, query.Page, query.PageSize, total);
    }
}

public sealed record GetExerciseQuery(Guid ExerciseId) : IQuery<ExerciseResponse>;

/// <summary>
/// Curated exercises are public. A tenant's own exercise is readable by that tenant's members (with the tenant selected)
/// and by clients with an active relationship in that tenant, who need it to perform their workouts.
/// </summary>
internal sealed class GetExerciseHandler(IReadDbContext db, IUserContext user, ITenantContext tenant) : IQueryHandler<GetExerciseQuery, ExerciseResponse>
{
    public async Task<Result<ExerciseResponse>> Handle(GetExerciseQuery query, CancellationToken cancellationToken)
    {
        var membership = await tenant.FindAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        var exercise = await db.Exercises.FirstOrDefaultAsync(e => e.Id == query.ExerciseId, cancellationToken);
        if (exercise is null)
        {
            return ExerciseErrors.NotFound;
        }

        var allowed = exercise.TenantId is not { } owner
                      || owner == membership.Value?.TenantId
                      || await db.CareRelationships.AnyAsync(
                          r => r.TenantId == owner && r.ClientId == user.UserId && r.Status == RelationshipStatus.Active, cancellationToken);

        return allowed
            ? new ExerciseResponse(
                exercise.Id, exercise.Name, exercise.Description, exercise.MuscleGroups, exercise.Equipment, exercise.Purpose, exercise.Precautions,
                exercise.MediaUrl, exercise.MediaCaption, exercise.Version, exercise.OwnerProfileId != null)
            : ExerciseErrors.NotFound;
    }
}
