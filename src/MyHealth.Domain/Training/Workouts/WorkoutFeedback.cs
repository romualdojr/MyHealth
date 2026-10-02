using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Training.Workouts;

/// <summary>
/// End-of-session feedback. Discomfort is reported, never diagnosed: it notifies the responsible trainer.
/// </summary>
public sealed record WorkoutFeedback(int PerceivedExertion, bool ReportedDiscomfort, string? DiscomfortDetails, string? Comments)
{
    public Result Validate() =>
        PerceivedExertion is < 1 or > 10
            ? WorkoutErrors.InvalidPerceivedExertion
            : Result.Success();
}

public sealed record FeedbackReply(string Message, Guid RepliedByProfileId, DateTimeOffset RepliedAt);

/// <summary>A copy of the session saved in a health platform. The phone stores our session id as HKMetadataKeyExternalUUID.</summary>
public sealed record ExternalWorkoutExport(Guid ExternalWorkoutId, DateTimeOffset SavedAt, DateTimeOffset RecordedAt);

public enum BodyCheckAnswer
{
    Better = 1,
    Same = 2,
    Worse = 3,

    /// <summary>Something new the client noticed, not a follow-up of an earlier report.</summary>
    New = 4,
}

/// <summary>
/// How a body part feels, asked during rest or told to the assistant ("como está o joelho?"). It is reported, never
/// assessed: with <see cref="ReportedToTrainer"/> the responsible trainer is notified.
/// </summary>
public sealed record BodyCheck(Guid Id, string Subject, BodyCheckAnswer Answer, string? Details, bool ReportedToTrainer, DateTimeOffset At);

/// <summary>What the client reported; <see cref="Subject"/> is null when only the end-of-workout flag was set.</summary>
public sealed record ReportedDiscomfort(string? Subject, string? Details);
