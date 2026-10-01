using Microsoft.EntityFrameworkCore;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;

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

internal sealed class TrainingPlanRepository(ApplicationDbContext db) : Repository<TrainingPlan>(db), ITrainingPlanRepository;

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
