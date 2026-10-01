using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;

namespace MyHealth.Application.Common;

/// <summary>The caller acting as a professional: their credentials and the tenant membership they act under.</summary>
public sealed record ActingProfessional(ActiveMembership Membership, ProfessionalProfile Profile)
{
    public Guid TenantId => Membership.TenantId;
}

/// <summary>
/// The single place that decides whether the caller may act as a professional on a client's data (RF-03, §6, MT-05).
/// Every rule is "deny unless": an active membership in the selected tenant that includes the discipline, an own
/// profile for the discipline, verified when required, an active relationship in that same tenant and, for shared
/// categories, an active grant from the client.
/// </summary>
public interface ICareAccess
{
    Task<Result<ActingProfessional>> GetCurrentProfessionalAsync(Discipline discipline, bool requireVerified, CancellationToken cancellationToken);

    Task<Result<CareRelationship>> GetActiveRelationshipAsync(ActingProfessional professional, Guid clientId, CancellationToken cancellationToken);

    Task<Result<CareRelationship>> GetSharedAccessAsync(Guid relationshipId, DataCategory category, CancellationToken cancellationToken);
}

internal sealed class CareAccess(
    IUserContext user,
    ITenantContext tenant,
    IProfessionalProfileRepository professionals,
    ICareRelationshipRepository relationships,
    IAuditLog audit) : ICareAccess
{
    public async Task<Result<ActingProfessional>> GetCurrentProfessionalAsync(
        Discipline discipline, bool requireVerified, CancellationToken cancellationToken)
    {
        var membership = await tenant.RequireAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        if (!membership.Value.Practices(discipline))
        {
            return TenantErrors.DisciplineNotInMembership;
        }

        var profile = await professionals.GetByUserAsync(user.UserId, discipline, cancellationToken);
        if (profile is null)
        {
            return ProfessionalErrors.MissingProfile(discipline);
        }

        if (profile.IsBarred || (requireVerified && !profile.IsVerified))
        {
            return ProfessionalErrors.NotVerified;
        }

        return new ActingProfessional(membership.Value, profile);
    }

    public async Task<Result<CareRelationship>> GetActiveRelationshipAsync(
        ActingProfessional professional, Guid clientId, CancellationToken cancellationToken)
    {
        var relationship = await relationships.GetActiveAsync(
            professional.TenantId, professional.Profile.UserId, professional.Profile.Discipline, clientId, cancellationToken);
        return relationship is null ? CareRelationshipErrors.NoActiveRelationship : relationship;
    }

    public async Task<Result<CareRelationship>> GetSharedAccessAsync(
        Guid relationshipId, DataCategory category, CancellationToken cancellationToken)
    {
        var membership = await tenant.RequireAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        var relationship = await relationships.GetByIdAsync(relationshipId, cancellationToken);

        // Not revealing whether a relationship exists to someone outside it, nor to its professional in another tenant.
        if (relationship is null || relationship.ProfessionalUserId != user.UserId || relationship.TenantId != membership.Value.TenantId)
        {
            if (relationship is not null)
            {
                await audit.RecordDeniedAsync(nameof(CareRelationship), relationshipId, membership.Value.TenantId, cancellationToken);
            }

            return CareRelationshipErrors.NotFound;
        }

        if (!membership.Value.Practices(relationship.Discipline))
        {
            return TenantErrors.DisciplineNotInMembership;
        }

        if (!relationship.IsActive)
        {
            return CareRelationshipErrors.NotActive;
        }

        var profile = await professionals.GetByIdAsync(relationship.ProfessionalProfileId, cancellationToken);
        if (profile is null || profile.IsBarred)
        {
            return ProfessionalErrors.NotVerified;
        }

        return relationship.HasActiveGrant(category) ? relationship : CareRelationshipErrors.SharingNotGranted;
    }
}
