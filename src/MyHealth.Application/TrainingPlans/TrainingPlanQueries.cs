using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Application.TrainingPlans;

public sealed record PlanVersionSummary(Guid VersionId, int VersionNumber, string ChangeDescription, DateTimeOffset PublishedAt);

public sealed record TrainerPlanResponse(
    Guid Id,
    Guid ClientId,
    PlanDetails Details,
    PlanStatus Status,
    PlanContent Draft,
    bool HasUnpublishedChanges,
    int CurrentVersionNumber,
    Guid? CopiedFromPlanId,
    IReadOnlyList<PlanVersionSummary> Versions);

/// <summary>The trainer's full view of a plan, including the unpublished draft.</summary>
public sealed record GetTrainerPlanQuery(Guid PlanId) : IQuery<TrainerPlanResponse>;

internal sealed class GetTrainerPlanHandler(IReadDbContext db, ICareAccess access) : IQueryHandler<GetTrainerPlanQuery, TrainerPlanResponse>
{
    public async Task<Result<TrainerPlanResponse>> Handle(GetTrainerPlanQuery query, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: false, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var plan = await db.TrainingPlans.FirstOrDefaultAsync(
            p => p.Id == query.PlanId && p.TrainerProfileId == trainer.Value.Profile.Id && p.TenantId == trainer.Value.TenantId, cancellationToken);
        if (plan is null)
        {
            return PlanErrors.NotFound;
        }

        var versions = await db.TrainingPlanVersions
            .Where(v => v.TrainingPlanId == plan.Id)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new PlanVersionSummary(v.Id, v.VersionNumber, v.ChangeDescription, v.PublishedAt))
            .ToListAsync(cancellationToken);

        return new TrainerPlanResponse(
            plan.Id, plan.ClientId, plan.Details, plan.Status, plan.Draft, plan.HasUnpublishedChanges, plan.CurrentVersionNumber,
            plan.CopiedFromPlanId, versions);
    }
}

public sealed record TrainerPlanListItem(Guid Id, Guid ClientId, string Name, PlanStatus Status, int CurrentVersionNumber, bool HasUnpublishedChanges, DateTimeOffset UpdatedAt);

public sealed record ListTrainerPlansQuery(Guid? ClientId) : IQuery<IReadOnlyList<TrainerPlanListItem>>;

internal sealed class ListTrainerPlansHandler(IReadDbContext db, ICareAccess access)
    : IQueryHandler<ListTrainerPlansQuery, IReadOnlyList<TrainerPlanListItem>>
{
    public async Task<Result<IReadOnlyList<TrainerPlanListItem>>> Handle(ListTrainerPlansQuery query, CancellationToken cancellationToken)
    {
        var trainer = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: false, cancellationToken);
        if (trainer.IsFailure)
        {
            return trainer.Error;
        }

        var plans = db.TrainingPlans.Where(p => p.TrainerProfileId == trainer.Value.Profile.Id && p.TenantId == trainer.Value.TenantId);
        if (query.ClientId is { } clientId)
        {
            plans = plans.Where(p => p.ClientId == clientId);
        }

        var items = await plans
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new TrainerPlanListItem(p.Id, p.ClientId, p.Details.Name, p.Status, p.CurrentVersionNumber, p.HasUnpublishedChanges, p.UpdatedAt))
            .ToListAsync(cancellationToken);

        return items;
    }
}

public sealed record PlanVersionResponse(
    Guid PlanId,
    Guid VersionId,
    int VersionNumber,
    PlanDetails Details,
    PlanContent Content,
    string ChangeDescription,
    DateTimeOffset PublishedAt);

/// <summary>
/// Any published version; readable by the client it was prescribed to, or by the owning trainer while acting for the
/// plan's tenant.
/// </summary>
public sealed record GetPlanVersionQuery(Guid PlanId, int VersionNumber) : IQuery<PlanVersionResponse>;

internal sealed class GetPlanVersionHandler(IReadDbContext db, IUserContext user, ITenantContext tenant) : IQueryHandler<GetPlanVersionQuery, PlanVersionResponse>
{
    public async Task<Result<PlanVersionResponse>> Handle(GetPlanVersionQuery query, CancellationToken cancellationToken)
    {
        var membership = await tenant.FindAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        var activeTenantId = membership.Value?.TenantId;
        var version = await db.TrainingPlanVersions
            .FirstOrDefaultAsync(
                v => v.TrainingPlanId == query.PlanId && v.VersionNumber == query.VersionNumber
                     && (v.ClientId == user.UserId || (v.TrainerUserId == user.UserId && v.TenantId == activeTenantId)),
                cancellationToken);

        return version is null
            ? PlanErrors.VersionNotFound
            : new PlanVersionResponse(version.TrainingPlanId, version.Id, version.VersionNumber, version.Details, version.Content, version.ChangeDescription, version.PublishedAt);
    }
}

/// <summary>A plan in the client's own view, labelled with the organization and trainer responsible for it (MT-10).</summary>
public sealed record ClientPlanListItem(
    Guid PlanId,
    Guid TenantId,
    string OrganizationName,
    string TrainerName,
    string Name,
    string Goal,
    TrainingLevel Level,
    DateOnly StartDate,
    DateOnly? EndDate,
    PlanStatus Status,
    int VersionNumber,
    DateTimeOffset PublishedAt);

/// <summary>The client's plans from every tenant. Drafts are never visible to clients.</summary>
public sealed record ListMyPlansQuery : IQuery<IReadOnlyList<ClientPlanListItem>>;

internal sealed class ListMyPlansHandler(IReadDbContext db, IUserContext user) : IQueryHandler<ListMyPlansQuery, IReadOnlyList<ClientPlanListItem>>
{
    public async Task<Result<IReadOnlyList<ClientPlanListItem>>> Handle(ListMyPlansQuery query, CancellationToken cancellationToken)
    {
        var items = await (
                from plan in db.TrainingPlans
                join version in db.TrainingPlanVersions on plan.CurrentVersionId equals version.Id
                join tenant in db.Tenants on plan.TenantId equals tenant.Id
                join trainer in db.ProfessionalProfiles on plan.TrainerProfileId equals trainer.Id
                where plan.ClientId == user.UserId
                orderby plan.Status, version.Details.StartDate descending
                select new ClientPlanListItem(
                    plan.Id, plan.TenantId, tenant.Name, trainer.DisplayName, version.Details.Name, version.Details.Goal, version.Details.Level, version.Details.StartDate,
                    version.Details.EndDate, plan.Status, version.VersionNumber, version.PublishedAt))
            .ToListAsync(cancellationToken);

        return items;
    }
}

public sealed record SessionStats(int CompletedCount, DateTimeOffset? LastCompletedAt, int? LastPerceivedExertion, bool HasTrainerReply);

public sealed record ClientPlanSession(PlannedSession Session, SessionStats Stats);

public sealed record ClientPlanResponse(
    Guid PlanId,
    Guid TenantId,
    string OrganizationName,
    PlanStatus Status,
    Guid VersionId,
    int VersionNumber,
    string ChangeDescription,
    DateTimeOffset PublishedAt,
    PlanDetails Details,
    IReadOnlyList<ClientPlanSession> Sessions);

/// <summary>The current published version with per-session execution count, last execution and feedback (RF-17).</summary>
public sealed record GetMyPlanQuery(Guid PlanId) : IQuery<ClientPlanResponse>;

internal sealed class GetMyPlanHandler(IReadDbContext db, IUserContext user) : IQueryHandler<GetMyPlanQuery, ClientPlanResponse>
{
    public async Task<Result<ClientPlanResponse>> Handle(GetMyPlanQuery query, CancellationToken cancellationToken)
    {
        var row = await (
                from plan in db.TrainingPlans
                join version in db.TrainingPlanVersions on plan.CurrentVersionId equals version.Id
                join tenant in db.Tenants on plan.TenantId equals tenant.Id
                where plan.Id == query.PlanId && plan.ClientId == user.UserId
                select new { plan.Status, plan.TenantId, OrganizationName = tenant.Name, Version = version })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return PlanErrors.NotFound;
        }

        var completed = await db.WorkoutSessions
            .Where(s => s.TrainingPlanId == query.PlanId && s.ClientId == user.UserId && s.Status == WorkoutStatus.Completed)
            .Select(s => new { s.PlannedSessionId, s.CompletedAt, s.Feedback!.PerceivedExertion, HasReply = s.Reply != null })
            .ToListAsync(cancellationToken);

        var sessions = row.Version.Content.Sessions.OrderBy(s => s.Order).Select(session =>
        {
            var executions = completed.Where(c => c.PlannedSessionId == session.Id).OrderByDescending(c => c.CompletedAt).ToList();
            var last = executions.FirstOrDefault();
            return new ClientPlanSession(session, new SessionStats(executions.Count, last?.CompletedAt, last?.PerceivedExertion, last?.HasReply ?? false));
        }).ToList();

        var v = row.Version;
        return new ClientPlanResponse(v.TrainingPlanId, row.TenantId, row.OrganizationName, row.Status, v.Id, v.VersionNumber, v.ChangeDescription, v.PublishedAt, v.Details, sessions);
    }
}
