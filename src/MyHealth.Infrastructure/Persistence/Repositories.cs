using Microsoft.EntityFrameworkCore;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.Assistant;
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
using MyHealth.Domain.Wellbeing;

namespace MyHealth.Infrastructure.Persistence;

internal abstract class Repository<TAggregate>(ApplicationDbContext db) : IRepository<TAggregate, Guid>
    where TAggregate : AggregateRoot<Guid>
{
    protected ApplicationDbContext Db { get; } = db;

    public Task<TAggregate?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Db.Set<TAggregate>().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public void Add(TAggregate aggregate) => Db.Set<TAggregate>().Add(aggregate);
}

internal sealed class UserProfileRepository(ApplicationDbContext db) : Repository<UserProfile>(db), IUserProfileRepository
{
    public Task<UserProfile?> GetByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
        Db.UserProfiles.FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);
}

internal sealed class ProfessionalProfileRepository(ApplicationDbContext db) : Repository<ProfessionalProfile>(db), IProfessionalProfileRepository
{
    public Task<ProfessionalProfile?> GetByUserAsync(Guid userId, Discipline discipline, CancellationToken cancellationToken = default) =>
        Db.ProfessionalProfiles.FirstOrDefaultAsync(p => p.UserId == userId && p.Discipline == discipline, cancellationToken);
}

internal sealed class TenantRepository(ApplicationDbContext db) : Repository<Tenant>(db), ITenantRepository;

internal sealed class TenantMembershipRepository(ApplicationDbContext db) : Repository<TenantMembership>(db), ITenantMembershipRepository
{
    public Task<TenantMembership?> GetActiveAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default) =>
        Db.TenantMemberships.FirstOrDefaultAsync(
            m => m.TenantId == tenantId && m.UserId == userId && m.Status == MembershipStatus.Active, cancellationToken);

    public Task<bool> ExistsOpenAsync(Guid tenantId, string email, CancellationToken cancellationToken = default) =>
        (from m in Db.TenantMemberships
         join u in Db.UserProfiles on m.UserId equals u.Id into users
         from u in users.DefaultIfEmpty()
         where m.TenantId == tenantId
               && ((m.Status == MembershipStatus.Pending && m.InviteeEmail == email)
                   || (m.Status == MembershipStatus.Active && (m.InviteeEmail == email || (u != null && u.Email == email))))
         select m.Id).AnyAsync(cancellationToken);

    public Task<int> CountActiveAdministratorsAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Db.TenantMemberships.CountAsync(m => m.TenantId == tenantId && m.Status == MembershipStatus.Active && m.IsAdministrator, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListActiveAdministratorIdsAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await Db.TenantMemberships
            .Where(m => m.TenantId == tenantId && m.Status == MembershipStatus.Active && m.IsAdministrator && m.UserId != null)
            .Select(m => m.UserId!.Value)
            .ToListAsync(cancellationToken);
}

internal sealed class CareRelationshipRepository(ApplicationDbContext db) : Repository<CareRelationship>(db), ICareRelationshipRepository
{
    public Task<bool> ExistsOpenAsync(Guid tenantId, Guid professionalProfileId, string inviteeEmail, CancellationToken cancellationToken = default) =>
        Db.CareRelationships.AnyAsync(
            r => r.TenantId == tenantId && r.ProfessionalProfileId == professionalProfileId && r.InviteeEmail == inviteeEmail
                 && (r.Status == RelationshipStatus.Pending || r.Status == RelationshipStatus.Active),
            cancellationToken);

    public Task<CareRelationship?> GetActiveAsync(
        Guid tenantId, Guid professionalUserId, Discipline discipline, Guid clientId, CancellationToken cancellationToken = default) =>
        Db.CareRelationships.FirstOrDefaultAsync(
            r => r.TenantId == tenantId && r.ProfessionalUserId == professionalUserId && r.Discipline == discipline && r.ClientId == clientId
                 && r.Status == RelationshipStatus.Active,
            cancellationToken);

    public async Task<IReadOnlyList<CareRelationship>> ListActiveForClientAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        await Db.CareRelationships.Where(r => r.ClientId == clientId && r.Status == RelationshipStatus.Active).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CareRelationship>> ListOpenForProfessionalAsync(
        Guid tenantId, Guid professionalUserId, CancellationToken cancellationToken = default) =>
        await Db.CareRelationships
            .Where(r => r.TenantId == tenantId && r.ProfessionalUserId == professionalUserId
                        && (r.Status == RelationshipStatus.Pending || r.Status == RelationshipStatus.Active))
            .ToListAsync(cancellationToken);
}

internal sealed class ExerciseRepository(ApplicationDbContext db) : Repository<Exercise>(db), IExerciseRepository
{
    public async Task<IReadOnlyList<Exercise>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        await Db.Exercises.Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken);
}

internal sealed class TrainingPlanRepository(ApplicationDbContext db) : Repository<TrainingPlan>(db), ITrainingPlanRepository
{
    public async Task<IReadOnlyList<TrainingPlan>> ListOpenForClientAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        await Db.TrainingPlans.Where(p => p.ClientId == clientId && p.Status != PlanStatus.Ended).ToListAsync(cancellationToken);
}

internal sealed class TrainingPlanVersionRepository(ApplicationDbContext db) : Repository<TrainingPlanVersion>(db), ITrainingPlanVersionRepository;

internal sealed class WorkoutSessionRepository(ApplicationDbContext db) : Repository<WorkoutSession>(db), IWorkoutSessionRepository;

internal sealed class WorkoutHeartRateRepository(ApplicationDbContext db) : Repository<WorkoutHeartRate>(db), IWorkoutHeartRateRepository
{
    public async Task<IReadOnlySet<Guid>> GetClaimedByOtherSessionsAsync(
        Guid clientId, Guid workoutSessionId, IReadOnlyCollection<Guid> externalIds, CancellationToken cancellationToken = default)
    {
        if (externalIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var claimed = await Db.Set<HeartRateSample>()
            .Where(s => s.ClientId == clientId && externalIds.Contains(s.ExternalId)
                        && EF.Property<Guid>(s, "workout_heart_rate_id") != workoutSessionId)
            .Select(s => s.ExternalId)
            .ToListAsync(cancellationToken);
        return claimed.ToHashSet();
    }
}

internal sealed class HealthConnectionRepository(ApplicationDbContext db) : Repository<HealthConnection>(db), IHealthConnectionRepository
{
    public Task<HealthConnection?> GetAsync(Guid clientId, HealthDataProvider provider, CancellationToken cancellationToken = default) =>
        Db.HealthConnections.FirstOrDefaultAsync(c => c.ClientId == clientId && c.Provider == provider, cancellationToken);
}

internal sealed class NotificationRepository(ApplicationDbContext db) : Repository<Notification>(db), INotificationRepository;

internal sealed class HealthHistoryRepository(ApplicationDbContext db) : Repository<HealthHistory>(db), IHealthHistoryRepository
{
    public Task<HealthHistory?> GetByClientAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        Db.HealthHistories.FirstOrDefaultAsync(h => h.ClientId == clientId, cancellationToken);
}

internal sealed class PhysicalAssessmentRepository(ApplicationDbContext db) : Repository<PhysicalAssessment>(db), IPhysicalAssessmentRepository;

internal sealed class NutritionAssessmentRepository(ApplicationDbContext db) : Repository<NutritionAssessment>(db), INutritionAssessmentRepository;

internal sealed class MealPlanRepository(ApplicationDbContext db) : Repository<MealPlan>(db), IMealPlanRepository;

internal sealed class MealPlanVersionRepository(ApplicationDbContext db) : Repository<MealPlanVersion>(db), IMealPlanVersionRepository
{
    public Task<MealPlanVersion?> GetByNumberAsync(Guid mealPlanId, int versionNumber, CancellationToken cancellationToken = default) =>
        Db.MealPlanVersions.FirstOrDefaultAsync(v => v.MealPlanId == mealPlanId && v.VersionNumber == versionNumber, cancellationToken);
}

internal sealed class FoodDiaryRepository(ApplicationDbContext db) : Repository<FoodDiaryEntry>(db), IFoodDiaryRepository;

internal sealed class PrivacyRequestRepository(ApplicationDbContext db) : Repository<PrivacyRequest>(db), IPrivacyRequestRepository;

internal sealed class RetentionPolicyRepository(ApplicationDbContext db) : Repository<RetentionPolicy>(db), IRetentionPolicyRepository
{
    public Task<RetentionPolicy?> GetByRecordTypeAsync(string recordType, CancellationToken cancellationToken = default) =>
        Db.RetentionPolicies.FirstOrDefaultAsync(p => p.RecordType == recordType, cancellationToken);
}

internal sealed class PushDeviceRepository(ApplicationDbContext db) : Repository<PushDevice>(db), IPushDeviceRepository
{
    public Task<PushDevice?> GetByInstallationAsync(Guid installationId, CancellationToken cancellationToken = default) =>
        Db.PushDevices.FirstOrDefaultAsync(d => d.InstallationId == installationId, cancellationToken);

    public void Remove(PushDevice device) => Db.PushDevices.Remove(device);
}

internal sealed class TherapyModalityRepository(ApplicationDbContext db) : Repository<TherapyModality>(db), ITherapyModalityRepository
{
    public Task<TherapyModality?> GetByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Db.TherapyModalities.FirstOrDefaultAsync(m => m.Code == code, cancellationToken);
}

internal sealed class PractitionerQualificationRepository(ApplicationDbContext db)
    : Repository<PractitionerQualification>(db), IPractitionerQualificationRepository
{
    public Task<PractitionerQualification?> GetCurrentAsync(Guid professionalProfileId, Guid modalityId, CancellationToken cancellationToken = default) =>
        Db.PractitionerQualifications
            .Where(q => q.ProfessionalProfileId == professionalProfileId && q.ModalityId == modalityId)
            .OrderByDescending(q => q.SubmittedAt)
            .FirstOrDefaultAsync(cancellationToken);
}

internal sealed class TherapyServiceRepository(ApplicationDbContext db) : Repository<TherapyService>(db), ITherapyServiceRepository;

internal sealed class ConsentTemplateRepository(ApplicationDbContext db) : Repository<ConsentTemplate>(db), IConsentTemplateRepository
{
    public Task<ConsentTemplate?> GetCurrentAsync(Guid modalityId, CancellationToken cancellationToken = default) =>
        Db.ConsentTemplates.Where(t => t.ModalityId == modalityId).OrderByDescending(t => t.Version).FirstOrDefaultAsync(cancellationToken);
}

internal sealed class TherapyConsentRepository(ApplicationDbContext db) : Repository<TherapyConsent>(db), ITherapyConsentRepository
{
    public Task<TherapyConsent?> GetLatestAsync(
        Guid clientId, Guid tenantId, Guid practitionerProfileId, Guid modalityId, CancellationToken cancellationToken = default) =>
        Db.TherapyConsents
            .Where(c => c.ClientId == clientId && c.TenantId == tenantId && c.PractitionerProfileId == practitionerProfileId && c.ModalityId == modalityId)
            .OrderByDescending(c => c.DecidedAt)
            .FirstOrDefaultAsync(cancellationToken);
}

internal sealed class TherapySessionRepository(ApplicationDbContext db) : Repository<TherapySession>(db), ITherapySessionRepository
{
    public Task<bool> HasOverlapAsync(
        Guid practitionerProfileId, DateTimeOffset startsAt, DateTimeOffset endsAt, Guid? excludeSessionId, CancellationToken cancellationToken = default) =>
        Db.TherapySessions.AnyAsync(
            s => s.PractitionerProfileId == practitionerProfileId && s.Status == TherapySessionStatus.Scheduled
                 && s.StartsAt < endsAt && s.EndsAt > startsAt && s.Id != excludeSessionId,
            cancellationToken);

    public async Task<IReadOnlyList<TherapySession>> ListScheduledAsync(
        Guid tenantId, Guid practitionerProfileId, Guid clientId, CancellationToken cancellationToken = default) =>
        await Db.TherapySessions
            .Where(s => s.TenantId == tenantId && s.PractitionerProfileId == practitionerProfileId && s.ClientId == clientId
                        && s.Status == TherapySessionStatus.Scheduled)
            .ToListAsync(cancellationToken);
}

internal sealed class TherapyCarePlanRepository(ApplicationDbContext db) : Repository<TherapyCarePlan>(db), ITherapyCarePlanRepository;

internal sealed class ModulePriceRepository(ApplicationDbContext db) : Repository<ModulePrice>(db), IModulePriceRepository
{
    public async Task<IReadOnlyDictionary<BillingModule, long>> GetCurrentAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var prices = await Db.ModulePrices.Where(p => p.EffectiveFrom <= at).ToListAsync(cancellationToken);
        return prices.GroupBy(p => p.Module).ToDictionary(g => g.Key, g => g.MaxBy(p => p.EffectiveFrom)!.MonthlyAmountCents);
    }
}

internal sealed class TenantSubscriptionRepository(ApplicationDbContext db) : Repository<TenantSubscription>(db), ITenantSubscriptionRepository
{
    public Task<TenantSubscription?> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

    public async Task<IReadOnlyList<TenantSubscription>> ListBillableAsync(CancellationToken cancellationToken = default) =>
        await Db.TenantSubscriptions.Where(s => s.Status != SubscriptionStatus.Cancelled).ToListAsync(cancellationToken);
}

internal sealed class InvoiceRepository(ApplicationDbContext db) : Repository<Invoice>(db), IInvoiceRepository
{
    public async Task<IReadOnlyList<Invoice>> ListOpenAsync(Guid subscriptionId, CancellationToken cancellationToken = default) =>
        await Db.Invoices.Where(i => i.SubscriptionId == subscriptionId && i.Status == InvoiceStatus.Open).ToListAsync(cancellationToken);

    public Task<Invoice?> GetByChargeAsync(string provider, string chargeId, CancellationToken cancellationToken = default) =>
        Db.Invoices.FirstOrDefaultAsync(i => i.Charge != null && i.Charge.Provider == provider && i.Charge.ChargeId == chargeId, cancellationToken);
}

internal sealed class PaymentEventRepository(ApplicationDbContext db) : Repository<PaymentEvent>(db), IPaymentEventRepository
{
    public Task<bool> ExistsAsync(string provider, string providerEventId, CancellationToken cancellationToken = default) =>
        Db.PaymentEvents.AnyAsync(e => e.Provider == provider && e.ProviderEventId == providerEventId, cancellationToken);
}

internal sealed class WellbeingCheckInRepository(ApplicationDbContext db) : Repository<WellbeingCheckIn>(db), IWellbeingCheckInRepository
{
    public Task<WellbeingCheckIn?> GetByDateAsync(Guid clientId, DateOnly localDate, CancellationToken cancellationToken = default) =>
        Db.WellbeingCheckIns.FirstOrDefaultAsync(c => c.ClientId == clientId && c.LocalDate == localDate, cancellationToken);
}

internal sealed class AssistantConsentRepository(ApplicationDbContext db) : Repository<AssistantConsent>(db), IAssistantConsentRepository
{
    public Task<AssistantConsent?> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Db.AssistantConsents.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
}

internal sealed class AssistantProposalRepository(ApplicationDbContext db) : Repository<AssistantProposal>(db), IAssistantProposalRepository
{
    public Task DeleteStaleAsync(Guid userId, DateTimeOffset olderThan, CancellationToken cancellationToken = default) =>
        Db.AssistantProposals.Where(p => p.UserId == userId && p.CreatedAt < olderThan).ExecuteDeleteAsync(cancellationToken);
}
