using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Data;
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

    public IQueryable<Notification> Notifications => db.Notifications.AsNoTracking();

    public IQueryable<AuditEntry> AuditEntries => db.AuditEntries.AsNoTracking();
}
