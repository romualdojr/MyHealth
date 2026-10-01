using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;

namespace MyHealth.Application.Abstractions.Data;

/// <summary>
/// Read side of CQRS: no-tracking queryables used by query handlers to project straight into response models.
/// Commands never use this; they load aggregates through repositories.
/// </summary>
public interface IReadDbContext
{
    IQueryable<UserProfile> UserProfiles { get; }

    IQueryable<ProfessionalProfile> ProfessionalProfiles { get; }

    IQueryable<CareRelationship> CareRelationships { get; }

    IQueryable<Exercise> Exercises { get; }

    IQueryable<TrainingPlan> TrainingPlans { get; }

    IQueryable<TrainingPlanVersion> TrainingPlanVersions { get; }

    IQueryable<WorkoutSession> WorkoutSessions { get; }

    IQueryable<WorkoutHeartRate> WorkoutHeartRates { get; }

    IQueryable<HealthConnection> HealthConnections { get; }

    IQueryable<Notification> Notifications { get; }

    IQueryable<AuditEntry> AuditEntries { get; }
}
