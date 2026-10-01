using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.Therapies;

public enum CarePlanStatus
{
    Active = 1,
    Closed = 2,
}

/// <summary>What the practitioner concluded at a review of the care plan (RF-36).</summary>
public enum CarePlanReviewOutcome
{
    Continue = 1,
    Adjust = 2,
    Discharge = 3,

    /// <summary>Warning signs: the client is advised to seek medical care and the plan is closed.</summary>
    ReferForMedicalCare = 4,
}

public sealed record CarePlanReview(CarePlanReviewOutcome Outcome, string Notes, DateOnly? NextReviewOn, Guid ReviewedByProfileId, DateTimeOffset ReviewedAt);

/// <summary>
/// The practitioner's plan for a client in one modality: goals, expected sessions and when to review it (RF-36). Reviews
/// are appended, never edited, so the plan's history shows how it evolved.
/// </summary>
public sealed class TherapyCarePlan : AggregateRoot<Guid>
{
    private TherapyCarePlan(Guid id) : base(id) { }

    private TherapyCarePlan() { }

    public Guid TenantId { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid PractitionerProfileId { get; private set; }

    public Guid PractitionerUserId { get; private set; }

    public Guid ModalityId { get; private set; }

    public string Goals { get; private set; } = null!;

    public int? PlannedSessions { get; private set; }

    public string? Frequency { get; private set; }

    public DateOnly? NextReviewOn { get; private set; }

    public CarePlanStatus Status { get; private set; }

    public List<CarePlanReview> Reviews { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<TherapyCarePlan> Start(
        Guid tenantId, Guid clientId, ProfessionalProfile practitioner, TherapyModality modality, PractitionerQualification? qualification, string goals,
        int? plannedSessions, string? frequency, DateOnly? nextReviewOn, DateTimeOffset now)
    {
        var eligible = TherapyRules.EnsureCanProvideCare(practitioner, modality, qualification);
        if (eligible.IsFailure)
        {
            return eligible.Error;
        }

        return new TherapyCarePlan(Guid.CreateVersion7(now))
        {
            TenantId = tenantId,
            ClientId = clientId,
            PractitionerProfileId = practitioner.Id,
            PractitionerUserId = practitioner.UserId,
            ModalityId = modality.Id,
            Goals = goals.Trim(),
            PlannedSessions = plannedSessions,
            Frequency = string.IsNullOrWhiteSpace(frequency) ? null : frequency.Trim(),
            NextReviewOn = nextReviewOn,
            Status = CarePlanStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public bool IsOwnedBy(Guid practitionerProfileId) => PractitionerProfileId == practitionerProfileId;

    /// <summary>Records a review. Adjusting may change goals and frequency; discharging or referring closes the plan.</summary>
    public Result Review(
        Guid practitionerProfileId, CarePlanReviewOutcome outcome, string notes, string? goals, int? plannedSessions, string? frequency, DateOnly? nextReviewOn,
        DateTimeOffset now)
    {
        if (!IsOwnedBy(practitionerProfileId))
        {
            return CarePlanErrors.NotFound;
        }

        if (Status == CarePlanStatus.Closed)
        {
            return CarePlanErrors.Closed;
        }

        if (string.IsNullOrWhiteSpace(notes))
        {
            return CarePlanErrors.NotesRequired;
        }

        if (outcome == CarePlanReviewOutcome.Adjust)
        {
            Goals = string.IsNullOrWhiteSpace(goals) ? Goals : goals.Trim();
            PlannedSessions = plannedSessions ?? PlannedSessions;
            Frequency = string.IsNullOrWhiteSpace(frequency) ? Frequency : frequency.Trim();
        }

        var closes = outcome is CarePlanReviewOutcome.Discharge or CarePlanReviewOutcome.ReferForMedicalCare;
        NextReviewOn = closes ? null : nextReviewOn;
        Status = closes ? CarePlanStatus.Closed : CarePlanStatus.Active;
        Reviews.Add(new CarePlanReview(outcome, notes.Trim(), NextReviewOn, practitionerProfileId, now));
        UpdatedAt = now;
        Raise(new TherapyCarePlanReviewed(Id, TenantId, ClientId, outcome, now));
        return Result.Success();
    }
}

public sealed record TherapyCarePlanReviewed(Guid CarePlanId, Guid TenantId, Guid ClientId, CarePlanReviewOutcome Outcome, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

public interface ITherapyCarePlanRepository : IRepository<TherapyCarePlan, Guid>;

public static class CarePlanErrors
{
    public static readonly Error NotFound = Error.NotFound("CarePlan.NotFound", "The care plan was not found.");
    public static readonly Error Closed = Error.Conflict("CarePlan.Closed", "The care plan is closed.");
    public static readonly Error NotesRequired = Error.Validation("CarePlan.NotesRequired", "A review needs notes.");
}
