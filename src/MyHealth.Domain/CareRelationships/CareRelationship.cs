using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Users;

namespace MyHealth.Domain.CareRelationships;

/// <summary>
/// The link between one client and one professional profile inside one tenant (pending → active → ended).
/// Access to a client's data always derives from an active relationship plus, where needed, an active grant. The same
/// client and professional may hold separate relationships in different tenants; each one is independent (MT-03).
/// </summary>
public sealed class CareRelationship : AggregateRoot<Guid>
{
    public const string ProfessionalLeftReason = "The professional left the organization.";

    private readonly List<DataSharingGrant> _grants = [];

    private CareRelationship(
        Guid id, Guid tenantId, Guid professionalProfileId, Guid professionalUserId, Discipline discipline, string inviteeEmail,
        DateTimeOffset invitedAt)
        : base(id)
    {
        TenantId = tenantId;
        ProfessionalProfileId = professionalProfileId;
        ProfessionalUserId = professionalUserId;
        Discipline = discipline;
        InviteeEmail = inviteeEmail;
        InvitedAt = invitedAt;
        Status = RelationshipStatus.Pending;
    }

    private CareRelationship() { }

    /// <summary>The organization the professional acts for; every record created through this relationship carries it.</summary>
    public Guid TenantId { get; private set; }

    public Guid ProfessionalProfileId { get; private set; }

    public Guid ProfessionalUserId { get; private set; }

    public Discipline Discipline { get; private set; }

    public string InviteeEmail { get; private set; } = null!;

    public Guid? ClientId { get; private set; }

    public RelationshipStatus Status { get; private set; }

    public DateTimeOffset InvitedAt { get; private set; }

    public DateTimeOffset? RespondedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public Guid? EndedBy { get; private set; }

    public string? EndReason { get; private set; }

    public IReadOnlyCollection<DataSharingGrant> Grants => _grants.AsReadOnly();

    public bool IsActive => Status == RelationshipStatus.Active;

    public static Result<CareRelationship> Invite(Guid tenantId, ProfessionalProfile professional, string inviteeEmail, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A care relationship always belongs to a tenant.", nameof(tenantId));
        }

        if (!professional.IsVerified)
        {
            return ProfessionalErrors.NotVerified;
        }

        return new CareRelationship(
            Guid.CreateVersion7(now), tenantId, professional.Id, professional.UserId, professional.Discipline,
            UserProfile.NormalizeEmail(inviteeEmail), now);
    }

    public Result Accept(UserProfile client, DateTimeOffset now)
    {
        if (Status != RelationshipStatus.Pending)
        {
            return CareRelationshipErrors.NotPending;
        }

        if (client.Email != InviteeEmail)
        {
            return CareRelationshipErrors.NotInvitee;
        }

        if (client.Id == ProfessionalUserId)
        {
            return CareRelationshipErrors.SelfRelationship;
        }

        ClientId = client.Id;
        Status = RelationshipStatus.Active;
        RespondedAt = now;
        Raise(new CareRelationshipActivated(Id, TenantId, client.Id, ProfessionalUserId, now));
        return Result.Success();
    }

    public Result Decline(UserProfile client, DateTimeOffset now)
    {
        if (Status != RelationshipStatus.Pending)
        {
            return CareRelationshipErrors.NotPending;
        }

        if (client.Email != InviteeEmail)
        {
            return CareRelationshipErrors.NotInvitee;
        }

        Status = RelationshipStatus.Declined;
        RespondedAt = now;
        return Result.Success();
    }

    /// <summary>Ends the relationship. New access stops immediately; existing records are retained.</summary>
    public Result End(Guid actorUserId, string? reason, DateTimeOffset now)
    {
        if (actorUserId != ClientId && actorUserId != ProfessionalUserId)
        {
            return CareRelationshipErrors.NotParticipant;
        }

        return Close(actorUserId, reason, now);
    }

    /// <summary>
    /// Ends the relationship because the professional no longer belongs to the tenant (MT-08). The actor is whoever
    /// revoked the membership, usually not a participant.
    /// </summary>
    public Result EndBecauseMembershipRevoked(Guid revokedBy, DateTimeOffset now) => Close(revokedBy, ProfessionalLeftReason, now);

    public Result Grant(Guid actorUserId, DataCategory category, DateTimeOffset now)
    {
        var check = EnsureClientCanManageSharing(actorUserId);
        if (check.IsFailure)
        {
            return check;
        }

        if (HasActiveGrant(category))
        {
            return Result.Success();
        }

        _grants.Add(new DataSharingGrant(Guid.CreateVersion7(now), category, now));
        return Result.Success();
    }

    public Result Revoke(Guid actorUserId, DataCategory category, DateTimeOffset now)
    {
        var check = EnsureClientCanManageSharing(actorUserId);
        if (check.IsFailure)
        {
            return check;
        }

        foreach (var grant in _grants.Where(g => g.IsActive && g.Category == category))
        {
            grant.Revoke(now);
        }

        return Result.Success();
    }

    public bool HasActiveGrant(DataCategory category) =>
        IsActive && _grants.Any(g => g.IsActive && g.Category == category);

    private Result Close(Guid actorUserId, string? reason, DateTimeOffset now)
    {
        if (Status is not (RelationshipStatus.Active or RelationshipStatus.Pending))
        {
            return CareRelationshipErrors.AlreadyClosed;
        }

        foreach (var grant in _grants.Where(g => g.IsActive))
        {
            grant.Revoke(now);
        }

        Status = RelationshipStatus.Ended;
        EndedAt = now;
        EndedBy = actorUserId;
        EndReason = reason;
        if (ClientId is { } clientId)
        {
            Raise(new CareRelationshipEnded(Id, TenantId, clientId, ProfessionalProfileId, actorUserId, now));
        }

        return Result.Success();
    }

    private Result EnsureClientCanManageSharing(Guid actorUserId)
    {
        if (actorUserId != ClientId)
        {
            return CareRelationshipErrors.OnlyClientManagesSharing;
        }

        return IsActive ? Result.Success() : CareRelationshipErrors.NotActive;
    }
}

public sealed record CareRelationshipActivated(Guid RelationshipId, Guid TenantId, Guid ClientId, Guid ProfessionalUserId, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

/// <summary>Raised when an accepted relationship ends, for whatever reason, so open work tied to it can be closed.</summary>
public sealed record CareRelationshipEnded(
    Guid RelationshipId, Guid TenantId, Guid ClientId, Guid ProfessionalProfileId, Guid EndedByUserId, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

public static class CareRelationshipErrors
{
    public static readonly Error NotFound = Error.NotFound("CareRelationship.NotFound", "The care relationship was not found.");
    public static readonly Error NotPending = Error.Conflict("CareRelationship.NotPending", "The invitation is no longer pending.");
    public static readonly Error NotActive = Error.Conflict("CareRelationship.NotActive", "The care relationship is not active.");
    public static readonly Error AlreadyClosed = Error.Conflict("CareRelationship.AlreadyClosed", "The care relationship is already closed.");
    public static readonly Error NotInvitee = Error.Forbidden("CareRelationship.NotInvitee", "This invitation was sent to a different e-mail address.");
    public static readonly Error NotParticipant = Error.Forbidden("CareRelationship.NotParticipant", "You are not part of this care relationship.");
    public static readonly Error SelfRelationship = Error.Validation("CareRelationship.Self", "A professional cannot be their own client.");
    public static readonly Error OnlyClientManagesSharing = Error.Forbidden("CareRelationship.OnlyClientManagesSharing", "Only the client can change data sharing.");
    public static readonly Error DuplicateInvitation = Error.Conflict("CareRelationship.Duplicate", "There is already a pending or active relationship with this client.");
    public static readonly Error NoActiveRelationship = Error.Forbidden("CareRelationship.NoActiveRelationship", "You do not have an active care relationship with this client.");
    public static readonly Error SharingNotGranted = Error.Forbidden("CareRelationship.SharingNotGranted", "The client has not shared this data with you.");
}
