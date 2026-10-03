using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Plans;

namespace MyHealth.Application.Assessments;

public sealed record HealthHistoryInput(
    string? Goals, string? ExerciseHistory, string? Limitations, string? ReportedInjuries, string? Availability, string? Equipment)
{
    internal HealthHistoryContent ToContent() => new(Goals, ExerciseHistory, Limitations, ReportedInjuries, Availability, Equipment);
}

public sealed record HealthHistoryRevisionResponse(int Number, HealthHistoryContent Content, bool IsSignificantChange, DateTimeOffset SubmittedAt, DateTimeOffset AcknowledgedAt);

public sealed record HealthHistoryRevisionItem(int Number, bool IsSignificantChange, DateTimeOffset SubmittedAt);

public sealed record HealthHistoryReviewItem(
    int RevisionNumber, Guid TenantId, string OrganizationName, string ReviewerName, Discipline Discipline, string? Notes, DateTimeOffset ReviewedAt);

public sealed record HealthHistoryResponse(
    HealthHistoryRevisionResponse Current, IReadOnlyList<HealthHistoryRevisionItem> Revisions, IReadOnlyList<HealthHistoryReviewItem> Reviews);

/// <summary>
/// The client submits their health history (RF-23). Each change becomes a new revision; submitting identical content
/// again returns the current revision. Changes to limitations or injuries flag the client's training plans for review.
/// </summary>
public sealed record SubmitHealthHistoryCommand(HealthHistoryInput History, bool Acknowledged) : ICommand<HealthHistoryRevisionItem>;

internal sealed class SubmitHealthHistoryValidator : AbstractValidator<SubmitHealthHistoryCommand>
{
    public SubmitHealthHistoryValidator()
    {
        RuleFor(c => c.History).NotNull();
        RuleFor(c => c.History.Goals).MaximumLength(2000);
        RuleFor(c => c.History.ExerciseHistory).MaximumLength(4000);
        RuleFor(c => c.History.Limitations).MaximumLength(4000);
        RuleFor(c => c.History.ReportedInjuries).MaximumLength(4000);
        RuleFor(c => c.History.Availability).MaximumLength(1000);
        RuleFor(c => c.History.Equipment).MaximumLength(1000);
    }
}

internal sealed class SubmitHealthHistoryHandler(
    IUserContext user, IHealthHistoryRepository histories, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<SubmitHealthHistoryCommand, HealthHistoryRevisionItem>
{
    public async Task<Result<HealthHistoryRevisionItem>> Handle(SubmitHealthHistoryCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var history = await histories.GetByClientAsync(user.UserId, cancellationToken);
        if (history is null)
        {
            history = HealthHistory.Start(user.UserId, now);
            histories.Add(history);
        }

        var previousRevision = history.CurrentRevisionNumber;
        var revision = history.Submit(command.History.ToContent(), command.Acknowledged, now);
        if (revision.IsFailure)
        {
            return revision.Error;
        }

        if (revision.Value.Number != previousRevision)
        {
            audit.Record(AuditActions.Update, nameof(HealthHistory), history.Id, user.UserId, tenantId: null);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new HealthHistoryRevisionItem(revision.Value.Number, revision.Value.IsSignificantChange, revision.Value.SubmittedAt);
    }
}

/// <summary>The client's own health history with every revision and who reviewed it, across organizations (MT-10).</summary>
public sealed record GetMyHealthHistoryQuery : IQuery<HealthHistoryResponse>;

internal sealed class GetMyHealthHistoryHandler(IUserContext user, IReadDbContext db) : IQueryHandler<GetMyHealthHistoryQuery, HealthHistoryResponse>
{
    public async Task<Result<HealthHistoryResponse>> Handle(GetMyHealthHistoryQuery query, CancellationToken cancellationToken)
    {
        var history = await HealthHistoryMapping.LoadAsync(db, user.UserId, cancellationToken);
        return history is null
            ? HealthHistoryErrors.NotFound
            : await HealthHistoryMapping.ToResponseAsync(db, history, reviewsInTenant: null, cancellationToken);
    }
}

public sealed record GetMyHealthHistoryRevisionQuery(int RevisionNumber) : IQuery<HealthHistoryRevisionResponse>;

internal sealed class GetMyHealthHistoryRevisionHandler(IUserContext user, IReadDbContext db)
    : IQueryHandler<GetMyHealthHistoryRevisionQuery, HealthHistoryRevisionResponse>
{
    public async Task<Result<HealthHistoryRevisionResponse>> Handle(GetMyHealthHistoryRevisionQuery query, CancellationToken cancellationToken)
    {
        var history = await HealthHistoryMapping.LoadAsync(db, user.UserId, cancellationToken);
        var revision = history?.Revisions.FirstOrDefault(r => r.Number == query.RevisionNumber);
        return revision is null ? HealthHistoryErrors.RevisionNotFound : HealthHistoryMapping.ToResponse(revision);
    }
}

/// <summary>
/// A professional reads the client's health history through an active relationship with a <c>HealthHistory</c> grant.
/// Only reviews made in the relationship's organization are listed. Every read is audited.
/// </summary>
public sealed record GetSharedHealthHistoryQuery(Guid RelationshipId) : IQuery<HealthHistoryResponse>;

internal sealed class GetSharedHealthHistoryHandler(ICareAccess access, IReadDbContext db, IAuditLog audit)
    : IQueryHandler<GetSharedHealthHistoryQuery, HealthHistoryResponse>
{
    public async Task<Result<HealthHistoryResponse>> Handle(GetSharedHealthHistoryQuery query, CancellationToken cancellationToken)
    {
        var relationship = await access.GetSharedAccessAsync(query.RelationshipId, DataCategory.HealthHistory, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var history = await HealthHistoryMapping.LoadAsync(db, relationship.Value.ClientId!.Value, cancellationToken);
        if (history is null)
        {
            return HealthHistoryErrors.NotFound;
        }

        await audit.RecordReadAsync(nameof(HealthHistory), history.Id, history.ClientId, relationship.Value.TenantId, cancellationToken);
        return await HealthHistoryMapping.ToResponseAsync(db, history, relationship.Value.TenantId, cancellationToken);
    }
}

/// <summary>The professional confirms they read a revision (RF-23 "professional-reviewable").</summary>
public sealed record ReviewHealthHistoryCommand(Guid RelationshipId, int RevisionNumber, string? Notes) : ICommand;

internal sealed class ReviewHealthHistoryValidator : AbstractValidator<ReviewHealthHistoryCommand>
{
    public ReviewHealthHistoryValidator()
    {
        RuleFor(c => c.RevisionNumber).GreaterThan(0);
        RuleFor(c => c.Notes).MaximumLength(2000);
    }
}

internal sealed class ReviewHealthHistoryHandler(
    ICareAccess access, IHealthHistoryRepository histories, IProfessionalProfileRepository professionals, IAuditLog audit,
    IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ReviewHealthHistoryCommand>
{
    public async Task<Result> Handle(ReviewHealthHistoryCommand command, CancellationToken cancellationToken)
    {
        var relationship = await access.GetSharedAccessAsync(command.RelationshipId, DataCategory.HealthHistory, cancellationToken);
        if (relationship.IsFailure)
        {
            return relationship.Error;
        }

        var history = await histories.GetByClientAsync(relationship.Value.ClientId!.Value, cancellationToken);
        if (history is null)
        {
            return HealthHistoryErrors.NotFound;
        }

        var reviewer = await professionals.GetByIdAsync(relationship.Value.ProfessionalProfileId, cancellationToken);
        if (reviewer is null)
        {
            return ProfessionalErrors.NotFound;
        }

        var result = history.Review(relationship.Value.TenantId, reviewer, command.RevisionNumber, command.Notes, clock.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(AuditActions.Review, nameof(HealthHistory), history.Id, history.ClientId, relationship.Value.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

/// <summary>
/// A significant change (limitations or injuries) flags the client's open training plans for review — but only plans
/// whose trainer the client currently shares the health history with (active relationship in the plan's tenant plus a
/// <c>HealthHistory</c> grant). Even the existence of a change is health information, so nobody else learns of it.
/// </summary>
internal sealed class HealthHistoryRevisedHandler(ITrainingPlanRepository plans, ICareRelationshipRepository relationships)
    : IDomainEventHandler<HealthHistoryRevised>
{
    public async Task Handle(HealthHistoryRevised domainEvent, CancellationToken cancellationToken)
    {
        if (!domainEvent.IsSignificantChange)
        {
            return;
        }

        var sharing = (await relationships.ListActiveForClientAsync(domainEvent.ClientId, cancellationToken))
            .Where(r => r.HasActiveGrant(DataCategory.HealthHistory))
            .Select(r => (r.TenantId, r.ProfessionalProfileId))
            .ToHashSet();

        foreach (var plan in await plans.ListOpenForClientAsync(domainEvent.ClientId, cancellationToken))
        {
            if (sharing.Contains((plan.TenantId, plan.TrainerProfileId)))
            {
                plan.RequestReview($"Health history revision {domainEvent.RevisionNumber} has changes that may affect this prescription.", domainEvent.OccurredAt);
            }
        }
    }
}

internal static class HealthHistoryMapping
{
    public static Task<HealthHistory?> LoadAsync(IReadDbContext db, Guid clientId, CancellationToken cancellationToken) =>
        db.HealthHistories.Include(h => h.Revisions).Include(h => h.Reviews).FirstOrDefaultAsync(h => h.ClientId == clientId, cancellationToken);

    public static HealthHistoryRevisionResponse ToResponse(HealthHistoryRevision r) =>
        new(r.Number, r.Content, r.IsSignificantChange, r.SubmittedAt, r.AcknowledgedAt);

    public static async Task<HealthHistoryResponse> ToResponseAsync(
        IReadDbContext db, HealthHistory history, Guid? reviewsInTenant, CancellationToken cancellationToken)
    {
        var reviews = history.Reviews.Where(r => reviewsInTenant is null || r.TenantId == reviewsInTenant).ToList();
        var reviewerIds = reviews.Select(r => r.ReviewerProfileId).Distinct().ToList();
        var tenantIds = reviews.Select(r => r.TenantId).Distinct().ToList();
        var reviewers = await db.ProfessionalProfiles.Where(p => reviewerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, cancellationToken);
        var tenants = await db.Tenants.Where(t => tenantIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        return new HealthHistoryResponse(
            ToResponse(history.Current!),
            history.Revisions.OrderByDescending(r => r.Number).Select(r => new HealthHistoryRevisionItem(r.Number, r.IsSignificantChange, r.SubmittedAt)).ToList(),
            reviews
                .OrderByDescending(r => r.ReviewedAt)
                .Select(r => new HealthHistoryReviewItem(
                    r.RevisionNumber, r.TenantId, tenants.GetValueOrDefault(r.TenantId, string.Empty), reviewers.GetValueOrDefault(r.ReviewerProfileId, string.Empty),
                    r.Discipline, r.Notes, r.ReviewedAt))
                .ToList());
    }
}
