using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Training.Workouts;

public sealed record SetLogEntry(
    Guid Id,
    Guid ExerciseId,
    Guid? PlannedExerciseId,
    int SetNumber,
    int? Repetitions,
    decimal? LoadKg,
    int? DurationSeconds,
    decimal? DistanceMeters,
    int? Difficulty,
    string? Notes,
    DateTimeOffset LoggedAt);

/// <summary>The measured part of a set: what a correction after the workout may change.</summary>
public sealed record SetValues(int? Repetitions, decimal? LoadKg, int? DurationSeconds, decimal? DistanceMeters, int? Difficulty, string? Notes)
{
    public bool IsEmpty => Repetitions is null && DurationSeconds is null && DistanceMeters is null;
}

/// <summary>What a set said before a correction made after the workout ended, why it changed and who changed it (§5 History).</summary>
public sealed record SetLogCorrection(SetValues Previous, string Reason, Guid CorrectedBy, DateTimeOffset CorrectedAt);

/// <summary>A set the client actually performed. Values are self-reported in the app.</summary>
public sealed class SetLog : Entity<Guid>
{
    internal SetLog(SetLogEntry entry) : base(entry.Id) => Apply(entry);

    private SetLog() { }

    public Guid ExerciseId { get; private set; }

    public Guid? PlannedExerciseId { get; private set; }

    public int SetNumber { get; private set; }

    public int? Repetitions { get; private set; }

    public decimal? LoadKg { get; private set; }

    public int? DurationSeconds { get; private set; }

    public decimal? DistanceMeters { get; private set; }

    /// <summary>Perceived difficulty for the set (1–10).</summary>
    public int? Difficulty { get; private set; }

    public string? Notes { get; private set; }

    public DateTimeOffset LoggedAt { get; private set; }

    /// <summary>Corrections made after the workout ended, oldest first. Corrections during the workout overwrite in place.</summary>
    public List<SetLogCorrection> Corrections { get; private set; } = [];

    /// <summary>Repetitions × load, when both are known.</summary>
    public decimal? Volume => Repetitions is { } reps && LoadKg is { } load ? reps * load : null;

    public SetValues Values => new(Repetitions, LoadKg, DurationSeconds, DistanceMeters, Difficulty, Notes);

    internal void Correct(SetLogEntry entry) => Apply(entry);

    internal void CorrectAfterWorkout(SetValues values, string reason, Guid correctedBy, DateTimeOffset now)
    {
        Corrections.Add(new SetLogCorrection(Values, reason, correctedBy, now));
        Repetitions = values.Repetitions;
        LoadKg = values.LoadKg;
        DurationSeconds = values.DurationSeconds;
        DistanceMeters = values.DistanceMeters;
        Difficulty = values.Difficulty;
        Notes = values.Notes;
    }

    private void Apply(SetLogEntry entry)
    {
        ExerciseId = entry.ExerciseId;
        PlannedExerciseId = entry.PlannedExerciseId;
        SetNumber = entry.SetNumber;
        Repetitions = entry.Repetitions;
        LoadKg = entry.LoadKg;
        DurationSeconds = entry.DurationSeconds;
        DistanceMeters = entry.DistanceMeters;
        Difficulty = entry.Difficulty;
        Notes = entry.Notes;
        LoggedAt = entry.LoggedAt;
    }
}

public sealed class WorkoutPause : Entity<Guid>
{
    internal WorkoutPause(DateTimeOffset pausedAt) : base(Guid.CreateVersion7(pausedAt)) => PausedAt = pausedAt;

    private WorkoutPause() { }

    public DateTimeOffset PausedAt { get; private set; }

    public DateTimeOffset? ResumedAt { get; private set; }

    internal void Close(DateTimeOffset at) => ResumedAt ??= at;
}
