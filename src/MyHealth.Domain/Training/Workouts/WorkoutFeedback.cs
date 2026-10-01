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
