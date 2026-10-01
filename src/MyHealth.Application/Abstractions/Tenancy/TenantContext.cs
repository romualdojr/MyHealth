using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Billing;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;

namespace MyHealth.Application.Abstractions.Tenancy;

/// <summary>The caller's confirmed, active membership in the tenant selected for this request.</summary>
public sealed record ActiveMembership(
    Guid TenantId, Guid MembershipId, IReadOnlyCollection<Discipline> Disciplines, bool IsAdministrator, TenantAccess Access)
{
    /// <summary>Has the discipline here and the subscription allows it (not suspended, module subscribed).</summary>
    public bool Practices(Discipline discipline) => Disciplines.Contains(discipline) && Access.Allows(discipline);
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

internal sealed class TenantContext(
    IUserContext user, ITenantMembershipRepository memberships, ITenantSubscriptionRepository subscriptions, IAuditLog audit, SecurityPolicy security)
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

        var subscription = await subscriptions.GetByTenantAsync(membership.TenantId, cancellationToken);
        return new ActiveMembership(membership.TenantId, membership.Id, membership.Disciplines, membership.IsAdministrator, TenantAccess.From(subscription));
    }
}

/// <summary>
/// What the tenant's subscription allows (§2 modules, billing suspension). Tenant administration (members, billing) is
/// never blocked, so an administrator can always settle a suspended subscription; care work is.
/// </summary>
public sealed record TenantAccess(SubscriptionStatus Status, IReadOnlyCollection<BillingModule> Modules)
{
    /// <summary>A tenant without a subscription row is treated as suspended: billing must exist before care does.</summary>
    public static readonly TenantAccess Missing = new(SubscriptionStatus.Suspended, []);

    public bool BlocksProfessionals => Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled;

    public static TenantAccess From(TenantSubscription? subscription) =>
        subscription is null ? Missing : new TenantAccess(subscription.Status, subscription.Modules.ToList());

    public bool Allows(Discipline discipline) => TenantSubscription.Permits(Status, Modules, discipline);

    /// <summary>Why <paramref name="discipline"/> is not allowed, or <c>null</c> when it is.</summary>
    public Error? Denial(Discipline discipline) =>
        BlocksProfessionals ? BillingErrors.Suspended
        : Allows(discipline) ? null
        : BillingErrors.ModuleNotSubscribed(BillingModules.For(discipline));
}
