using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;

namespace MyHealth.Application.Common;

/// <summary>
/// The single place that decides whether the caller may act as a professional on a client's data (RF-03, §6).
/// Every rule is "deny unless": an own profile for the discipline, verified when required, an active relationship
/// and, for shared categories, an active grant from the client.
/// </summary>
public interface ICareAccess
{
    Task<Result<ProfessionalProfile>> GetCurrentProfessionalAsync(Discipline discipline, bool requireVerified, CancellationToken cancellationToken);

    Task<Result<CareRelationship>> GetActiveRelationshipAsync(ProfessionalProfile professional, Guid clientId, CancellationToken cancellationToken);

    Task<Result<CareRelationship>> GetSharedAccessAsync(Guid relationshipId, DataCategory category, CancellationToken cancellationToken);
}

internal sealed class CareAccess(
    IUserContext user,
    IProfessionalProfileRepository professionals,
    ICareRelationshipRepository relationships) : ICareAccess
{
    public async Task<Result<ProfessionalProfile>> GetCurrentProfessionalAsync(
        Discipline discipline, bool requireVerified, CancellationToken cancellationToken)
    {
        var profile = await professionals.GetByUserAsync(user.UserId, discipline, cancellationToken);
        if (profile is null)
        {
            return ProfessionalErrors.MissingProfile(discipline);
        }

        if (requireVerified && !profile.IsVerified)
        {
            return ProfessionalErrors.NotVerified;
        }

        return profile;
    }

    public async Task<Result<CareRelationship>> GetActiveRelationshipAsync(
        ProfessionalProfile professional, Guid clientId, CancellationToken cancellationToken)
    {
        var relationship = await relationships.GetActiveAsync(professional.UserId, professional.Discipline, clientId, cancellationToken);
        return relationship is null ? CareRelationshipErrors.NoActiveRelationship : relationship;
    }

    public async Task<Result<CareRelationship>> GetSharedAccessAsync(
        Guid relationshipId, DataCategory category, CancellationToken cancellationToken)
    {
        var relationship = await relationships.GetByIdAsync(relationshipId, cancellationToken);

        // Not revealing whether a relationship exists to someone outside it.
        if (relationship is null || relationship.ProfessionalUserId != user.UserId)
        {
            return CareRelationshipErrors.NotFound;
        }

        if (!relationship.IsActive)
        {
            return CareRelationshipErrors.NotActive;
        }

        return relationship.HasActiveGrant(category) ? relationship : CareRelationshipErrors.SharingNotGranted;
    }
}
