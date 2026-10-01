using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.CareRelationships;

public interface ICareRelationshipRepository : IRepository<CareRelationship, Guid>
{
    Task<bool> ExistsOpenAsync(Guid professionalProfileId, string inviteeEmail, CancellationToken cancellationToken = default);

    Task<CareRelationship?> GetActiveAsync(Guid professionalUserId, Discipline discipline, Guid clientId, CancellationToken cancellationToken = default);
}
