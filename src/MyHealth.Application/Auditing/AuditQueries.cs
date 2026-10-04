using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;

namespace MyHealth.Application.Auditing;

public sealed record AuditEntryResponse(
    Guid Id, DateTimeOffset OccurredAt, Guid ActorUserId, string Action, string ResourceType, Guid ResourceId, Guid? SubjectUserId, Guid? TenantId);

/// <summary>Platform operators audit who accessed what — metadata only, no clinical content (RF-50, RNF-03).</summary>
/// <param name="Area"><c>billing</c> for subscriptions, invoices, payments and prices only (the back office's billing audit).</param>
public sealed record ListAuditEntriesQuery(Guid? SubjectUserId, Guid? ActorUserId, Guid? TenantId = null, int Page = 1, int PageSize = 100, string? Area = null)
    : IQuery<PagedResponse<AuditEntryResponse>>;

internal sealed class ListAuditEntriesValidator : AbstractValidator<ListAuditEntriesQuery>
{
    public ListAuditEntriesValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 500);
        RuleFor(q => q.Area).Must(a => a is null or AuditAreas.Billing).WithMessage("Unknown area.");
    }
}

internal sealed class ListAuditEntriesHandler(IReadDbContext db) : IQueryHandler<ListAuditEntriesQuery, PagedResponse<AuditEntryResponse>>
{
    public async Task<Result<PagedResponse<AuditEntryResponse>>> Handle(ListAuditEntriesQuery query, CancellationToken cancellationToken)
    {
        var entries = db.AuditEntries.AsQueryable();
        if (query.TenantId is { } tenantId)
        {
            entries = entries.Where(e => e.TenantId == tenantId);
        }

        if (query.Area == AuditAreas.Billing)
        {
            entries = entries.Where(e => AuditAreas.BillingResources.Contains(e.ResourceType) || e.ResourceType.StartsWith("ModulePrice."));
        }

        return await AuditPaging.PageAsync(entries, query.SubjectUserId, query.ActorUserId, query.Page, query.PageSize, cancellationToken);
    }
}

internal static class AuditPaging
{
    public static async Task<PagedResponse<AuditEntryResponse>> PageAsync(
        IQueryable<AuditEntry> entries, Guid? subjectUserId, Guid? actorUserId, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (subjectUserId is { } subject)
        {
            entries = entries.Where(e => e.SubjectUserId == subject);
        }

        if (actorUserId is { } actor)
        {
            entries = entries.Where(e => e.ActorUserId == actor);
        }

        var total = await entries.CountAsync(cancellationToken);
        var items = await entries
            .OrderByDescending(e => e.OccurredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new AuditEntryResponse(e.Id, e.OccurredAt, e.ActorUserId, e.Action, e.ResourceType, e.ResourceId, e.SubjectUserId, e.TenantId))
            .ToListAsync(cancellationToken);

        return new PagedResponse<AuditEntryResponse>(items, page, pageSize, total);
    }
}

internal static class AuditAreas
{
    public const string Billing = "billing";

    public static readonly string[] BillingResources = ["TenantSubscription", "Invoice", "Invoice.Charge", "PaymentEvent"];
}
