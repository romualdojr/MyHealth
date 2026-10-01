using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Training.Plans;

public enum SessionKind
{
    Strength = 1,
    Aerobic = 2,
}

public enum TrainingLevel
{
    Beginner = 1,
    Intermediate = 2,
    Advanced = 3,
}

/// <summary>One prescribed set. Either a repetition range or a duration target is required.</summary>
public sealed record SetTarget(int Order, int? MinRepetitions, int? MaxRepetitions, int? DurationSeconds, decimal? SuggestedLoadKg);

/// <summary>An exercise inside a session, with a snapshot of the library entry it was prescribed from.</summary>
public sealed record PlannedExercise(
    Guid Id,
    Guid ExerciseId,
    string ExerciseName,
    int ExerciseVersion,
    int Order,
    string? Instructions,
    int RestSeconds,
    IReadOnlyList<Guid> AlternativeExerciseIds,
    IReadOnlyList<SetTarget> Sets);

public sealed record AerobicTarget(
    string Activity,
    int? DurationMinutes,
    decimal? DistanceKm,
    string? Intensity,
    string? WarmUp,
    string? CoolDown);

/// <summary>A workout within a plan. Its id is stable across plan versions so progress can be followed.</summary>
public sealed record PlannedSession(
    Guid Id,
    string Name,
    SessionKind Kind,
    int Order,
    string? Notes,
    IReadOnlyList<PlannedExercise> Exercises,
    AerobicTarget? Aerobic)
{
    public IEnumerable<Guid> AllowedExerciseIds() =>
        Exercises.SelectMany(e => e.AlternativeExerciseIds.Prepend(e.ExerciseId)).Distinct();
}

/// <summary>The ordered sessions of a plan. Immutable: drafts are replaced, published content is never changed.</summary>
public sealed record PlanContent(IReadOnlyList<PlannedSession> Sessions)
{
    public static readonly PlanContent Empty = new([]);

    public PlannedSession? FindSession(Guid plannedSessionId) => Sessions.FirstOrDefault(s => s.Id == plannedSessionId);

    /// <summary>Structural rules that hold for drafts as well as published content.</summary>
    public Result ValidateStructure()
    {
        if (Sessions.Select(s => s.Id).Distinct().Count() != Sessions.Count)
        {
            return PlanErrors.DuplicateIds("session");
        }

        var exercises = Sessions.SelectMany(s => s.Exercises).ToList();
        if (exercises.Select(e => e.Id).Distinct().Count() != exercises.Count)
        {
            return PlanErrors.DuplicateIds("planned exercise");
        }

        foreach (var set in exercises.SelectMany(e => e.Sets))
        {
            if (set.MinRepetitions is null && set.DurationSeconds is null)
            {
                return PlanErrors.SetWithoutTarget;
            }

            if (set.MinRepetitions > set.MaxRepetitions)
            {
                return PlanErrors.InvalidRepetitionRange;
            }
        }

        return Result.Success();
    }

    /// <summary>Completeness rules required before a client can perform the plan.</summary>
    public Result ValidateForPublishing()
    {
        var structure = ValidateStructure();
        if (structure.IsFailure)
        {
            return structure;
        }

        if (Sessions.Count == 0)
        {
            return PlanErrors.NoSessions;
        }

        foreach (var session in Sessions)
        {
            var complete = session.Kind switch
            {
                SessionKind.Strength => session.Exercises.Count > 0 && session.Exercises.All(e => e.Sets.Count > 0),
                SessionKind.Aerobic => session.Aerobic is not null,
                _ => false,
            };

            if (!complete)
            {
                return PlanErrors.IncompleteSession(session.Name);
            }
        }

        return Result.Success();
    }
}
