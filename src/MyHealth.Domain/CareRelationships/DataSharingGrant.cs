using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.CareRelationships;

/// <summary>One grant period. Re-granting after a revocation creates a new row, so the full history is kept.</summary>
public sealed class DataSharingGrant : Entity<Guid>
{
    internal DataSharingGrant(Guid id, DataCategory category, DateTimeOffset grantedAt) : base(id)
    {
        Category = category;
        GrantedAt = grantedAt;
    }

    private DataSharingGrant() { }

    public DataCategory Category { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => RevokedAt is null;

    internal void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
