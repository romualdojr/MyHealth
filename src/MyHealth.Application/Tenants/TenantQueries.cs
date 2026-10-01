using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Application.Auditing;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Users;

namespace MyHealth.Application.Tenants;

public sealed record CurrentTenantResponse(
    Guid Id, string Name, TenantKind Kind, DateTimeOffset CreatedAt, Guid MembershipId, IReadOnlyCollection<Discipline> Disciplines, bool IsAdministrator);

/// <summary>The tenant selected for this request and the caller's role in it.</summary>
public sealed record GetCurrentTenantQuery : IQuery<CurrentTenantResponse>;

internal sealed class GetCurrentTenantHandler(ITenantContext tenant, IReadDbContext db) : IQueryHandler<GetCurrentTenantQuery, CurrentTenantResponse>
{
    public async Task<Result<CurrentTenantResponse>> Handle(GetCurrentTenantQuery query, CancellationToken cancellationToken)
    {
        var membership = await tenant.RequireAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        var m = membership.Value;
        var row = await db.Tenants.FirstAsync(t => t.Id == m.TenantId, cancellationToken);
        return new CurrentTenantResponse(row.Id, row.Name, row.Kind, row.CreatedAt, m.MembershipId, m.Disciplines, m.IsAdministrator);
    }
}

public sealed record MembershipResponse(
    Guid Id,
    Guid? UserId,
    string Email,
    string? FullName,
    IReadOnlyList<Discipline> Disciplines,
    bool IsAdministrator,
    MembershipStatus Status,
    DateTimeOffset InvitedAt,
    DateTimeOffset? JoinedAt,
    DateTimeOffset? RevokedAt);

/// <summary>Members and invitations of the selected tenant. Administrators only; contains no clinical data.</summary>
public sealed record ListMembershipsQuery : IQuery<IReadOnlyList<MembershipResponse>>;

internal sealed class ListMembershipsHandler(ITenantContext tenant, IReadDbContext db)
    : IQueryHandler<ListMembershipsQuery, IReadOnlyList<MembershipResponse>>
{
    public async Task<Result<IReadOnlyList<MembershipResponse>>> Handle(ListMembershipsQuery query, CancellationToken cancellationToken)
    {
        var admin = await TenantAdministration.RequireAdministratorAsync(tenant, cancellationToken);
        if (admin.IsFailure)
        {
            return admin.Error;
        }

        var tenantId = admin.Value.TenantId;
        var rows = await (
                from m in db.TenantMemberships
                join u in db.UserProfiles on m.UserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                where m.TenantId == tenantId
                orderby m.InvitedAt
                select new { Membership = m, FullName = u == null ? null : u.FullName })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new MembershipResponse(
                r.Membership.Id, r.Membership.UserId, r.Membership.InviteeEmail, r.FullName, r.Membership.Disciplines,
                r.Membership.IsAdministrator, r.Membership.Status, r.Membership.InvitedAt, r.Membership.JoinedAt, r.Membership.RevokedAt))
            .ToList();
    }
}

public sealed record TenantInvitationResponse(
    Guid MembershipId, Guid TenantId, string TenantName, IReadOnlyList<Discipline> Disciplines, bool IsAdministrator, DateTimeOffset InvitedAt);

/// <summary>Pending invitations to join tenants, addressed to the caller's e-mail.</summary>
public sealed record ListMyTenantInvitationsQuery : IQuery<IReadOnlyList<TenantInvitationResponse>>;

internal sealed class ListMyTenantInvitationsHandler(IUserContext user, IReadDbContext db)
    : IQueryHandler<ListMyTenantInvitationsQuery, IReadOnlyList<TenantInvitationResponse>>
{
    public async Task<Result<IReadOnlyList<TenantInvitationResponse>>> Handle(ListMyTenantInvitationsQuery query, CancellationToken cancellationToken)
    {
        var email = UserProfile.NormalizeEmail(user.Email);
        var rows = await (
                from m in db.TenantMemberships
                join t in db.Tenants on m.TenantId equals t.Id
                where m.InviteeEmail == email && m.Status == MembershipStatus.Pending
                orderby m.InvitedAt descending
                select new { Membership = m, t.Name })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new TenantInvitationResponse(
                r.Membership.Id, r.Membership.TenantId, r.Name, r.Membership.Disciplines, r.Membership.IsAdministrator, r.Membership.InvitedAt))
            .ToList();
    }
}

/// <summary>The selected tenant's audit trail for its administrators (RF-50): metadata only, never clinical content.</summary>
public sealed record ListTenantAuditEntriesQuery(Guid? SubjectUserId, Guid? ActorUserId, int Page = 1, int PageSize = 100)
    : IQuery<PagedResponse<AuditEntryResponse>>;

internal sealed class ListTenantAuditEntriesValidator : AbstractValidator<ListTenantAuditEntriesQuery>
{
    public ListTenantAuditEntriesValidator()
    {
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 500);
    }
}

internal sealed class ListTenantAuditEntriesHandler(ITenantContext tenant, IReadDbContext db)
    : IQueryHandler<ListTenantAuditEntriesQuery, PagedResponse<AuditEntryResponse>>
{
    public async Task<Result<PagedResponse<AuditEntryResponse>>> Handle(ListTenantAuditEntriesQuery query, CancellationToken cancellationToken)
    {
        var admin = await TenantAdministration.RequireAdministratorAsync(tenant, cancellationToken);
        if (admin.IsFailure)
        {
            return admin.Error;
        }

        return await AuditPaging.PageAsync(
            db.AuditEntries.Where(e => e.TenantId == admin.Value.TenantId), query.SubjectUserId, query.ActorUserId, query.Page, query.PageSize,
            cancellationToken);
    }
}

public sealed record TenantSummaryResponse(Guid Id, string Name, TenantKind Kind, DateTimeOffset CreatedAt, int ActiveMembers);

/// <summary>Platform operators list tenants (RF-50). Metadata only.</summary>
public sealed record ListTenantsQuery : IQuery<IReadOnlyList<TenantSummaryResponse>>;

internal sealed class ListTenantsHandler(IReadDbContext db) : IQueryHandler<ListTenantsQuery, IReadOnlyList<TenantSummaryResponse>>
{
    public async Task<Result<IReadOnlyList<TenantSummaryResponse>>> Handle(ListTenantsQuery query, CancellationToken cancellationToken)
    {
        var items = await db.Tenants
            .OrderBy(t => t.CreatedAt)
            .Select(t => new TenantSummaryResponse(
                t.Id, t.Name, t.Kind, t.CreatedAt,
                db.TenantMemberships.Count(m => m.TenantId == t.Id && m.Status == MembershipStatus.Active)))
            .ToListAsync(cancellationToken);

        return items;
    }
}
