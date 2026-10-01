using FluentValidation;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Application.CareRelationships;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Billing;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Users;

namespace MyHealth.Application.Tenants;

/// <summary>
/// Creates a tenant with the caller as its first administrator (MT-01). An independent professional creates a
/// <see cref="TenantKind.SoloPractice"/>; a clinic or team creates an <see cref="TenantKind.Organization"/>.
/// </summary>
public sealed record CreateTenantCommand(string Name, TenantKind Kind, IReadOnlyList<Discipline> Disciplines) : ICommand<Guid>;

internal sealed class CreateTenantValidator : AbstractValidator<CreateTenantCommand>
{
    public CreateTenantValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Kind).IsInEnum();
        RuleFor(c => c.Disciplines).NotNull();
        RuleForEach(c => c.Disciplines).IsInEnum();
    }
}

internal sealed class CreateTenantHandler(
    IUserContext user, IUserProfileRepository users, ITenantRepository tenants, ITenantMembershipRepository memberships, ITenantSubscriptionRepository subscriptions,
    IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CreateTenantCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateTenantCommand command, CancellationToken cancellationToken)
    {
        var founder = await users.GetByIdAsync(user.UserId, cancellationToken);
        if (founder is null)
        {
            return UserErrors.ProfileNotFound;
        }

        var now = clock.GetUtcNow();
        var tenant = Tenant.Create(command.Name, command.Kind, user.UserId, now);
        var membership = TenantMembership.Founder(tenant, founder, command.Disciplines, now);

        tenants.Add(tenant);
        memberships.Add(membership);

        // Every organization starts with a trial of every module (billing); afterwards it pays for the ones it keeps.
        subscriptions.Add(TenantSubscription.StartTrial(tenant.Id, command.Disciplines.Distinct().Select(BillingModules.For), now));
        audit.Record(AuditActions.Create, nameof(Tenant), tenant.Id, null, tenant.Id);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return tenant.Id;
    }
}

/// <summary>A tenant administrator invites a person by e-mail, with the disciplines they will practise there (RF-05).</summary>
public sealed record InviteMemberCommand(string Email, IReadOnlyList<Discipline> Disciplines, bool IsAdministrator) : ICommand<Guid>;

internal sealed class InviteMemberValidator : AbstractValidator<InviteMemberCommand>
{
    public InviteMemberValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(c => c.Disciplines).NotNull();
        RuleForEach(c => c.Disciplines).IsInEnum();
    }
}

internal sealed class InviteMemberHandler(
    IUserContext user, ITenantContext tenant, ITenantMembershipRepository memberships, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<InviteMemberCommand, Guid>
{
    public async Task<Result<Guid>> Handle(InviteMemberCommand command, CancellationToken cancellationToken)
    {
        var admin = await TenantAdministration.RequireAdministratorAsync(tenant, cancellationToken);
        if (admin.IsFailure)
        {
            return admin.Error;
        }

        var email = UserProfile.NormalizeEmail(command.Email);
        if (await memberships.ExistsOpenAsync(admin.Value.TenantId, email, cancellationToken))
        {
            return MembershipErrors.Duplicate;
        }

        var membership = TenantMembership.Invite(
            admin.Value.TenantId, email, command.Disciplines, command.IsAdministrator, user.UserId, clock.GetUtcNow());
        memberships.Add(membership);
        audit.Record(AuditActions.Invite, nameof(TenantMembership), membership.Id, null, admin.Value.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return membership.Id;
    }
}

public sealed record RespondToMembershipInvitationCommand(Guid MembershipId, InvitationResponse Response) : ICommand;

internal sealed class RespondToMembershipInvitationHandler(
    IUserContext user, IUserProfileRepository users, ITenantMembershipRepository memberships, IAuditLog audit, IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<RespondToMembershipInvitationCommand>
{
    public async Task<Result> Handle(RespondToMembershipInvitationCommand command, CancellationToken cancellationToken)
    {
        var invitee = await users.GetByIdAsync(user.UserId, cancellationToken);
        if (invitee is null)
        {
            return UserErrors.ProfileNotFound;
        }

        var membership = await memberships.GetByIdAsync(command.MembershipId, cancellationToken);
        if (membership is null || membership.InviteeEmail != invitee.Email)
        {
            return MembershipErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        if (command.Response == InvitationResponse.Decline)
        {
            var declined = membership.Decline(invitee, now);
            if (declined.IsSuccess)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return declined;
        }

        // Someone who already belongs to the tenant through another membership must not hold two at once.
        if (await memberships.GetActiveAsync(membership.TenantId, invitee.Id, cancellationToken) is not null)
        {
            return MembershipErrors.Duplicate;
        }

        var accepted = membership.Accept(invitee, now);
        if (accepted.IsFailure)
        {
            return accepted;
        }

        audit.Record(AuditActions.Join, nameof(TenantMembership), membership.Id, null, membership.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return accepted;
    }
}

/// <summary>
/// Removes a person from the tenant (MT-08): an administrator removes anyone, a member may leave. Their open care
/// relationships in the tenant end in the same transaction, so access stops immediately while records stay.
/// </summary>
public sealed record RevokeMembershipCommand(Guid MembershipId, string? Reason) : ICommand;

internal sealed class RevokeMembershipValidator : AbstractValidator<RevokeMembershipCommand>
{
    public RevokeMembershipValidator() => RuleFor(c => c.Reason).MaximumLength(1000);
}

internal sealed class RevokeMembershipHandler(
    IUserContext user, ITenantContext tenant, ITenantMembershipRepository memberships, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<RevokeMembershipCommand>
{
    public async Task<Result> Handle(RevokeMembershipCommand command, CancellationToken cancellationToken)
    {
        var membership = await memberships.GetByIdAsync(command.MembershipId, cancellationToken);
        if (membership is null)
        {
            return MembershipErrors.NotFound;
        }

        var leavingThemselves = membership.UserId == user.UserId;
        if (!leavingThemselves)
        {
            var admin = await TenantAdministration.RequireAdministratorAsync(tenant, cancellationToken);
            if (admin.IsFailure)
            {
                return admin.Error;
            }

            if (admin.Value.TenantId != membership.TenantId)
            {
                await audit.RecordDeniedAsync(nameof(TenantMembership), membership.Id, admin.Value.TenantId, cancellationToken);
                return MembershipErrors.NotFound;
            }
        }

        if (membership is { IsActive: true, IsAdministrator: true }
            && await memberships.CountActiveAdministratorsAsync(membership.TenantId, cancellationToken) <= 1)
        {
            return MembershipErrors.LastAdministrator;
        }

        var result = membership.Revoke(user.UserId, command.Reason, clock.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(AuditActions.Revoke, nameof(TenantMembership), membership.Id, membership.UserId, membership.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

/// <summary>Ends the departing professional's open relationships in the tenant (MT-08); unrelated tenants are untouched.</summary>
internal sealed class MembershipRevokedHandler(ICareRelationshipRepository relationships, IAuditLog audit)
    : IDomainEventHandler<TenantMembershipRevoked>
{
    public async Task Handle(TenantMembershipRevoked domainEvent, CancellationToken cancellationToken)
    {
        var open = await relationships.ListOpenForProfessionalAsync(domainEvent.TenantId, domainEvent.UserId, cancellationToken);
        foreach (var relationship in open)
        {
            if (relationship.EndBecauseMembershipRevoked(domainEvent.RevokedBy, domainEvent.OccurredAt).IsSuccess)
            {
                audit.Record(AuditActions.End, nameof(CareRelationship), relationship.Id, relationship.ClientId, relationship.TenantId);
            }
        }
    }
}

internal static class TenantAdministration
{
    public static async Task<Result<ActiveMembership>> RequireAdministratorAsync(ITenantContext tenant, CancellationToken cancellationToken)
    {
        var membership = await tenant.RequireAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership;
        }

        return membership.Value.IsAdministrator ? membership : TenantErrors.NotAdministrator;
    }
}
