using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.Therapies;

public enum QualificationStatus
{
    Pending = 1,
    Verified = 2,
    Rejected = 3,
    Revoked = 4,
}

/// <summary>
/// A practitioner's training for one modality, e.g. an acupuncture specialization certificate (RF-33). Like the
/// council registration it belongs to the person, so it is global and verified once by a platform operator, who also
/// confirms the registration is within the modality's permitted scope (§9).
/// </summary>
public sealed class PractitionerQualification : AggregateRoot<Guid>
{
    private PractitionerQualification(Guid id) : base(id) { }

    private PractitionerQualification() { }

    public Guid ProfessionalProfileId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid ModalityId { get; private set; }

    /// <summary>What the training is, e.g. "Especialização em Acupuntura".</summary>
    public string Title { get; private set; } = null!;

    public string Issuer { get; private set; } = null!;

    public string? CertificateNumber { get; private set; }

    public DateOnly? IssuedOn { get; private set; }

    public QualificationStatus Status { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public Guid? ReviewedBy { get; private set; }

    public string? ReviewNotes { get; private set; }

    public bool IsVerified => Status == QualificationStatus.Verified;

    public static Result<PractitionerQualification> Submit(
        ProfessionalProfile practitioner, TherapyModality modality, string title, string issuer, string? certificateNumber, DateOnly? issuedOn,
        DateTimeOffset now)
    {
        if (practitioner.Discipline != Discipline.TherapyPractitioner)
        {
            return ProfessionalErrors.MissingProfile(Discipline.TherapyPractitioner);
        }

        return new PractitionerQualification(Guid.CreateVersion7(now))
        {
            ProfessionalProfileId = practitioner.Id,
            UserId = practitioner.UserId,
            ModalityId = modality.Id,
            Title = title.Trim(),
            Issuer = issuer.Trim(),
            CertificateNumber = certificateNumber?.Trim(),
            IssuedOn = issuedOn,
            Status = QualificationStatus.Pending,
            SubmittedAt = now,
        };
    }

    /// <summary>Verifies the training, refusing it when the practitioner's council is outside the modality's scope.</summary>
    public Result Verify(ProfessionalProfile practitioner, TherapyModality modality, Guid operatorUserId, string? notes, DateTimeOffset now)
    {
        if (practitioner.Id != ProfessionalProfileId || modality.Id != ModalityId)
        {
            throw new ArgumentException("The profile and modality must be the ones the qualification was submitted for.");
        }

        if (!modality.AcceptsRegistration(practitioner.Registration))
        {
            return TherapyErrors.CouncilNotAccepted;
        }

        Status = QualificationStatus.Verified;
        Review(operatorUserId, notes, now);
        return Result.Success();
    }

    public Result Reject(Guid operatorUserId, string reason, DateTimeOffset now) => Close(QualificationStatus.Rejected, operatorUserId, reason, now);

    /// <summary>Withdraws a verified qualification (e.g. the certificate was cancelled). Care stops at once.</summary>
    public Result Revoke(Guid operatorUserId, string reason, DateTimeOffset now) => Close(QualificationStatus.Revoked, operatorUserId, reason, now);

    private Result Close(QualificationStatus status, Guid operatorUserId, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ProfessionalErrors.ReasonRequired;
        }

        Status = status;
        Review(operatorUserId, reason.Trim(), now);
        return Result.Success();
    }

    private void Review(Guid operatorUserId, string? notes, DateTimeOffset now)
    {
        ReviewedAt = now;
        ReviewedBy = operatorUserId;
        ReviewNotes = notes;
    }
}

public interface IPractitionerQualificationRepository : IRepository<PractitionerQualification, Guid>
{
    Task<PractitionerQualification?> GetCurrentAsync(Guid professionalProfileId, Guid modalityId, CancellationToken cancellationToken = default);
}

public static class QualificationErrors
{
    public static readonly Error NotFound = Error.NotFound("Qualification.NotFound", "The qualification was not found.");
    public static readonly Error AlreadySubmitted = Error.Conflict(
        "Qualification.AlreadySubmitted", "You already have a pending or verified qualification for this modality.");
}
