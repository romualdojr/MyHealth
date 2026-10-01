using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.CareRelationships;

public interface ICareRelationshipRepository : IRepository<CareRelationship, Guid>
{
    Task<bool> ExistsOpenAsync(Guid tenantId, Guid professionalProfileId, string inviteeEmail, CancellationToken cancellationToken = default);

    Task<CareRelationship?> GetActiveAsync(
        Guid tenantId, Guid professionalUserId, Discipline discipline, Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>Pending and active relationships a professional holds in one tenant.</summary>
    Task<IReadOnlyList<CareRelationship>> ListOpenForProfessionalAsync(
        Guid tenantId, Guid professionalUserId, CancellationToken cancellationToken = default);
}
