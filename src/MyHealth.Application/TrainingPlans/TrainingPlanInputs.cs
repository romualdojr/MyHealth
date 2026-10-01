using FluentValidation;
using MyHealth.Domain.Training.Plans;

namespace MyHealth.Application.TrainingPlans;

public sealed record PlanDetailsInput(
    string Name,
    string Goal,
    TrainingLevel Level,
    DateOnly StartDate,
    DateOnly? EndDate,
    int SuggestedSessionsPerWeek,
    string? Notes)
{
    internal PlanDetails ToDetails() => new(Name.Trim(), Goal.Trim(), Level, StartDate, EndDate, SuggestedSessionsPerWeek, Notes);
}

public sealed record SetTargetInput(int? MinRepetitions, int? MaxRepetitions, int? DurationSeconds, decimal? SuggestedLoadKg);

/// <summary>Send back the existing <paramref name="Id"/> when editing so progress stays linked across versions.</summary>
public sealed record PlannedExerciseInput(
    Guid? Id,
    Guid ExerciseId,
    string? Instructions,
    int RestSeconds,
    IReadOnlyList<Guid>? AlternativeExerciseIds,
    IReadOnlyList<SetTargetInput> Sets);

public sealed record PlannedSessionInput(
    Guid? Id,
    string Name,
    SessionKind Kind,
    string? Notes,
    IReadOnlyList<PlannedExerciseInput> Exercises,
    AerobicTarget? Aerobic);

internal sealed class PlanDetailsInputValidator : AbstractValidator<PlanDetailsInput>
{
    public PlanDetailsInputValidator()
    {
        RuleFor(d => d.Name).NotEmpty().MaximumLength(200);
        RuleFor(d => d.Goal).NotEmpty().MaximumLength(500);
        RuleFor(d => d.Level).IsInEnum();
        RuleFor(d => d.SuggestedSessionsPerWeek).InclusiveBetween(1, 14);
        RuleFor(d => d.EndDate).GreaterThanOrEqualTo(d => d.StartDate).When(d => d.EndDate is not null);
        RuleFor(d => d.Notes).MaximumLength(4000);
    }
}

internal sealed class PlannedSessionInputValidator : AbstractValidator<PlannedSessionInput>
{
    public PlannedSessionInputValidator()
    {
        RuleFor(s => s.Name).NotEmpty().MaximumLength(200);
        RuleFor(s => s.Kind).IsInEnum();
        RuleFor(s => s.Notes).MaximumLength(4000);
        RuleFor(s => s.Exercises).NotNull();
        RuleForEach(s => s.Exercises).ChildRules(exercise =>
        {
            exercise.RuleFor(e => e.ExerciseId).NotEmpty();
            exercise.RuleFor(e => e.RestSeconds).InclusiveBetween(0, 3600);
            exercise.RuleFor(e => e.Instructions).MaximumLength(4000);
            exercise.RuleFor(e => e.Sets).NotNull();
            exercise.RuleForEach(e => e.Sets).ChildRules(set =>
            {
                set.RuleFor(t => t.MinRepetitions).InclusiveBetween(1, 1000);
                set.RuleFor(t => t.MaxRepetitions).InclusiveBetween(1, 1000);
                set.RuleFor(t => t.DurationSeconds).InclusiveBetween(1, 86_400);
                set.RuleFor(t => t.SuggestedLoadKg).InclusiveBetween(0, 1000);
            });
        });
        RuleFor(s => s.Aerobic!.Activity).NotEmpty().MaximumLength(200).When(s => s.Aerobic is not null);
    }
}
