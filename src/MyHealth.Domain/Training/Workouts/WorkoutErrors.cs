using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Training.Workouts;

public static class WorkoutErrors
{
    public static readonly Error NotFound = Error.NotFound("Workout.NotFound", "The workout session was not found.");
    public static readonly Error InvalidId = Error.Validation("Workout.InvalidId", "A client-generated session id is required.");
    public static readonly Error IdConflict = Error.Conflict("Workout.IdConflict", "This session id is already used by a different workout.");
    public static readonly Error PlannedSessionNotFound = Error.Validation("Workout.PlannedSessionNotFound", "The session does not exist in this plan version.");
    public static readonly Error NotInProgress = Error.Conflict("Workout.NotInProgress", "The workout is not in progress.");
    public static readonly Error NotPaused = Error.Conflict("Workout.NotPaused", "The workout is not paused.");
    public static readonly Error NotCompleted = Error.Conflict("Workout.NotCompleted", "The workout has not been completed.");
    public static readonly Error AlreadyFinished = Error.Conflict("Workout.AlreadyFinished", "The workout has already been completed or abandoned.");
    public static readonly Error BeforeStart = Error.Validation("Workout.BeforeStart", "The timestamp is earlier than the workout start.");
    public static readonly Error FutureTimestamp = Error.Validation("Workout.FutureTimestamp", "The timestamp is in the future.");
    public static readonly Error EmptySet = Error.Validation("Workout.EmptySet", "A set needs repetitions, duration or distance.");
    public static readonly Error ExerciseNotInSession = Error.Validation("Workout.ExerciseNotInSession", "The exercise is not part of this session or its alternatives.");
    public static readonly Error InvalidPerceivedExertion = Error.Validation("Workout.InvalidPerceivedExertion", "Perceived exertion must be between 1 and 10.");
    public static readonly Error AlreadySavedToAppleHealth = Error.Conflict(
        "Workout.AlreadySavedToAppleHealth", "A different Apple Health workout is already recorded for this session; delete the duplicate on the phone.");
    public static readonly Error StillOpen = Error.Conflict("Workout.StillOpen", "The workout is still open; update the set directly.");
    public static readonly Error SetNotFound = Error.NotFound("Workout.SetNotFound", "The set was not found in this workout.");
    public static readonly Error CorrectionReasonRequired = Error.Validation("Workout.CorrectionReasonRequired", "A correction after the workout needs a reason.");
    public static readonly Error NotResponsibleTrainer = Error.Forbidden("Workout.NotResponsibleTrainer", "Only the trainer responsible for this plan can reply.");
    public static readonly Error BodyCheckClosed = Error.Conflict(
        "Workout.BodyCheckClosed", "Body checks can be added during the workout and up to 7 days after it ended.");
    public static readonly Error BodyCheckSubjectRequired = Error.Validation("Workout.BodyCheckSubjectRequired", "Say what the check is about, for example \"joelho direito\".");
    public static readonly Error BodyCheckIdConflict = Error.Conflict("Workout.BodyCheckIdConflict", "This body check id was already used with different answers.");
}
