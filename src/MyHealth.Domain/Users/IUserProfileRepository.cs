using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Users;

public interface IUserProfileRepository : IRepository<UserProfile, Guid>
{
    Task<UserProfile?> GetByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default);
}
