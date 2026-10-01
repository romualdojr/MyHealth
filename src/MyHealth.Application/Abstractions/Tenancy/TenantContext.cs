using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;

namespace MyHealth.Application.Abstractions.Tenancy;

/// <summary>The caller's confirmed, active membership in the tenant selected for this request.</summary>
public sealed record ActiveMembership(Guid TenantId, Guid MembershipId, IReadOnlyCollection<Discipline> Disciplines, bool IsAdministrator)
{
    public bool Practices(Discipline discipline) => Disciplines.Contains(discipline);
}

/// <summary>
/// Resolves the active tenant from the authenticated membership (MT-04): the requested tenant id is honoured only when
/// the caller holds an active membership in it. A request for any other tenant fails as "not found" and is audited.
/// Acting for a tenant — as a professional or a tenant administrator — also requires two-factor authentication (RNF-02).
/// </summary>
public interface ITenantContext
{
    /// <summary>The active membership; fails when no tenant was selected.</summary>
    Task<Result<ActiveMembership>> RequireAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The active membership, or <c>null</c> when no tenant was selected (the client view spans tenants, MT-10).
    /// Fails when a tenant was selected but the caller is not an active member of it.
    /// </summary>
    Task<Result<ActiveMembership?>> FindAsync(CancellationToken cancellationToken);
}

internal sealed class TenantContext(IUserContext user, ITenantMembershipRepository memberships, IAuditLog audit, SecurityPolicy security)
    : ITenantContext
{
    private Result<ActiveMembership?>? _resolved;

    public async Task<Result<ActiveMembership>> RequireAsync(CancellationToken cancellationToken)
    {
        var found = await FindAsync(cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        return found.Value is { } membership ? membership : TenantErrors.Required;
    }

    public async Task<Result<ActiveMembership?>> FindAsync(CancellationToken cancellationToken) =>
        _resolved ??= await ResolveAsync(cancellationToken);

    private async Task<Result<ActiveMembership?>> ResolveAsync(CancellationToken cancellationToken)
    {
        if (user.RequestedTenantId is not { } tenantId)
        {
            return Result.Success<ActiveMembership?>(null);
        }

        var membership = tenantId == Guid.Empty ? null : await memberships.GetActiveAsync(tenantId, user.UserId, cancellationToken);
        if (membership is null)
        {
            await audit.RecordDeniedAsync(nameof(Tenant), tenantId, tenantId == Guid.Empty ? null : tenantId, cancellationToken);
            return TenantErrors.NotFound;
        }

        if (security.RequireTwoFactorForProfessionals && !await user.HasTwoFactorAsync(cancellationToken))
        {
            return SecurityErrors.TwoFactorRequired;
        }

        return new ActiveMembership(membership.TenantId, membership.Id, membership.Disciplines, membership.IsAdministrator);
    }
}
