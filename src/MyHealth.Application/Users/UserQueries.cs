using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Notifications;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Billing;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Users;

namespace MyHealth.Application.Users;

public sealed record MeResponse(
    Guid UserId,
    string FullName,
    string Email,
    string TimeZoneId,
    bool IsAdministrator,
    IReadOnlyList<MyProfessionalProfileItem> ProfessionalProfiles,
    IReadOnlyList<MyMembershipItem> Memberships);

public sealed record MyProfessionalProfileItem(Guid Id, Discipline Discipline, string DisplayName, VerificationStatus VerificationStatus);

/// <summary>A tenant the caller can act for; send its id as <c>X-Tenant-Id</c> to work in it (MT-02).</summary>
public sealed record MyMembershipItem(
    Guid MembershipId, Guid TenantId, string TenantName, TenantKind Kind, IReadOnlyList<Discipline> Disciplines, bool IsAdministrator,
    SubscriptionStatus? SubscriptionStatus);

public sealed record GetMeQuery : IQuery<MeResponse>;

internal sealed class GetMeHandler(IUserContext user, IReadDbContext db) : IQueryHandler<GetMeQuery, MeResponse>
{
    public async Task<Result<MeResponse>> Handle(GetMeQuery query, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == user.UserId, cancellationToken);
        if (profile is null)
        {
            return UserErrors.ProfileNotFound;
        }

        var professional = await db.ProfessionalProfiles
            .Where(p => p.UserId == user.UserId)
            .Select(p => new MyProfessionalProfileItem(p.Id, p.Discipline, p.DisplayName, p.VerificationStatus))
            .ToListAsync(cancellationToken);

        var memberships = await (
                from m in db.TenantMemberships
                join t in db.Tenants on m.TenantId equals t.Id
                join s in db.TenantSubscriptions on t.Id equals s.TenantId into subscriptions
                from s in subscriptions.DefaultIfEmpty()
                where m.UserId == user.UserId && m.Status == MembershipStatus.Active
                orderby t.Name
                select new { Membership = m, t.Name, t.Kind, Status = s == null ? (SubscriptionStatus?)null : s.Status })
            .ToListAsync(cancellationToken);

        return new MeResponse(
            profile.Id, profile.FullName, profile.Email, profile.TimeZoneId, user.IsAdministrator, professional,
            memberships.Select(m => new MyMembershipItem(
                m.Membership.Id, m.Membership.TenantId, m.Name, m.Kind, m.Membership.Disciplines, m.Membership.IsAdministrator, m.Status)).ToList());
    }
}

public sealed record ClientHomeResponse(
    string Greeting,
    IReadOnlyList<AssignedProfessionalItem> Professionals,
    IReadOnlyList<ActivePlanItem> ActivePlans,
    WeeklyAttendance Attendance,
    int UnreadNotifications,
    HealthConnectionStatus? AppleHealthStatus);

public sealed record AssignedProfessionalItem(
    Guid RelationshipId, Guid TenantId, string OrganizationName, Guid ProfessionalProfileId, string DisplayName, Discipline Discipline, string Registration);

/// <summary>"Next session" is the next session in plan order after the last one completed (no calendar in Phase 1).</summary>
public sealed record ActivePlanItem(Guid PlanId, Guid TenantId, string Name, int VersionNumber, Guid? NextSessionId, string? NextSessionName);

/// <summary>Seven local days ending today. Days without a completed session are not "missed": rest is not absence.</summary>
public sealed record WeeklyAttendance(DateOnly From, DateOnly To, int ActiveDays, int CompletedSessions, IReadOnlyList<AttendanceDay> Days);

public sealed record AttendanceDay(DateOnly Date, int CompletedSessions);

public sealed record GetClientHomeQuery : IQuery<ClientHomeResponse>;

internal sealed class GetClientHomeHandler(IUserContext user, IReadDbContext db, TimeProvider clock)
    : IQueryHandler<GetClientHomeQuery, ClientHomeResponse>
{
    public async Task<Result<ClientHomeResponse>> Handle(GetClientHomeQuery query, CancellationToken cancellationToken)
    {
        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == user.UserId, cancellationToken);
        if (profile is null)
        {
            return UserErrors.ProfileNotFound;
        }

        var professionals = await (
                from r in db.CareRelationships
                join p in db.ProfessionalProfiles on r.ProfessionalProfileId equals p.Id
                join t in db.Tenants on r.TenantId equals t.Id
                where r.ClientId == user.UserId && r.Status == RelationshipStatus.Active
                select new { r.Id, r.TenantId, OrganizationName = t.Name, ProfileId = p.Id, p.DisplayName, p.Discipline, p.Registration })
            .ToListAsync(cancellationToken);

        var today = TimeZones.LocalDate(clock.GetUtcNow(), profile.TimeZoneId);
        var attendance = await AttendanceCalculator.CalculateAsync(db, user.UserId, today.AddDays(-6), today, cancellationToken);

        var unread = await NotificationScope.Visible(db.Notifications, user.UserId, activeTenantId: null)
            .CountAsync(n => n.ReadAt == null, cancellationToken);
        var appleHealth = await db.HealthConnections
            .Where(c => c.ClientId == user.UserId && c.Provider == HealthDataProvider.AppleHealth)
            .Select(c => (HealthConnectionStatus?)c.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return new ClientHomeResponse(
            $"Olá, {profile.FullName.Split(' ')[0]}",
            professionals
                .Select(p => new AssignedProfessionalItem(
                    p.Id, p.TenantId, p.OrganizationName, p.ProfileId, p.DisplayName, p.Discipline, $"{p.Registration.Council} {p.Registration.Number}/{p.Registration.Region}"))
                .ToList(),
            await GetActivePlansAsync(cancellationToken),
            attendance,
            unread,
            appleHealth);
    }

    private async Task<IReadOnlyList<ActivePlanItem>> GetActivePlansAsync(CancellationToken cancellationToken)
    {
        var versions = await (
                from plan in db.TrainingPlans
                join version in db.TrainingPlanVersions on plan.CurrentVersionId equals version.Id
                where plan.ClientId == user.UserId && plan.Status == PlanStatus.Published
                select version)
            .ToListAsync(cancellationToken);

        var planIds = versions.Select(v => v.TrainingPlanId).ToList();
        var lastCompleted = await db.WorkoutSessions
            .Where(s => s.ClientId == user.UserId && planIds.Contains(s.TrainingPlanId) && s.Status == WorkoutStatus.Completed)
            .GroupBy(s => s.TrainingPlanId)
            .Select(g => g.OrderByDescending(s => s.CompletedAt).Select(s => new { s.TrainingPlanId, s.PlannedSessionId }).First())
            .ToListAsync(cancellationToken);

        return versions.Select(v =>
        {
            var ordered = v.Content.Sessions.OrderBy(s => s.Order).ToList();
            var last = lastCompleted.FirstOrDefault(l => l.TrainingPlanId == v.TrainingPlanId);
            var lastIndex = last is null ? -1 : ordered.FindIndex(s => s.Id == last.PlannedSessionId);
            var next = ordered.Count == 0 ? null : ordered[(lastIndex + 1) % ordered.Count];
            return new ActivePlanItem(v.TrainingPlanId, v.TenantId, v.Details.Name, v.VersionNumber, next?.Id, next?.Name);
        }).ToList();
    }
}

internal static class AttendanceCalculator
{
    /// <summary>Counts only completed sessions, grouped by the client's local completion date.</summary>
    public static async Task<WeeklyAttendance> CalculateAsync(
        IReadDbContext db, Guid clientId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var perDay = await db.WorkoutSessions
            .Where(s => s.ClientId == clientId && s.Status == WorkoutStatus.Completed && s.LocalDate >= from && s.LocalDate <= to)
            .GroupBy(s => s.LocalDate!.Value)
            .Select(g => new AttendanceDay(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(offset => from.AddDays(offset))
            .Select(date => perDay.FirstOrDefault(d => d.Date == date) ?? new AttendanceDay(date, 0))
            .ToList();

        return new WeeklyAttendance(from, to, days.Count(d => d.CompletedSessions > 0), days.Sum(d => d.CompletedSessions), days);
    }
}
