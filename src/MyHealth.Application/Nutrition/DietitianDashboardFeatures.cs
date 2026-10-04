using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Professionals;

namespace MyHealth.Application.Nutrition;

/// <summary>
/// One of the dietitian's active clients in the active tenant: the plan the client follows, how the last week's diary
/// went (in counts), diary entries still without a comment and the follow-up date of the latest assessment.
/// </summary>
public sealed record DietitianClientSummary(
    Guid ClientId,
    string ClientName,
    Guid RelationshipId,
    Guid? ActivePlanId,
    string? ActivePlanName,
    int DraftPlans,
    int EntriesLast7Days,
    int DaysWithEntriesLast7Days,
    int AsPlannedLast7Days,
    int UncommentedEntries,
    DateTimeOffset? LastEntryAt,
    DateOnly? LastAssessedOn,
    DateOnly? NextFollowUpOn);

public sealed record DietitianDashboardResponse(
    IReadOnlyList<DietitianClientSummary> Clients, int TotalUncommentedEntries, int TotalFollowUpsDue, int ClientsWithoutPlan);

/// <summary>
/// The dietitian's clients page (RF-28 → RF-30): active clients with their plan, diary activity, entries waiting for a
/// comment and follow-ups due. Counts only; the diary itself is read per client (and audited) on the client page.
/// </summary>
public sealed record GetDietitianDashboardQuery : IQuery<DietitianDashboardResponse>;

internal sealed class GetDietitianDashboardHandler(IReadDbContext db, ICareAccess access, TimeProvider clock)
    : IQueryHandler<GetDietitianDashboardQuery, DietitianDashboardResponse>
{
    /// <summary>Entries older than this no longer count as waiting for a comment.</summary>
    private const int CommentWindowDays = 14;

    public async Task<Result<DietitianDashboardResponse>> Handle(GetDietitianDashboardQuery query, CancellationToken cancellationToken)
    {
        var dietitian = await access.GetCurrentProfessionalAsync(Discipline.Dietitian, requireVerified: false, cancellationToken);
        if (dietitian.IsFailure)
        {
            return dietitian.Error;
        }

        var dietitianId = dietitian.Value.Profile.Id;
        var tenantId = dietitian.Value.TenantId;
        var now = clock.GetUtcNow();
        var weekAgo = now.AddDays(-7);
        var commentWindow = now.AddDays(-CommentWindowDays);
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var clients = await (
                from r in db.CareRelationships
                join c in db.UserProfiles on r.ClientId equals c.Id
                where r.TenantId == tenantId && r.ProfessionalProfileId == dietitianId && r.Status == RelationshipStatus.Active
                select new { ClientId = c.Id, c.FullName, RelationshipId = r.Id })
            .ToListAsync(cancellationToken);

        var clientIds = clients.Select(c => c.ClientId).ToList();

        var plans = await db.MealPlans
            .Where(p => p.TenantId == tenantId && p.DietitianProfileId == dietitianId && clientIds.Contains(p.ClientId) && p.Status != MealPlanStatus.Ended)
            .Select(p => new { p.Id, p.ClientId, p.Details.Name, p.Status, p.UpdatedAt })
            .ToListAsync(cancellationToken);

        var entries = await db.FoodDiaryEntries
            .Where(e => e.TenantId == tenantId && e.DietitianProfileId == dietitianId && clientIds.Contains(e.ClientId) && e.EatenAt >= commentWindow)
            .Select(e => new { e.ClientId, e.EatenAt, e.LocalDate, e.Adherence, HasComment = e.Comments.Any() })
            .ToListAsync(cancellationToken);

        var lastEntries = await db.FoodDiaryEntries
            .Where(e => e.TenantId == tenantId && e.DietitianProfileId == dietitianId && clientIds.Contains(e.ClientId))
            .GroupBy(e => e.ClientId)
            .Select(g => new { ClientId = g.Key, LastEntryAt = g.Max(e => e.EatenAt) })
            .ToListAsync(cancellationToken);

        // Content is stored as JSON, so the latest assessment per client is picked in memory.
        var assessments = await db.NutritionAssessments
            .Where(a => a.TenantId == tenantId && a.DietitianProfileId == dietitianId && clientIds.Contains(a.ClientId))
            .Select(a => new { a.ClientId, a.AssessedOn, a.Content, a.CreatedAt })
            .ToListAsync(cancellationToken);

        var summaries = clients.Select(client =>
        {
            var mine = plans.Where(p => p.ClientId == client.ClientId).ToList();
            var active = mine.Where(p => p.Status == MealPlanStatus.Published).MaxBy(p => p.UpdatedAt);
            var recent = entries.Where(e => e.ClientId == client.ClientId).ToList();
            var week = recent.Where(e => e.EatenAt >= weekAgo).ToList();
            var latest = assessments.Where(a => a.ClientId == client.ClientId).OrderBy(a => a.AssessedOn).ThenBy(a => a.CreatedAt).LastOrDefault();
            return new DietitianClientSummary(
                client.ClientId,
                client.FullName,
                client.RelationshipId,
                active?.Id,
                active?.Name,
                mine.Count(p => p.Status == MealPlanStatus.Draft),
                week.Count,
                week.Select(e => e.LocalDate).Distinct().Count(),
                week.Count(e => e.Adherence == MealAdherence.AsPlanned),
                recent.Count(e => !e.HasComment),
                lastEntries.FirstOrDefault(l => l.ClientId == client.ClientId)?.LastEntryAt,
                latest?.AssessedOn,
                latest?.Content.NextFollowUpOn);
        }).OrderByDescending(s => s.UncommentedEntries).ThenBy(s => s.ClientName).ToList();

        return new DietitianDashboardResponse(
            summaries,
            summaries.Sum(s => s.UncommentedEntries),
            summaries.Count(s => s.NextFollowUpOn <= today),
            summaries.Count(s => s.ActivePlanId is null));
    }
}
