using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Billing;
using MyHealth.Domain.Tenants;

namespace MyHealth.Application.Billing;

public sealed record ModulePriceItem(BillingModule Module, long? MonthlyAmountCents, DateTimeOffset? EffectiveFrom, IReadOnlyList<PriceChange> History);

public sealed record PriceChange(long MonthlyAmountCents, DateTimeOffset EffectiveFrom, DateTimeOffset SetAt);

public sealed record ChargeResponse(string Provider, string? PixCopyPaste, string? CheckoutUrl, DateTimeOffset ExpiresAt);

public sealed record InvoiceResponse(
    Guid Id,
    Guid TenantId,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    IReadOnlyList<InvoiceLine> Lines,
    long TotalCents,
    string Currency,
    DateTimeOffset IssuedAt,
    DateTimeOffset DueAt,
    InvoiceStatus Status,
    bool IsOverdue,
    DateTimeOffset? PaidAt,
    string? PaymentReference,
    ChargeResponse? Charge,
    string? VoidReason);

public sealed record TenantBillingItem(
    Guid TenantId,
    string Name,
    TenantKind Kind,
    SubscriptionStatus Status,
    IReadOnlyList<BillingModule> Modules,
    long MonthlyAmountCents,
    long OpenAmountCents,
    long OverdueAmountCents,
    DateTimeOffset TrialEndsAt,
    DateTimeOffset NextInvoiceAt,
    DateTimeOffset? LastPaidAt,
    DateTimeOffset CreatedAt);

public sealed record TenantBillingDetail(
    TenantBillingItem Summary, IReadOnlyList<string> AdministratorEmails, IReadOnlyList<SubscriptionChange> History, IReadOnlyList<InvoiceResponse> Invoices);

public sealed record RevenueSummary(
    long MonthlyRecurringRevenueCents,
    IReadOnlyDictionary<SubscriptionStatus, int> TenantsByStatus,
    long OpenAmountCents,
    long OverdueAmountCents,
    long PaidLast30DaysCents,
    int PaymentsLast30Days,
    IReadOnlyList<RecentPayment> RecentPayments);

public sealed record RecentPayment(Guid InvoiceId, Guid TenantId, string TenantName, long AmountCents, DateTimeOffset PaidAt, string? Reference);

/// <summary>Module prices in force now, with their history (back office).</summary>
public sealed record ListModulePricesQuery : IQuery<IReadOnlyList<ModulePriceItem>>;

internal sealed class ListModulePricesHandler(IReadDbContext db, TimeProvider clock) : IQueryHandler<ListModulePricesQuery, IReadOnlyList<ModulePriceItem>>
{
    public async Task<Result<IReadOnlyList<ModulePriceItem>>> Handle(ListModulePricesQuery query, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var prices = await db.ModulePrices.OrderByDescending(p => p.EffectiveFrom).ToListAsync(cancellationToken);
        return BillingModules.All.Select(module =>
        {
            var history = prices.Where(p => p.Module == module).ToList();
            var current = history.FirstOrDefault(p => p.EffectiveFrom <= now);
            return new ModulePriceItem(
                module, current?.MonthlyAmountCents, current?.EffectiveFrom,
                history.Select(p => new PriceChange(p.MonthlyAmountCents, p.EffectiveFrom, p.SetAt)).ToList());
        }).ToList();
    }
}

/// <summary>Every tenant with its subscription and money situation (back office), optionally by status.</summary>
public sealed record ListTenantBillingQuery(SubscriptionStatus? Status) : IQuery<IReadOnlyList<TenantBillingItem>>;

internal sealed class ListTenantBillingHandler(IReadDbContext db, TimeProvider clock) : IQueryHandler<ListTenantBillingQuery, IReadOnlyList<TenantBillingItem>>
{
    public async Task<Result<IReadOnlyList<TenantBillingItem>>> Handle(ListTenantBillingQuery query, CancellationToken cancellationToken) =>
        Result.Success(await BillingMapping.TenantsAsync(db, clock.GetUtcNow(), query.Status, tenantId: null, cancellationToken));
}

public sealed record GetTenantBillingQuery(Guid TenantId) : IQuery<TenantBillingDetail>;

internal sealed class GetTenantBillingHandler(IReadDbContext db, TimeProvider clock) : IQueryHandler<GetTenantBillingQuery, TenantBillingDetail>
{
    public async Task<Result<TenantBillingDetail>> Handle(GetTenantBillingQuery query, CancellationToken cancellationToken)
    {
        var detail = await BillingMapping.DetailAsync(db, clock.GetUtcNow(), query.TenantId, cancellationToken);
        return detail is null ? BillingErrors.SubscriptionNotFound : detail;
    }
}

/// <summary>
/// The selected tenant's plan and invoices, for its administrators ("Plano e cobrança"). Works while suspended, so the
/// organization can always pay.
/// </summary>
public sealed record GetMyTenantBillingQuery : IQuery<TenantBillingDetail>;

internal sealed class GetMyTenantBillingHandler(ITenantContext tenant, IReadDbContext db, TimeProvider clock) : IQueryHandler<GetMyTenantBillingQuery, TenantBillingDetail>
{
    public async Task<Result<TenantBillingDetail>> Handle(GetMyTenantBillingQuery query, CancellationToken cancellationToken)
    {
        var admin = await BillingMapping.RequireTenantAdministratorAsync(tenant, cancellationToken);
        if (admin.IsFailure)
        {
            return admin.Error;
        }

        var detail = await BillingMapping.DetailAsync(db, clock.GetUtcNow(), admin.Value.TenantId, cancellationToken);
        return detail is null ? BillingErrors.SubscriptionNotFound : detail;
    }
}

/// <summary>Monthly recurring revenue, tenants by status, open and overdue amounts, recent payments (back office).</summary>
public sealed record GetRevenueSummaryQuery : IQuery<RevenueSummary>;

internal sealed class GetRevenueSummaryHandler(IReadDbContext db, TimeProvider clock) : IQueryHandler<GetRevenueSummaryQuery, RevenueSummary>
{
    public async Task<Result<RevenueSummary>> Handle(GetRevenueSummaryQuery query, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var tenants = await BillingMapping.TenantsAsync(db, now, status: null, tenantId: null, cancellationToken);
        var since = now.AddDays(-30);
        var recent = await (
                from i in db.Invoices
                join t in db.Tenants on i.TenantId equals t.Id
                where i.Status == InvoiceStatus.Paid && i.PaidAt >= since && i.TotalCents > 0
                orderby i.PaidAt descending
                select new RecentPayment(i.Id, i.TenantId, t.Name, i.TotalCents, i.PaidAt!.Value, i.PaymentReference))
            .ToListAsync(cancellationToken);

        return new RevenueSummary(
            tenants.Where(t => t.Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue).Sum(t => t.MonthlyAmountCents),
            Enum.GetValues<SubscriptionStatus>().ToDictionary(s => s, s => tenants.Count(t => t.Status == s)),
            tenants.Sum(t => t.OpenAmountCents),
            tenants.Sum(t => t.OverdueAmountCents),
            recent.Sum(p => p.AmountCents),
            recent.Count,
            recent.Take(10).ToList());
    }
}

internal static class BillingMapping
{
    public static async Task<Result<ActiveMembership>> RequireTenantAdministratorAsync(ITenantContext tenant, CancellationToken cancellationToken)
    {
        var membership = await tenant.RequireAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership;
        }

        return membership.Value.IsAdministrator ? membership : TenantErrors.NotAdministrator;
    }

    public static InvoiceResponse ToResponse(Invoice i, DateTimeOffset now) =>
        new(
            i.Id, i.TenantId, i.PeriodStart, i.PeriodEnd, i.Lines, i.TotalCents, i.Currency, i.IssuedAt, i.DueAt, i.Status, i.IsOverdue(now), i.PaidAt,
            i.PaymentReference,
            i.Charge is { } c && i.Status == InvoiceStatus.Open ? new ChargeResponse(c.Provider, c.PixCopyPaste, c.CheckoutUrl, c.ExpiresAt) : null,
            i.VoidReason);

    public static async Task<IReadOnlyList<TenantBillingItem>> TenantsAsync(
        IReadDbContext db, DateTimeOffset now, SubscriptionStatus? status, Guid? tenantId, CancellationToken cancellationToken)
    {
        var rows = db.TenantSubscriptions.AsQueryable();
        if (status is { } s)
        {
            rows = rows.Where(r => r.Status == s);
        }

        if (tenantId is { } id)
        {
            rows = rows.Where(r => r.TenantId == id);
        }

        var subscriptions = await (
                from sub in rows
                join t in db.Tenants on sub.TenantId equals t.Id
                orderby t.Name
                select new { Subscription = sub, t.Name, t.Kind })
            .ToListAsync(cancellationToken);

        var tenantIds = subscriptions.Select(x => x.Subscription.TenantId).ToList();
        var invoices = await db.Invoices
            .Where(i => tenantIds.Contains(i.TenantId) && i.Status != InvoiceStatus.Void)
            .Select(i => new { i.TenantId, i.Status, i.TotalCents, i.DueAt, i.PaidAt })
            .ToListAsync(cancellationToken);
        var prices = await CurrentPricesAsync(db, now, cancellationToken);

        return subscriptions.Select(x =>
        {
            var mine = invoices.Where(i => i.TenantId == x.Subscription.TenantId).ToList();
            var open = mine.Where(i => i.Status == InvoiceStatus.Open).ToList();
            return new TenantBillingItem(
                x.Subscription.TenantId, x.Name, x.Kind, x.Subscription.Status, x.Subscription.Modules,
                x.Subscription.Modules.Sum(m => prices.GetValueOrDefault(m)),
                open.Sum(i => i.TotalCents),
                open.Where(i => i.DueAt < now).Sum(i => i.TotalCents),
                x.Subscription.TrialEndsAt, x.Subscription.NextInvoiceAt,
                mine.Where(i => i.Status == InvoiceStatus.Paid && i.TotalCents > 0).Max(i => i.PaidAt),
                x.Subscription.CreatedAt);
        }).ToList();
    }

    public static async Task<TenantBillingDetail?> DetailAsync(IReadDbContext db, DateTimeOffset now, Guid tenantId, CancellationToken cancellationToken)
    {
        var summary = (await TenantsAsync(db, now, status: null, tenantId, cancellationToken)).SingleOrDefault();
        if (summary is null)
        {
            return null;
        }

        var subscription = await db.TenantSubscriptions.FirstAsync(s => s.TenantId == tenantId, cancellationToken);
        var invoices = await db.Invoices.Where(i => i.TenantId == tenantId).OrderByDescending(i => i.PeriodStart).Take(36).ToListAsync(cancellationToken);
        var administrators = await (
                from m in db.TenantMemberships
                join u in db.UserProfiles on m.UserId equals u.Id
                where m.TenantId == tenantId && m.Status == MembershipStatus.Active && m.IsAdministrator
                select u.Email)
            .ToListAsync(cancellationToken);

        return new TenantBillingDetail(
            summary, administrators, subscription.History.OrderByDescending(h => h.At).ToList(), invoices.Select(i => ToResponse(i, now)).ToList());
    }

    public static async Task<IReadOnlyDictionary<BillingModule, long>> CurrentPricesAsync(IReadDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var prices = await db.ModulePrices.Where(p => p.EffectiveFrom <= now).ToListAsync(cancellationToken);
        return prices.GroupBy(p => p.Module).ToDictionary(g => g.Key, g => g.MaxBy(p => p.EffectiveFrom)!.MonthlyAmountCents);
    }
}
