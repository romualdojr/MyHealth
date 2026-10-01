using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Users;

namespace MyHealth.Application.CareRelationships;

public enum RelationshipPerspective
{
    /// <summary>The caller is the client.</summary>
    Client = 1,

    /// <summary>The caller is the professional.</summary>
    Professional = 2,
}

public sealed record CareRelationshipResponse(
    Guid Id,
    RelationshipPerspective Perspective,
    Guid TenantId,
    string OrganizationName,
    Discipline Discipline,
    RelationshipStatus Status,
    Guid ProfessionalProfileId,
    string ProfessionalName,
    Guid? ClientId,
    string? ClientName,
    string InviteeEmail,
    DateTimeOffset InvitedAt,
    DateTimeOffset? RespondedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<DataCategory> SharedCategories);

/// <summary>
/// Relationships where the caller is the client or the invitee, across every tenant and labelled by organization
/// (MT-10), plus — only when a tenant is selected — those where the caller is the professional in that tenant (MT-05).
/// </summary>
public sealed record ListMyRelationshipsQuery : IQuery<IReadOnlyList<CareRelationshipResponse>>;

internal sealed class ListMyRelationshipsHandler(IUserContext user, ITenantContext tenant, IReadDbContext db)
    : IQueryHandler<ListMyRelationshipsQuery, IReadOnlyList<CareRelationshipResponse>>
{
    public async Task<Result<IReadOnlyList<CareRelationshipResponse>>> Handle(
        ListMyRelationshipsQuery query, CancellationToken cancellationToken)
    {
        var membership = await tenant.FindAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        var activeTenantId = membership.Value?.TenantId;
        var email = UserProfile.NormalizeEmail(user.Email);
        var rows = await (
                from r in db.CareRelationships.Include(r => r.Grants)
                join t in db.Tenants on r.TenantId equals t.Id
                join p in db.ProfessionalProfiles on r.ProfessionalProfileId equals p.Id
                join c in db.UserProfiles on r.ClientId equals c.Id into clients
                from c in clients.DefaultIfEmpty()
                where r.ClientId == user.UserId
                      || (r.Status == RelationshipStatus.Pending && r.InviteeEmail == email)
                      || (r.ProfessionalUserId == user.UserId && activeTenantId != null && r.TenantId == activeTenantId)
                orderby r.InvitedAt descending
                select new { Relationship = r, OrganizationName = t.Name, ProfessionalName = p.DisplayName, ClientName = c == null ? null : c.FullName })
            .ToListAsync(cancellationToken);

        return rows.Select(row =>
        {
            var r = row.Relationship;
            return new CareRelationshipResponse(
                r.Id,
                r.ProfessionalUserId == user.UserId && r.TenantId == activeTenantId ? RelationshipPerspective.Professional : RelationshipPerspective.Client,
                r.TenantId,
                row.OrganizationName,
                r.Discipline,
                r.Status,
                r.ProfessionalProfileId,
                row.ProfessionalName,
                r.ClientId,
                row.ClientName,
                r.InviteeEmail,
                r.InvitedAt,
                r.RespondedAt,
                r.EndedAt,
                r.Grants.Where(g => g.IsActive).Select(g => g.Category).Distinct().ToList());
        }).ToList();
    }
}
