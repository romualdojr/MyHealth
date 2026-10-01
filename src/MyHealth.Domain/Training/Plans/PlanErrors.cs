using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Training.Plans;

public static class PlanErrors
{
    public static readonly Error NotFound = Error.NotFound("TrainingPlan.NotFound", "The training plan was not found.");
    public static readonly Error VersionNotFound = Error.NotFound("TrainingPlan.VersionNotFound", "The training plan version was not found.");
    public static readonly Error NotOwner = Error.Forbidden("TrainingPlan.NotOwner", "Only the trainer who owns this plan can change it.");
    public static readonly Error Ended = Error.Conflict("TrainingPlan.Ended", "The training plan has ended and can no longer be changed.");
    public static readonly Error NotPublished = Error.Conflict("TrainingPlan.NotPublished", "The training plan has no published version.");
    public static readonly Error NothingToPublish = Error.Conflict("TrainingPlan.NothingToPublish", "There are no changes to publish.");
    public static readonly Error NoSessions = Error.Validation("TrainingPlan.NoSessions", "A plan needs at least one session to be published.");
    public static readonly Error InvalidDates = Error.Validation("TrainingPlan.InvalidDates", "The end date must be on or after the start date.");
    public static readonly Error InvalidFrequency = Error.Validation("TrainingPlan.InvalidFrequency", "Suggested frequency must be between 1 and 14 sessions per week.");
    public static readonly Error SetWithoutTarget = Error.Validation("TrainingPlan.SetWithoutTarget", "Each set needs a repetition or duration target.");
    public static readonly Error InvalidRepetitionRange = Error.Validation("TrainingPlan.InvalidRepetitionRange", "Minimum repetitions cannot exceed maximum repetitions.");

    public static Error DuplicateIds(string item) =>
        Error.Validation("TrainingPlan.DuplicateIds", $"Each {item} must have a unique id.");

    public static Error IncompleteSession(string sessionName) =>
        Error.Validation("TrainingPlan.IncompleteSession", $"Session '{sessionName}' is incomplete: strength sessions need exercises with sets, aerobic sessions need a target.");
}
