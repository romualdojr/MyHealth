namespace MyHealth.Domain.CareRelationships;

/// <summary>
/// Data a client may share with a professional beyond what the professional's own discipline produces.
/// Example: a trainer always sees the workouts they prescribe, but sees heart rate only with a HeartRate grant.
/// </summary>
public enum DataCategory
{
    HeartRate = 1,
    WorkoutSummary = 2,
    PhysicalAssessments = 3,
    NutritionSummary = 4,
    TherapySummary = 5,

    /// <summary>The client's health history (RF-23). Every professional needs it shared, including trainers.</summary>
    HealthHistory = 6,

    /// <summary>Sleep and energy check-ins, often answered during rest.</summary>
    Wellbeing = 7,

    /// <summary>
    /// The trainer may send the client's training records (plan, logged sets, feedback, body checks; wellbeing only
    /// when also shared) to an AI provider to get suggestions for the plan. Pseudonymised; the trainer decides.
    /// </summary>
    AiCoaching = 8,
}

public enum RelationshipStatus
{
    Pending = 1,
    Active = 2,
    Declined = 3,
    Ended = 4,
}
