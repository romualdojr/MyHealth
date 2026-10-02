using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Domain.Assistant;
using MyHealth.Domain.Wellbeing;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.Billing;
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
using MyHealth.Infrastructure.Identity;

namespace MyHealth.Infrastructure.Persistence;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public const string IdentitySchema = "identity";
    public const string DefaultSchema = "myhealth";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();

    public DbSet<ProfessionalProfile> ProfessionalProfiles => Set<ProfessionalProfile>();

    public DbSet<CareRelationship> CareRelationships => Set<CareRelationship>();

    public DbSet<Exercise> Exercises => Set<Exercise>();

    public DbSet<TrainingPlan> TrainingPlans => Set<TrainingPlan>();

    public DbSet<TrainingPlanVersion> TrainingPlanVersions => Set<TrainingPlanVersion>();

    public DbSet<WorkoutSession> WorkoutSessions => Set<WorkoutSession>();

    public DbSet<WorkoutHeartRate> WorkoutHeartRates => Set<WorkoutHeartRate>();

    public DbSet<HealthConnection> HealthConnections => Set<HealthConnection>();

    public DbSet<HealthHistory> HealthHistories => Set<HealthHistory>();

    public DbSet<PhysicalAssessment> PhysicalAssessments => Set<PhysicalAssessment>();

    public DbSet<NutritionAssessment> NutritionAssessments => Set<NutritionAssessment>();

    public DbSet<MealPlan> MealPlans => Set<MealPlan>();

    public DbSet<MealPlanVersion> MealPlanVersions => Set<MealPlanVersion>();

    public DbSet<FoodDiaryEntry> FoodDiaryEntries => Set<FoodDiaryEntry>();

    public DbSet<PrivacyRequest> PrivacyRequests => Set<PrivacyRequest>();

    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();

    public DbSet<PushDevice> PushDevices => Set<PushDevice>();

    public DbSet<TherapyModality> TherapyModalities => Set<TherapyModality>();

    public DbSet<PractitionerQualification> PractitionerQualifications => Set<PractitionerQualification>();

    public DbSet<TherapyService> TherapyServices => Set<TherapyService>();

    public DbSet<ConsentTemplate> ConsentTemplates => Set<ConsentTemplate>();

    public DbSet<TherapyConsent> TherapyConsents => Set<TherapyConsent>();

    public DbSet<TherapySession> TherapySessions => Set<TherapySession>();

    public DbSet<TherapyCarePlan> TherapyCarePlans => Set<TherapyCarePlan>();

    public DbSet<TenantSubscription> TenantSubscriptions => Set<TenantSubscription>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<ModulePrice> ModulePrices => Set<ModulePrice>();

    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();

    public DbSet<WellbeingCheckIn> WellbeingCheckIns => Set<WellbeingCheckIn>();

    public DbSet<AssistantConsent> AssistantConsents => Set<AssistantConsent>();

    public DbSet<AssistantProposal> AssistantProposals => Set<AssistantProposal>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Identity tables live in their own schema, apart from the domain model.
        builder.HasDefaultSchema(IdentitySchema);
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums as readable strings: safer for reporting and for reordering enum members.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(64);
    }
}
