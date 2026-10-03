using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.Therapies;

/// <summary>
/// The care record of one session (RF-34). Which parts are mandatory depends on the modality; "none" must be written
/// explicitly (e.g. for adverse events), because an empty field cannot be told apart from a forgotten one.
/// </summary>
public sealed record TherapyRecord(
    string? ReportedConcern,
    string? ProfessionalAssessment,
    string? ContraindicationsAndPrecautions,
    string? Technique,
    string? PointsOrRegions,
    int? DurationMinutes,
    string? AdverseEvents,
    string? PostSessionGuidance)
{
    public bool Has(TherapyRecordField field) => field switch
    {
        TherapyRecordField.ReportedConcern => Filled(ReportedConcern),
        TherapyRecordField.ProfessionalAssessment => Filled(ProfessionalAssessment),
        TherapyRecordField.ContraindicationsAndPrecautions => Filled(ContraindicationsAndPrecautions),
        TherapyRecordField.Technique => Filled(Technique),
        TherapyRecordField.PointsOrRegions => Filled(PointsOrRegions),
        TherapyRecordField.DurationMinutes => DurationMinutes is > 0,
        TherapyRecordField.AdverseEvents => Filled(AdverseEvents),
        TherapyRecordField.PostSessionGuidance => Filled(PostSessionGuidance),
        _ => false,
    };

    public TherapyRecord Normalized() =>
        new(Clean(ReportedConcern), Clean(ProfessionalAssessment), Clean(ContraindicationsAndPrecautions), Clean(Technique), Clean(PointsOrRegions),
            DurationMinutes, Clean(AdverseEvents), Clean(PostSessionGuidance));

    private static bool Filled(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record TherapyRecordCorrection(TherapyRecord Previous, string Reason, Guid CorrectedByProfileId, DateTimeOffset CorrectedAt);

/// <summary>How the client says they feel after the session (journey 6), on a 1–5 scale. Self-reported, never a diagnosis.</summary>
public sealed record ClientProgressReport(int Wellbeing, string? Comments, DateTimeOffset ReportedAt);

public enum ReferralUrgency
{
    Routine = 1,
    Urgent = 2,
}

/// <summary>The practitioner advises the client to seek medical care because of warning signs (RF-36). Advice, not a diagnosis.</summary>
public sealed record MedicalReferral(string Reason, ReferralUrgency Urgency, Guid RecommendedByProfileId, DateTimeOffset RecommendedAt);

public enum TherapySessionStatus
{
    Scheduled = 1,
    Completed = 2,
    Cancelled = 3,
}

/// <summary>
/// One appointment and its care record (RF-34, RF-36). The practitioner documents it while it is scheduled; it can only
/// be completed with the modality's mandatory fields filled and, where the modality requires it, a consent that is in
/// force for the current consent version (RF-35, acceptance test 8). After completion, the record changes only through
/// corrections that keep what was replaced.
/// </summary>
public sealed class TherapySession : AggregateRoot<Guid>
{
    /// <summary>How early a session may be completed, for clocks slightly ahead of the scheduled start.</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromMinutes(15);

    public const string RelationshipEndedReason = "The care relationship ended.";

    private TherapySession(Guid id) : base(id) { }

    private TherapySession() { }

    public Guid TenantId { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid PractitionerProfileId { get; private set; }

    public Guid PractitionerUserId { get; private set; }

    public Guid ServiceId { get; private set; }

    public Guid ModalityId { get; private set; }

    public string ServiceName { get; private set; } = null!;

    public ServiceDelivery Delivery { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    public TherapySessionStatus Status { get; private set; }

    public DateTimeOffset ScheduledAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public Guid? CancelledBy { get; private set; }

    public string? CancellationReason { get; private set; }

    public TherapyRecord? Record { get; private set; }

    public DateTimeOffset? RecordUpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>The consent in force when the session was completed.</summary>
    public Guid? ConsentId { get; private set; }

    public List<TherapyRecordCorrection> Corrections { get; private set; } = [];

    public ClientProgressReport? ClientReport { get; private set; }

    /// <summary>Every medical-care referral made, oldest first; a later one never replaces an earlier one.</summary>
    public List<MedicalReferral> Referrals { get; private set; } = [];

    public static Result<TherapySession> Schedule(
        Guid clientId, ProfessionalProfile practitioner, TherapyModality modality, PractitionerQualification? qualification, TherapyService service,
        DateTimeOffset startsAt, DateTimeOffset now)
    {
        if (service.PractitionerProfileId != practitioner.Id || service.ModalityId != modality.Id)
        {
            return TherapyServiceErrors.NotFound;
        }

        if (!service.IsActive)
        {
            return TherapyServiceErrors.Inactive;
        }

        var eligible = TherapyRules.EnsureCanProvideCare(practitioner, modality, qualification);
        if (eligible.IsFailure)
        {
            return eligible.Error;
        }

        var valid = ValidateStart(startsAt, now);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var session = new TherapySession(Guid.CreateVersion7(now))
        {
            TenantId = service.TenantId,
            ClientId = clientId,
            PractitionerProfileId = practitioner.Id,
            PractitionerUserId = practitioner.UserId,
            ServiceId = service.Id,
            ModalityId = modality.Id,
            ServiceName = service.Name,
            Delivery = service.Delivery,
            StartsAt = startsAt,
            EndsAt = startsAt.AddMinutes(service.DurationMinutes),
            Status = TherapySessionStatus.Scheduled,
            ScheduledAt = now,
        };
        session.Raise(new TherapySessionScheduled(session.Id, session.TenantId, clientId, startsAt, now));
        return session;
    }

    public bool IsLedBy(Guid practitionerProfileId) => PractitionerProfileId == practitionerProfileId;

    public Result Reschedule(Guid practitionerProfileId, DateTimeOffset startsAt, DateTimeOffset now)
    {
        var check = EnsureScheduledBy(practitionerProfileId);
        if (check.IsFailure)
        {
            return check;
        }

        var valid = ValidateStart(startsAt, now);
        if (valid.IsFailure)
        {
            return valid;
        }

        var duration = EndsAt - StartsAt;
        StartsAt = startsAt;
        EndsAt = startsAt + duration;
        Raise(new TherapySessionScheduled(Id, TenantId, ClientId, startsAt, now));
        return Result.Success();
    }

    /// <summary>The client or the practitioner cancels a scheduled session. A completed session is never cancelled.</summary>
    public Result Cancel(Guid actorUserId, string? reason, DateTimeOffset now)
    {
        if (actorUserId != ClientId && actorUserId != PractitionerUserId)
        {
            return TherapySessionErrors.NotFound;
        }

        if (Status == TherapySessionStatus.Cancelled)
        {
            return Result.Success();
        }

        if (Status != TherapySessionStatus.Scheduled)
        {
            return TherapySessionErrors.NotScheduled;
        }

        Status = TherapySessionStatus.Cancelled;
        CancelledAt = now;
        CancelledBy = actorUserId;
        CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        Raise(new TherapySessionCancelled(Id, TenantId, ClientId, PractitionerUserId, CancelledByClient: actorUserId == ClientId, now));
        return Result.Success();
    }

    /// <summary>
    /// The care relationship ended (by either side, or the practitioner left the organization): a session that can no longer
    /// take place is cancelled, the client is told, and the practitioner's calendar is freed.
    /// </summary>
    public void CancelBecauseRelationshipEnded(DateTimeOffset now)
    {
        if (Status != TherapySessionStatus.Scheduled)
        {
            return;
        }

        Status = TherapySessionStatus.Cancelled;
        CancelledAt = now;
        CancellationReason = RelationshipEndedReason;
        Raise(new TherapySessionCancelled(Id, TenantId, ClientId, PractitionerUserId, CancelledByClient: false, now));
    }

    /// <summary>Saves the care record while the session is scheduled; it can be filled in over several saves.</summary>
    public Result Document(Guid practitionerProfileId, TherapyRecord record, DateTimeOffset now)
    {
        var check = EnsureScheduledBy(practitionerProfileId);
        if (check.IsFailure)
        {
            return check;
        }

        var valid = ValidateRecord(record);
        if (valid.IsFailure)
        {
            return valid;
        }

        Record = record.Normalized();
        RecordUpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Completes the session. Fails, naming what is missing, without the mandatory documentation or a valid consent;
    /// eligibility (verified profile, enabled modality, verified qualification) is checked again at this moment.
    /// </summary>
    public Result Complete(
        ProfessionalProfile practitioner, TherapyModality modality, PractitionerQualification? qualification, TherapyConsent? consent,
        ConsentTemplate? currentTemplate, DateTimeOffset now)
    {
        if (Status == TherapySessionStatus.Completed)
        {
            return Result.Success();
        }

        var check = EnsureScheduledBy(practitioner.Id);
        if (check.IsFailure)
        {
            return check;
        }

        var eligible = TherapyRules.EnsureCanProvideCare(practitioner, modality, qualification);
        if (eligible.IsFailure)
        {
            return eligible;
        }

        if (now + StartTolerance < StartsAt)
        {
            return TherapySessionErrors.NotStarted;
        }

        var missing = modality.MissingFields(Record);
        if (missing.Count > 0)
        {
            return TherapySessionErrors.MissingDocumentation(missing);
        }

        if (modality.RequiresConsent)
        {
            if (currentTemplate is null)
            {
                return ConsentErrors.NoTemplate;
            }

            if (consent is null || consent.ClientId != ClientId || consent.TenantId != TenantId
                || !consent.IsValidFor(PractitionerProfileId, ModalityId, currentTemplate.Version))
            {
                return TherapySessionErrors.ConsentRequired;
            }

            ConsentId = consent.Id;
        }

        Status = TherapySessionStatus.Completed;
        CompletedAt = now;
        Raise(new TherapySessionCompleted(Id, TenantId, ClientId, now));
        return Result.Success();
    }

    /// <summary>Corrects a completed record; the replaced record, the reason and the practitioner are kept (§5 History).</summary>
    public Result CorrectRecord(Guid practitionerProfileId, TherapyModality modality, TherapyRecord record, string reason, DateTimeOffset now)
    {
        if (!IsLedBy(practitionerProfileId))
        {
            return TherapySessionErrors.NotFound;
        }

        if (Status != TherapySessionStatus.Completed)
        {
            return TherapySessionErrors.NotCompleted;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return TherapySessionErrors.CorrectionReasonRequired;
        }

        var valid = ValidateRecord(record);
        if (valid.IsFailure)
        {
            return valid;
        }

        var normalized = record.Normalized();
        var missing = modality.MissingFields(normalized);
        if (missing.Count > 0)
        {
            return TherapySessionErrors.MissingDocumentation(missing);
        }

        Corrections.Add(new TherapyRecordCorrection(Record!, reason.Trim(), practitionerProfileId, now));
        Record = normalized;
        RecordUpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Advises medical care because of warning signs (RF-36); the client is notified at once.</summary>
    public Result RecommendMedicalCare(Guid practitionerProfileId, string reason, ReferralUrgency urgency, DateTimeOffset now)
    {
        if (!IsLedBy(practitionerProfileId))
        {
            return TherapySessionErrors.NotFound;
        }

        if (Status == TherapySessionStatus.Cancelled)
        {
            return TherapySessionErrors.NotScheduled;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return TherapySessionErrors.ReferralReasonRequired;
        }

        Referrals.Add(new MedicalReferral(reason.Trim(), urgency, practitionerProfileId, now));
        Raise(new MedicalReferralRecommended(Id, TenantId, ClientId, urgency, now));
        return Result.Success();
    }

    /// <summary>The client reports how they feel after a completed session; a later report replaces the earlier one.</summary>
    public Result ReportProgress(Guid clientId, int wellbeing, string? comments, DateTimeOffset now)
    {
        if (clientId != ClientId)
        {
            return TherapySessionErrors.NotFound;
        }

        if (Status != TherapySessionStatus.Completed)
        {
            return TherapySessionErrors.NotCompleted;
        }

        if (wellbeing is < 1 or > 5)
        {
            return TherapySessionErrors.InvalidWellbeing;
        }

        ClientReport = new ClientProgressReport(wellbeing, string.IsNullOrWhiteSpace(comments) ? null : comments.Trim(), now);
        Raise(new TherapyProgressReported(Id, TenantId, ClientId, PractitionerUserId, now));
        return Result.Success();
    }

    private Result EnsureScheduledBy(Guid practitionerProfileId)
    {
        if (!IsLedBy(practitionerProfileId))
        {
            return TherapySessionErrors.NotFound;
        }

        return Status == TherapySessionStatus.Scheduled ? Result.Success() : TherapySessionErrors.NotScheduled;
    }

    private static Result ValidateStart(DateTimeOffset startsAt, DateTimeOffset now) =>
        startsAt > now.AddYears(1) ? TherapySessionErrors.TooFarAhead : Result.Success();

    private static Result ValidateRecord(TherapyRecord record) =>
        record.DurationMinutes is < 1 or > 480 ? TherapySessionErrors.InvalidDuration : Result.Success();
}

public sealed record TherapySessionScheduled(Guid SessionId, Guid TenantId, Guid ClientId, DateTimeOffset StartsAt, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

public sealed record TherapySessionCancelled(
    Guid SessionId, Guid TenantId, Guid ClientId, Guid PractitionerUserId, bool CancelledByClient, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed record TherapySessionCompleted(Guid SessionId, Guid TenantId, Guid ClientId, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed record MedicalReferralRecommended(Guid SessionId, Guid TenantId, Guid ClientId, ReferralUrgency Urgency, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

public sealed record TherapyProgressReported(Guid SessionId, Guid TenantId, Guid ClientId, Guid PractitionerUserId, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

public interface ITherapySessionRepository : IRepository<TherapySession, Guid>
{
    /// <summary>Whether the practitioner already has a scheduled session overlapping the interval, in any tenant.</summary>
    Task<bool> HasOverlapAsync(Guid practitionerProfileId, DateTimeOffset startsAt, DateTimeOffset endsAt, Guid? excludeSessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TherapySession>> ListScheduledAsync(Guid tenantId, Guid practitionerProfileId, Guid clientId, CancellationToken cancellationToken = default);
}

public static class TherapySessionErrors
{
    public static readonly Error NotFound = Error.NotFound("TherapySession.NotFound", "The therapy session was not found.");
    public static readonly Error NotScheduled = Error.Conflict("TherapySession.NotScheduled", "The session is no longer scheduled.");
    public static readonly Error NotCompleted = Error.Conflict("TherapySession.NotCompleted", "The session has not been completed.");
    public static readonly Error NotStarted = Error.Conflict("TherapySession.NotStarted", "The session cannot be completed before it starts.");
    public static readonly Error Overlap = Error.Conflict("TherapySession.Overlap", "You already have a session at this time.");
    public static readonly Error TooFarAhead = Error.Validation("TherapySession.TooFarAhead", "Sessions can be scheduled at most one year ahead.");
    public static readonly Error InvalidDuration = Error.Validation("TherapySession.InvalidDuration", "Duration must be between 5 and 480 minutes.");
    public static readonly Error InvalidWellbeing = Error.Validation("TherapySession.InvalidWellbeing", "Wellbeing must be between 1 and 5.");
    public static readonly Error CorrectionReasonRequired = Error.Validation("TherapySession.CorrectionReasonRequired", "A correction needs a reason.");
    public static readonly Error ReferralReasonRequired = Error.Validation("TherapySession.ReferralReasonRequired", "Describe the warning signs.");
    public static readonly Error ConsentRequired = Error.Conflict(
        "TherapySession.ConsentRequired",
        "The client has not given consent for this procedure on the current consent version, or has withdrawn it.");

    public static Error MissingDocumentation(IEnumerable<TherapyRecordField> fields) =>
        Error.Conflict("TherapySession.MissingDocumentation", $"Complete the mandatory record fields first: {string.Join(", ", fields)}.");
}
