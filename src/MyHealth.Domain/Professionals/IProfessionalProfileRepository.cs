using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Professionals;

public interface IProfessionalProfileRepository : IRepository<ProfessionalProfile, Guid>
{
    Task<ProfessionalProfile?> GetByUserAsync(Guid userId, Discipline discipline, CancellationToken cancellationToken = default);
}
