using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Users;

namespace MyHealth.Domain.Tenants;

public enum MembershipStatus
{
    Pending = 1,
    Active = 2,
    Declined = 3,
    Revoked = 4,
}

/// <summary>
/// A person's role inside one tenant (MT-02): the disciplines they practise there and whether they administer it.
/// Membership alone never grants access to care records; that still needs an active care relationship (MT-05).
/// Credentials (<see cref="ProfessionalProfile"/>) stay global because a council registration belongs to the person.
/// </summary>
public sealed class TenantMembership : AggregateRoot<Guid>
{
    private TenantMembership(Guid id) : base(id) { }

    private TenantMembership() { }

    public Guid TenantId { get; private set; }

    /// <summary>Set when the invitation is accepted.</summary>
    public Guid? UserId { get; private set; }

    public string InviteeEmail { get; private set; } = null!;

    public List<Discipline> Disciplines { get; private set; } = [];

    /// <summary>Manages memberships and the organization's audit trail. Grants no clinical access (MT-05).</summary>
    public bool IsAdministrator { get; private set; }

    public MembershipStatus Status { get; private set; }

    public Guid InvitedBy { get; private set; }

    public DateTimeOffset InvitedAt { get; private set; }

    public DateTimeOffset? JoinedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? RevokedBy { get; private set; }

    public string? RevokeReason { get; private set; }

    public bool IsActive => Status == MembershipStatus.Active;

    public bool Practices(Discipline discipline) => IsActive && Disciplines.Contains(discipline);

    /// <summary>The person who creates a tenant becomes its first, active administrator.</summary>
    public static TenantMembership Founder(Tenant tenant, UserProfile founder, IEnumerable<Discipline> disciplines, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now))
        {
            TenantId = tenant.Id,
            UserId = founder.Id,
            InviteeEmail = founder.Email,
            Disciplines = disciplines.Distinct().Order().ToList(),
            IsAdministrator = true,
            Status = MembershipStatus.Active,
            InvitedBy = founder.Id,
            InvitedAt = now,
            JoinedAt = now,
        };

    public static TenantMembership Invite(
        Guid tenantId, string email, IEnumerable<Discipline> disciplines, bool isAdministrator, Guid invitedBy, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now))
        {
            TenantId = tenantId,
            InviteeEmail = UserProfile.NormalizeEmail(email),
            Disciplines = disciplines.Distinct().Order().ToList(),
            IsAdministrator = isAdministrator,
            Status = MembershipStatus.Pending,
            InvitedBy = invitedBy,
            InvitedAt = now,
        };

    public Result Accept(UserProfile user, DateTimeOffset now)
    {
        if (Status != MembershipStatus.Pending)
        {
            return MembershipErrors.NotPending;
        }

        if (user.Email != InviteeEmail)
        {
            return MembershipErrors.NotInvitee;
        }

        UserId = user.Id;
        Status = MembershipStatus.Active;
        JoinedAt = now;
        return Result.Success();
    }

    public Result Decline(UserProfile user, DateTimeOffset now)
    {
        if (Status != MembershipStatus.Pending)
        {
            return MembershipErrors.NotPending;
        }

        if (user.Email != InviteeEmail)
        {
            return MembershipErrors.NotInvitee;
        }

        Status = MembershipStatus.Declined;
        RevokedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Removes the person from the tenant (MT-08). Future access stops at once because every professional action
    /// re-checks the membership; the tenant's care records stay, with authorship preserved.
    /// </summary>
    public Result Revoke(Guid actorUserId, string? reason, DateTimeOffset now)
    {
        if (Status is not (MembershipStatus.Active or MembershipStatus.Pending))
        {
            return MembershipErrors.AlreadyClosed;
        }

        var wasActive = IsActive;
        Status = MembershipStatus.Revoked;
        RevokedAt = now;
        RevokedBy = actorUserId;
        RevokeReason = reason;

        if (wasActive && UserId is { } userId)
        {
            Raise(new TenantMembershipRevoked(TenantId, Id, userId, actorUserId, now));
        }

        return Result.Success();
    }
}

public sealed record TenantMembershipRevoked(Guid TenantId, Guid MembershipId, Guid UserId, Guid RevokedBy, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

public interface ITenantMembershipRepository : IRepository<TenantMembership, Guid>
{
    Task<TenantMembership?> GetActiveAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>A pending invitation or an active membership for this e-mail or user.</summary>
    Task<bool> ExistsOpenAsync(Guid tenantId, string email, CancellationToken cancellationToken = default);

    Task<int> CountActiveAdministratorsAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

public static class MembershipErrors
{
    public static readonly Error NotFound = Error.NotFound("Membership.NotFound", "The membership was not found.");
    public static readonly Error NotPending = Error.Conflict("Membership.NotPending", "The invitation is no longer pending.");
    public static readonly Error NotInvitee = Error.Forbidden("Membership.NotInvitee", "This invitation was sent to a different e-mail address.");
    public static readonly Error AlreadyClosed = Error.Conflict("Membership.AlreadyClosed", "The membership has already ended.");
    public static readonly Error Duplicate = Error.Conflict("Membership.Duplicate", "This person already belongs to or is invited to the organization.");
    public static readonly Error LastAdministrator = Error.Conflict(
        "Membership.LastAdministrator", "An organization needs at least one administrator; appoint another one first.");
}
