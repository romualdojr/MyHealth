using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Therapies;

namespace MyHealth.Application.Therapies;

public sealed record CarePlanResponse(
    Guid Id,
    Guid TenantId,
    string OrganizationName,
    Guid ClientId,
    Guid PractitionerProfileId,
    string PractitionerName,
    string ModalityCode,
    string Goals,
    int? PlannedSessions,
    string? Frequency,
    DateOnly? NextReviewOn,
    CarePlanStatus Status,
    IReadOnlyList<CarePlanReview> Reviews,
    DateTimeOffset CreatedAt);

/// <summary>The practitioner starts a care plan for a client linked to them in the active tenant (RF-36).</summary>
public sealed record StartCarePlanCommand(Guid ClientId, string ModalityCode, string Goals, int? PlannedSessions, string? Frequency, DateOnly? NextReviewOn)
    : ICommand<Guid>;

internal sealed class StartCarePlanValidator : AbstractValidator<StartCarePlanCommand>
{
    public StartCarePlanValidator()
    {
        RuleFor(c => c.ClientId).NotEmpty();
        RuleFor(c => c.ModalityCode).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Goals).NotEmpty().MaximumLength(4000);
        RuleFor(c => c.PlannedSessions).InclusiveBetween(1, 200);
        RuleFor(c => c.Frequency).MaximumLength(200);
    }
}

internal sealed class StartCarePlanHandler(
    ICareAccess access, ITherapyModalityRepository modalities, IPractitionerQualificationRepository qualifications, ITherapyCarePlanRepository plans,
    IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<StartCarePlanCommand, Guid>
{
    public async Task<Result<Guid>> Handle(StartCarePlanCommand command, CancellationToken cancellationToken)
    {
        var modality = await TherapyAccess.ModalityByCodeAsync(modalities, command.ModalityCode, cancellationToken);
        if (modality.IsFailure)
        {
            return modality.Error;
        }

        var acting = await TherapyAccess.ForModalityAsync(access, modalities, qualifications, modality.Value.Id, cancellationToken);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var relationship = await access.GetActiveRelationshipAsync(acting.Value.Professional, command.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var a = acting.Value;
        var plan = TherapyCarePlan.Start(
            a.TenantId, command.ClientId, a.Profile, a.Modality, a.Qualification, command.Goals, command.PlannedSessions, command.Frequency, command.NextReviewOn,
            clock.GetUtcNow());
        if (plan.IsFailure)
        {
            return plan.Error;
        }

        plans.Add(plan.Value);
        audit.Record(AuditActions.Create, nameof(TherapyCarePlan), plan.Value.Id, command.ClientId, a.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return plan.Value.Id;
    }
}

/// <summary>The practitioner reviews the care plan: continue, adjust, discharge or refer for medical care (RF-36).</summary>
public sealed record ReviewCarePlanCommand(
    Guid CarePlanId, CarePlanReviewOutcome Outcome, string Notes, string? Goals, int? PlannedSessions, string? Frequency, DateOnly? NextReviewOn) : ICommand;

internal sealed class ReviewCarePlanValidator : AbstractValidator<ReviewCarePlanCommand>
{
    public ReviewCarePlanValidator()
    {
        RuleFor(c => c.Outcome).IsInEnum();
        RuleFor(c => c.Notes).NotEmpty().MaximumLength(4000);
        RuleFor(c => c.Goals).MaximumLength(4000);
        RuleFor(c => c.PlannedSessions).InclusiveBetween(1, 200);
        RuleFor(c => c.Frequency).MaximumLength(200);
    }
}

internal sealed class ReviewCarePlanHandler(
    ICareAccess access, ITherapyCarePlanRepository plans, ITherapyModalityRepository modalities, IPractitionerQualificationRepository qualifications,
    IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ReviewCarePlanCommand>
{
    public async Task<Result> Handle(ReviewCarePlanCommand command, CancellationToken cancellationToken)
    {
        var practitioner = await access.GetCurrentProfessionalAsync(Discipline.TherapyPractitioner, requireVerified: true, cancellationToken);
        if (practitioner.IsFailure)
        {
            return practitioner;
        }

        var plan = await plans.GetByIdAsync(command.CarePlanId, cancellationToken);
        if (plan is null || !plan.IsOwnedBy(practitioner.Value.Profile.Id) || plan.TenantId != practitioner.Value.TenantId)
        {
            if (plan is not null)
            {
                await audit.RecordDeniedAsync(nameof(TherapyCarePlan), plan.Id, practitioner.Value.TenantId, cancellationToken);
            }

            return CarePlanErrors.NotFound;
        }

        var relationship = await access.GetActiveRelationshipAsync(practitioner.Value, plan.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship;
        }

        var eligible = await TherapySessionLoader.EnsureEligibleAsync(access, modalities, qualifications, plan.ModalityId, cancellationToken);
        if (eligible.IsFailure)
        {
            return eligible;
        }

        var result = plan.Review(
            practitioner.Value.Profile.Id, command.Outcome, command.Notes, command.Goals, command.PlannedSessions, command.Frequency, command.NextReviewOn,
            clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Review, nameof(TherapyCarePlan), plan.Id, plan.ClientId, plan.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

public sealed record ListClientCarePlansQuery(Guid ClientId) : IQuery<IReadOnlyList<CarePlanResponse>>;

internal sealed class ListClientCarePlansHandler(ICareAccess access, IReadDbContext db, IAuditLog audit)
    : IQueryHandler<ListClientCarePlansQuery, IReadOnlyList<CarePlanResponse>>
{
    public async Task<Result<IReadOnlyList<CarePlanResponse>>> Handle(ListClientCarePlansQuery query, CancellationToken cancellationToken)
    {
        var practitioner = await access.GetCurrentProfessionalAsync(Discipline.TherapyPractitioner, requireVerified: false, cancellationToken);
        if (practitioner.IsFailure)
        {
            return practitioner.Error;
        }

        var relationship = await access.GetActiveRelationshipAsync(practitioner.Value, query.ClientId, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var items = await CarePlanMapping.ListAsync(
            db,
            db.TherapyCarePlans.Where(p => p.ClientId == query.ClientId && p.TenantId == practitioner.Value.TenantId && p.PractitionerProfileId == practitioner.Value.Profile.Id),
            cancellationToken);
        await audit.RecordReadAsync("TherapyCarePlan.List", relationship.Value.Id, query.ClientId, practitioner.Value.TenantId, cancellationToken);
        return Result.Success(items);
    }
}

public sealed record ListMyCarePlansQuery : IQuery<IReadOnlyList<CarePlanResponse>>;

internal sealed class ListMyCarePlansHandler(IUserContext user, IReadDbContext db) : IQueryHandler<ListMyCarePlansQuery, IReadOnlyList<CarePlanResponse>>
{
    public async Task<Result<IReadOnlyList<CarePlanResponse>>> Handle(ListMyCarePlansQuery query, CancellationToken cancellationToken) =>
        Result.Success(await CarePlanMapping.ListAsync(db, db.TherapyCarePlans.Where(p => p.ClientId == user.UserId), cancellationToken));
}

public sealed record TherapySummaryResponse(
    Guid ClientId, IReadOnlyList<string> ActiveModalities, DateTimeOffset? NextSessionAt, int CompletedLast30Days, int ScheduledUpcoming);

/// <summary>
/// What a trainer or dietitian may know about therapies for coordination (§6, <c>TherapySummary</c> grant): which
/// modalities are under way, the next appointment and how many sessions happened recently — never the care record,
/// consent or referrals. Covers the relationship's organization only; audited.
/// </summary>
public sealed record GetSharedTherapySummaryQuery(Guid RelationshipId) : IQuery<TherapySummaryResponse>;

internal sealed class GetSharedTherapySummaryHandler(ICareAccess access, IReadDbContext db, IAuditLog audit, TimeProvider clock)
    : IQueryHandler<GetSharedTherapySummaryQuery, TherapySummaryResponse>
{
    public async Task<Result<TherapySummaryResponse>> Handle(GetSharedTherapySummaryQuery query, CancellationToken cancellationToken)
    {
        var relationship = await access.GetSharedAccessAsync(query.RelationshipId, DataCategory.TherapySummary, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var r = relationship.Value;
        var now = clock.GetUtcNow();
        var since = now.AddDays(-30);
        var sessions = await db.TherapySessions
            .Where(s => s.ClientId == r.ClientId && s.TenantId == r.TenantId && s.Status != TherapySessionStatus.Cancelled)
            .Select(s => new { s.Status, s.StartsAt, s.CompletedAt })
            .ToListAsync(cancellationToken);
        var modalities = await (
                from p in db.TherapyCarePlans
                join m in db.TherapyModalities on p.ModalityId equals m.Id
                where p.ClientId == r.ClientId && p.TenantId == r.TenantId && p.Status == CarePlanStatus.Active
                select m.Name)
            .Distinct()
            .ToListAsync(cancellationToken);

        var upcoming = sessions.Where(s => s.Status == TherapySessionStatus.Scheduled && s.StartsAt >= now).ToList();
        await audit.RecordReadAsync("TherapySummary", r.Id, r.ClientId, r.TenantId, cancellationToken);
        return new TherapySummaryResponse(
            r.ClientId!.Value, modalities.Order().ToList(), upcoming.Count == 0 ? null : upcoming.Min(s => s.StartsAt),
            sessions.Count(s => s.Status == TherapySessionStatus.Completed && s.CompletedAt >= since), upcoming.Count);
    }
}

internal static class CarePlanMapping
{
    public static async Task<IReadOnlyList<CarePlanResponse>> ListAsync(IReadDbContext db, IQueryable<TherapyCarePlan> plans, CancellationToken cancellationToken)
    {
        var rows = await (
                from p in plans
                join t in db.Tenants on p.TenantId equals t.Id
                join m in db.TherapyModalities on p.ModalityId equals m.Id
                join pr in db.ProfessionalProfiles on p.PractitionerProfileId equals pr.Id
                orderby p.CreatedAt descending
                select new { Plan = p, Organization = t.Name, m.Code, Practitioner = pr.DisplayName })
            .Take(200)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new CarePlanResponse(
                r.Plan.Id, r.Plan.TenantId, r.Organization, r.Plan.ClientId, r.Plan.PractitionerProfileId, r.Practitioner, r.Code, r.Plan.Goals, r.Plan.PlannedSessions,
                r.Plan.Frequency, r.Plan.NextReviewOn, r.Plan.Status, r.Plan.Reviews, r.Plan.CreatedAt))
            .ToList();
    }
}
