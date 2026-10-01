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
}

public enum RelationshipStatus
{
    Pending = 1,
    Active = 2,
    Declined = 3,
    Ended = 4,
}
