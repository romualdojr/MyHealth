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
    private TrainingPlan(Guid id, Guid clientId, ProfessionalProfile trainer, PlanDetails details, PlanContent draft, DateTimeOffset now)
        : base(id)
    {
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

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<TrainingPlan> Create(ProfessionalProfile trainer, Guid clientId, PlanDetails details, DateTimeOffset now)
    {
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

        return new TrainingPlan(Guid.CreateVersion7(now), clientId, trainer, details, PlanContent.Empty, now);
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
        UpdatedAt = now;
        Raise(new TrainingPlanPublished(Id, version.Id, version.VersionNumber, ClientId, now));
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

    /// <summary>Starts a new draft from this plan's current draft. The source plan and its history are untouched.</summary>
    public Result<TrainingPlan> CopyAsDraft(ProfessionalProfile trainer, Guid clientId, string? newName, DateTimeOffset now)
    {
        if (!IsOwnedBy(trainer.Id))
        {
            return PlanErrors.NotOwner;
        }

        var copy = Create(trainer, clientId, Details with { Name = newName ?? Details.Name }, now);
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

public sealed record TrainingPlanPublished(Guid PlanId, Guid VersionId, int VersionNumber, Guid ClientId, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);
