using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.Training.Plans;

public enum PlanStatus
{
    Draft = 1,
    Published = 2,
    Ended = 3,
}

public sealed record PlanDetails(
    string Name,
    string Goal,
    TrainingLevel Level,
    DateOnly StartDate,
    DateOnly? EndDate,
    int SuggestedSessionsPerWeek,
    string? Notes);

/// <summary>
/// A time-bound prescription for one client. The trainer edits <see cref="Draft"/>; publishing freezes it into
/// an immutable <see cref="TrainingPlanVersion"/>, so sessions already started keep pointing to what was performed.
/// </summary>
public sealed class TrainingPlan : AggregateRoot<Guid>
{
    private TrainingPlan(
        Guid id, Guid tenantId, Guid clientId, ProfessionalProfile trainer, PlanDetails details, PlanContent draft, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        ClientId = clientId;
        TrainerProfileId = trainer.Id;
        TrainerUserId = trainer.UserId;
        Details = details;
        Draft = draft;
        Status = PlanStatus.Draft;
        CreatedAt = now;
        UpdatedAt = now;
        HasUnpublishedChanges = true;
    }

    private TrainingPlan() { }

    /// <summary>The organization the plan was prescribed in. Versions and executions inherit it.</summary>
    public Guid TenantId { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid TrainerProfileId { get; private set; }

    public Guid TrainerUserId { get; private set; }

    public PlanDetails Details { get; private set; } = null!;

    public PlanContent Draft { get; private set; } = PlanContent.Empty;

    public PlanStatus Status { get; private set; }

    public int CurrentVersionNumber { get; private set; }

    public Guid? CurrentVersionId { get; private set; }

    public bool HasUnpublishedChanges { get; private set; }

    public Guid? CopiedFromPlanId { get; private set; }

    /// <summary>
    /// Set when something the prescription depends on changed (e.g. the client reported a new injury, RF-23). Cleared when
    /// the trainer acknowledges it or publishes a new version.
    /// </summary>
    public DateTimeOffset? ReviewRequestedAt { get; private set; }

    public string? ReviewReason { get; private set; }

    public bool NeedsReview => ReviewRequestedAt is not null;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<TrainingPlan> Create(Guid tenantId, ProfessionalProfile trainer, Guid clientId, PlanDetails details, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A training plan always belongs to a tenant.", nameof(tenantId));
        }

        var check = EnsureTrainer(trainer);
        if (check.IsFailure)
        {
            return check.Error;
        }

        var validDetails = ValidateDetails(details);
        if (validDetails.IsFailure)
        {
            return validDetails.Error;
        }

        return new TrainingPlan(Guid.CreateVersion7(now), tenantId, clientId, trainer, details, PlanContent.Empty, now);
    }

    public bool IsOwnedBy(Guid trainerProfileId) => TrainerProfileId == trainerProfileId;

    public Result UpdateDetails(PlanDetails details, DateTimeOffset now)
    {
        if (Status == PlanStatus.Ended)
        {
            return PlanErrors.Ended;
        }

        var valid = ValidateDetails(details);
        if (valid.IsFailure)
        {
            return valid;
        }

        Details = details;
        Touch(now);
        return Result.Success();
    }

    public Result UpdateDraft(PlanContent content, DateTimeOffset now)
    {
        if (Status == PlanStatus.Ended)
        {
            return PlanErrors.Ended;
        }

        var valid = content.ValidateStructure();
        if (valid.IsFailure)
        {
            return valid;
        }

        Draft = content;
        Touch(now);
        return Result.Success();
    }

    public Result<TrainingPlanVersion> Publish(ProfessionalProfile trainer, string changeDescription, DateTimeOffset now)
    {
        if (!IsOwnedBy(trainer.Id))
        {
            return PlanErrors.NotOwner;
        }

        if (!trainer.IsVerified)
        {
            return ProfessionalErrors.NotVerified;
        }

        if (Status == PlanStatus.Ended)
        {
            return PlanErrors.Ended;
        }

        if (!HasUnpublishedChanges)
        {
            return PlanErrors.NothingToPublish;
        }

        var valid = Draft.ValidateForPublishing();
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var version = TrainingPlanVersion.Create(this, CurrentVersionNumber + 1, changeDescription, now);
        CurrentVersionNumber = version.VersionNumber;
        CurrentVersionId = version.Id;
        Status = PlanStatus.Published;
        HasUnpublishedChanges = false;
        ReviewRequestedAt = null;
        ReviewReason = null;
        UpdatedAt = now;
        Raise(new TrainingPlanPublished(Id, TenantId, version.Id, version.VersionNumber, ClientId, now));
        return version;
    }

    public Result End(DateTimeOffset now)
    {
        if (Status == PlanStatus.Ended)
        {
            return PlanErrors.Ended;
        }

        Status = PlanStatus.Ended;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Flags the plan for the trainer's review. Ended plans are not flagged, and a pending flag is not raised twice.</summary>
    public void RequestReview(string reason, DateTimeOffset now)
    {
        if (Status == PlanStatus.Ended || NeedsReview)
        {
            return;
        }

        ReviewRequestedAt = now;
        ReviewReason = reason;
        Raise(new TrainingPlanReviewRequested(Id, TenantId, ClientId, TrainerUserId, now));
    }

    /// <summary>The trainer confirms the prescription is still appropriate without changing it.</summary>
    public Result AcknowledgeReview(DateTimeOffset now)
    {
        if (!NeedsReview)
        {
            return PlanErrors.NoReviewPending;
        }

        ReviewRequestedAt = null;
        ReviewReason = null;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Starts a new draft from this plan's current draft, in the same tenant. The source plan and its history are untouched.
    /// </summary>
    public Result<TrainingPlan> CopyAsDraft(ProfessionalProfile trainer, Guid clientId, string? newName, DateTimeOffset now)
    {
        if (!IsOwnedBy(trainer.Id))
        {
            return PlanErrors.NotOwner;
        }

        var copy = Create(TenantId, trainer, clientId, Details with { Name = newName ?? Details.Name }, now);
        if (copy.IsFailure)
        {
            return copy;
        }

        copy.Value.Draft = Draft;
        copy.Value.CopiedFromPlanId = Id;
        return copy;
    }

    private void Touch(DateTimeOffset now)
    {
        HasUnpublishedChanges = true;
        UpdatedAt = now;
    }

    private static Result EnsureTrainer(ProfessionalProfile trainer)
    {
        if (trainer.Discipline != Discipline.PersonalTrainer)
        {
            return ProfessionalErrors.MissingProfile(Discipline.PersonalTrainer);
        }

        return trainer.IsVerified ? Result.Success() : ProfessionalErrors.NotVerified;
    }

    private static Result ValidateDetails(PlanDetails details)
    {
        if (details.EndDate < details.StartDate)
        {
            return PlanErrors.InvalidDates;
        }

        return details.SuggestedSessionsPerWeek is < 1 or > 14 ? PlanErrors.InvalidFrequency : Result.Success();
    }
}

public sealed record TrainingPlanReviewRequested(Guid PlanId, Guid TenantId, Guid ClientId, Guid TrainerUserId, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

public sealed record TrainingPlanPublished(Guid PlanId, Guid TenantId, Guid VersionId, int VersionNumber, Guid ClientId, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);
