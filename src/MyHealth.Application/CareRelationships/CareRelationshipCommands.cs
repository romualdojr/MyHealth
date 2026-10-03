using FluentValidation;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Users;

namespace MyHealth.Application.CareRelationships;

public sealed record InviteClientCommand(Discipline Discipline, string ClientEmail) : ICommand<Guid>;

internal sealed class InviteClientValidator : AbstractValidator<InviteClientCommand>
{
    public InviteClientValidator()
    {
        RuleFor(c => c.Discipline).IsInEnum();
        RuleFor(c => c.ClientEmail).NotEmpty().EmailAddress().MaximumLength(320);
    }
}

internal sealed class InviteClientHandler(
    ICareAccess access, ICareRelationshipRepository relationships, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<InviteClientCommand, Guid>
{
    public async Task<Result<Guid>> Handle(InviteClientCommand command, CancellationToken cancellationToken)
    {
        var professional = await access.GetCurrentProfessionalAsync(command.Discipline, requireVerified: true, cancellationToken);
        if (professional.IsFailure)
        {
            return professional.Error;
        }

        var (membership, profile) = professional.Value;
        var email = UserProfile.NormalizeEmail(command.ClientEmail);
        if (await relationships.ExistsOpenAsync(membership.TenantId, profile.Id, email, cancellationToken))
        {
            return CareRelationshipErrors.DuplicateInvitation;
        }

        var relationship = CareRelationship.Invite(membership.TenantId, profile, email, clock.GetUtcNow());
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        relationships.Add(relationship.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return relationship.Value.Id;
    }
}

public enum InvitationResponse
{
    Accept = 1,
    Decline = 2,
}

public sealed record RespondToInvitationCommand(Guid RelationshipId, InvitationResponse Response) : ICommand;

internal sealed class RespondToInvitationHandler(
    IUserContext user, IUserProfileRepository users, ICareRelationshipRepository relationships, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<RespondToInvitationCommand>
{
    public async Task<Result> Handle(RespondToInvitationCommand command, CancellationToken cancellationToken)
    {
        var client = await users.GetByIdAsync(user.UserId, cancellationToken);
        if (client is null)
        {
            return UserErrors.ProfileNotFound;
        }

        var relationship = await relationships.GetByIdAsync(command.RelationshipId, cancellationToken);
        if (relationship is null || relationship.InviteeEmail != client.Email)
        {
            return CareRelationshipErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        var result = command.Response == InvitationResponse.Accept
            ? relationship.Accept(client, now)
            : relationship.Decline(client, now);

        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

public sealed record EndRelationshipCommand(Guid RelationshipId, string? Reason) : ICommand;

internal sealed class EndRelationshipValidator : AbstractValidator<EndRelationshipCommand>
{
    public EndRelationshipValidator() => RuleFor(c => c.Reason).MaximumLength(1000);
}

/// <summary>The client may end a relationship at any time; the professional only while acting for its tenant.</summary>
internal sealed class EndRelationshipHandler(
    IUserContext user, ITenantContext tenant, ICareRelationshipRepository relationships, IAuditLog audit, IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<EndRelationshipCommand>
{
    public async Task<Result> Handle(EndRelationshipCommand command, CancellationToken cancellationToken)
    {
        var relationship = await relationships.GetByIdAsync(command.RelationshipId, cancellationToken);
        if (relationship is null)
        {
            return CareRelationshipErrors.NotFound;
        }

        if (relationship.ClientId != user.UserId)
        {
            var membership = await tenant.FindAsync(cancellationToken);
            if (membership.IsFailure)
            {
                return membership.Error;
            }

            if (relationship.ProfessionalUserId != user.UserId || membership.Value?.TenantId != relationship.TenantId)
            {
                await audit.RecordDeniedAsync(nameof(CareRelationship), relationship.Id, membership.Value?.TenantId, cancellationToken);
                return CareRelationshipErrors.NotFound;
            }
        }

        var result = relationship.End(user.UserId, command.Reason, clock.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(AuditActions.End, nameof(CareRelationship), relationship.Id, relationship.ClientId, relationship.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

/// <summary>The client grants or revokes one data category for one relationship (RF-43).</summary>
public sealed record ChangeDataSharingCommand(Guid RelationshipId, DataCategory Category, bool Shared) : ICommand;

internal sealed class ChangeDataSharingValidator : AbstractValidator<ChangeDataSharingCommand>
{
    public ChangeDataSharingValidator() => RuleFor(c => c.Category).IsInEnum();
}

internal sealed class ChangeDataSharingHandler(
    IUserContext user, ICareRelationshipRepository relationships, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ChangeDataSharingCommand>
{
    public async Task<Result> Handle(ChangeDataSharingCommand command, CancellationToken cancellationToken)
    {
        var relationship = await relationships.GetByIdAsync(command.RelationshipId, cancellationToken);
        if (relationship is null || relationship.ClientId != user.UserId)
        {
            return CareRelationshipErrors.NotFound;
        }

        var now = clock.GetUtcNow();
        var result = command.Shared
            ? relationship.Grant(user.UserId, command.Category, now)
            : relationship.Revoke(user.UserId, command.Category, now);

        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(
            command.Shared ? AuditActions.Grant : AuditActions.Revoke, $"DataSharing.{command.Category}", relationship.Id, user.UserId,
            relationship.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}
