using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Domain.Assistant;
using MyHealth.Domain.Wellbeing;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.Billing;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Coaching;
using MyHealth.Domain.Messaging;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Privacy;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Therapies;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.MotionCapture;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;

namespace MyHealth.Infrastructure.Persistence;

/// <summary>No-tracking view over the same database for query handlers.</summary>
internal sealed class ReadDbContext(ApplicationDbContext db) : IReadDbContext
{
    public IQueryable<Tenant> Tenants => db.Tenants.AsNoTracking();

    public IQueryable<TenantMembership> TenantMemberships => db.TenantMemberships.AsNoTracking();

    public IQueryable<UserProfile> UserProfiles => db.UserProfiles.AsNoTracking();

    public IQueryable<ProfessionalProfile> ProfessionalProfiles => db.ProfessionalProfiles.AsNoTracking();

    public IQueryable<CareRelationship> CareRelationships => db.CareRelationships.AsNoTracking();

    public IQueryable<Exercise> Exercises => db.Exercises.AsNoTracking();

    public IQueryable<TrainingPlan> TrainingPlans => db.TrainingPlans.AsNoTracking();

    public IQueryable<TrainingPlanVersion> TrainingPlanVersions => db.TrainingPlanVersions.AsNoTracking();

    public IQueryable<WorkoutSession> WorkoutSessions => db.WorkoutSessions.AsNoTracking();

    public IQueryable<WorkoutHeartRate> WorkoutHeartRates => db.WorkoutHeartRates.AsNoTracking();

    public IQueryable<HealthConnection> HealthConnections => db.HealthConnections.AsNoTracking();

    public IQueryable<HealthHistory> HealthHistories => db.HealthHistories.AsNoTracking();

    public IQueryable<PhysicalAssessment> PhysicalAssessments => db.PhysicalAssessments.AsNoTracking();

    public IQueryable<NutritionAssessment> NutritionAssessments => db.NutritionAssessments.AsNoTracking();

    public IQueryable<MealPlan> MealPlans => db.MealPlans.AsNoTracking();

    public IQueryable<MealPlanVersion> MealPlanVersions => db.MealPlanVersions.AsNoTracking();

    public IQueryable<FoodDiaryEntry> FoodDiaryEntries => db.FoodDiaryEntries.AsNoTracking();

    public IQueryable<PrivacyRequest> PrivacyRequests => db.PrivacyRequests.AsNoTracking();

    public IQueryable<RetentionPolicy> RetentionPolicies => db.RetentionPolicies.AsNoTracking();

    public IQueryable<PushDevice> PushDevices => db.PushDevices.AsNoTracking();

    public IQueryable<TherapyModality> TherapyModalities => db.TherapyModalities.AsNoTracking();

    public IQueryable<PractitionerQualification> PractitionerQualifications => db.PractitionerQualifications.AsNoTracking();

    public IQueryable<TherapyService> TherapyServices => db.TherapyServices.AsNoTracking();

    public IQueryable<ConsentTemplate> ConsentTemplates => db.ConsentTemplates.AsNoTracking();

    public IQueryable<TherapyConsent> TherapyConsents => db.TherapyConsents.AsNoTracking();

    public IQueryable<TherapySession> TherapySessions => db.TherapySessions.AsNoTracking();

    public IQueryable<TherapyCarePlan> TherapyCarePlans => db.TherapyCarePlans.AsNoTracking();

    public IQueryable<TenantSubscription> TenantSubscriptions => db.TenantSubscriptions.AsNoTracking();

    public IQueryable<Invoice> Invoices => db.Invoices.AsNoTracking();

    public IQueryable<ModulePrice> ModulePrices => db.ModulePrices.AsNoTracking();

    public IQueryable<PaymentEvent> PaymentEvents => db.PaymentEvents.AsNoTracking();

    public IQueryable<WellbeingCheckIn> WellbeingCheckIns => db.WellbeingCheckIns.AsNoTracking();

    public IQueryable<AssistantConsent> AssistantConsents => db.AssistantConsents.AsNoTracking();

    public IQueryable<AssistantProposal> AssistantProposals => db.AssistantProposals.AsNoTracking();

    public IQueryable<CoachAnalysis> CoachAnalyses => db.CoachAnalyses.AsNoTracking();

    public IQueryable<WhatsAppContact> WhatsAppContacts => db.WhatsAppContacts.AsNoTracking();

    public IQueryable<MotionCaptureJob> MotionCaptureJobs => db.MotionCaptureJobs.AsNoTracking();

    public IQueryable<Notification> Notifications => db.Notifications.AsNoTracking();

    public IQueryable<AuditEntry> AuditEntries => db.AuditEntries.AsNoTracking();
}
