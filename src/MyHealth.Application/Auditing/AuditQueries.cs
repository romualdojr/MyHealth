using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;

namespace MyHealth.Application.Auditing;

public sealed record AuditEntryResponse(
    Guid Id, DateTimeOffset OccurredAt, Guid ActorUserId, string Action, string ResourceType, Guid ResourceId, Guid? SubjectUserId);

/// <summary>Administrators audit who accessed what — metadata only, no clinical content (RF-50, RNF-03).</summary>
public sealed record ListAuditEntriesQuery(Guid? SubjectUserId, Guid? ActorUserId, int Page = 1, int PageSize = 100)
    : IQuery<PagedResponse<AuditEntryResponse>>;

internal sealed class ListAuditEntriesValidator : AbstractValidator<ListAuditEntriesQuery>
{
    public ListAuditEntriesValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 500);
    }
}

internal sealed class ListAuditEntriesHandler(IReadDbContext db) : IQueryHandler<ListAuditEntriesQuery, PagedResponse<AuditEntryResponse>>
{
    public async Task<Result<PagedResponse<AuditEntryResponse>>> Handle(ListAuditEntriesQuery query, CancellationToken cancellationToken)
    {
        var entries = db.AuditEntries.AsQueryable();
        if (query.SubjectUserId is { } subject)
        {
            entries = entries.Where(e => e.SubjectUserId == subject);
        }

        if (query.ActorUserId is { } actor)
        {
            entries = entries.Where(e => e.ActorUserId == actor);
        }

        var total = await entries.CountAsync(cancellationToken);
        var items = await entries
            .OrderByDescending(e => e.OccurredAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(e => new AuditEntryResponse(e.Id, e.OccurredAt, e.ActorUserId, e.Action, e.ResourceType, e.ResourceId, e.SubjectUserId))
            .ToListAsync(cancellationToken);

        return new PagedResponse<AuditEntryResponse>(items, query.Page, query.PageSize, total);
    }
}
