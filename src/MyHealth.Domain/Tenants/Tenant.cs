using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Tenants;

public enum TenantKind
{
    /// <summary>An independent professional's own practice.</summary>
    SoloPractice = 1,

    /// <summary>A clinic, studio or team with several professionals.</summary>
    Organization = 2,
}

/// <summary>
/// The service organization that owns care records (MT-01): a clinic, a training team or a solo practice. Accounts are
/// global; professionals work inside a tenant through a <see cref="TenantMembership"/>.
/// </summary>
public sealed class Tenant : AggregateRoot<Guid>
{
    private Tenant(Guid id) : base(id) { }

    private Tenant() { }

    public string Name { get; private set; } = null!;

    public TenantKind Kind { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Tenant Create(string name, TenantKind kind, Guid createdBy, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now))
        {
            Name = name.Trim(),
            Kind = kind,
            CreatedBy = createdBy,
            CreatedAt = now,
        };
}

public interface ITenantRepository : IRepository<Tenant, Guid>;

public static class TenantErrors
{
    public static readonly Error Required = Error.Validation(
        "Tenant.Required", "Select the organization you are acting for with the X-Tenant-Id header.");

    /// <summary>Also returned when the tenant exists but the caller is not a member, so existence is not revealed.</summary>
    public static readonly Error NotFound = Error.NotFound("Tenant.NotFound", "The organization was not found.");

    public static readonly Error NotAdministrator = Error.Forbidden(
        "Tenant.NotAdministrator", "Only administrators of this organization can perform this action.");

    public static readonly Error DisciplineNotInMembership = Error.Forbidden(
        "Tenant.DisciplineNotInMembership", "Your membership in this organization does not include this discipline.");
}
