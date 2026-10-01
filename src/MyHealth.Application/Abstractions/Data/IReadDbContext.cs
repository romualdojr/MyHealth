using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Privacy;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Therapies;
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
    IQueryable<Tenant> Tenants { get; }

    IQueryable<TenantMembership> TenantMemberships { get; }

    IQueryable<UserProfile> UserProfiles { get; }

    IQueryable<ProfessionalProfile> ProfessionalProfiles { get; }

    IQueryable<CareRelationship> CareRelationships { get; }

    IQueryable<Exercise> Exercises { get; }

    IQueryable<TrainingPlan> TrainingPlans { get; }

    IQueryable<TrainingPlanVersion> TrainingPlanVersions { get; }

    IQueryable<WorkoutSession> WorkoutSessions { get; }

    IQueryable<WorkoutHeartRate> WorkoutHeartRates { get; }

    IQueryable<HealthConnection> HealthConnections { get; }

    IQueryable<HealthHistory> HealthHistories { get; }

    IQueryable<PhysicalAssessment> PhysicalAssessments { get; }

    IQueryable<NutritionAssessment> NutritionAssessments { get; }

    IQueryable<MealPlan> MealPlans { get; }

    IQueryable<MealPlanVersion> MealPlanVersions { get; }

    IQueryable<FoodDiaryEntry> FoodDiaryEntries { get; }

    IQueryable<PrivacyRequest> PrivacyRequests { get; }

    IQueryable<RetentionPolicy> RetentionPolicies { get; }

    IQueryable<PushDevice> PushDevices { get; }

    IQueryable<TherapyModality> TherapyModalities { get; }

    IQueryable<PractitionerQualification> PractitionerQualifications { get; }

    IQueryable<TherapyService> TherapyServices { get; }

    IQueryable<ConsentTemplate> ConsentTemplates { get; }

    IQueryable<TherapyConsent> TherapyConsents { get; }

    IQueryable<TherapySession> TherapySessions { get; }

    IQueryable<TherapyCarePlan> TherapyCarePlans { get; }

    IQueryable<Notification> Notifications { get; }

    IQueryable<AuditEntry> AuditEntries { get; }
}
