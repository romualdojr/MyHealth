using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.Assessments;

/// <summary>What a physical assessment looked like before a correction (§5 History: corrections keep prior records).</summary>
public sealed record PhysicalAssessmentCorrection(
    int ReplacedVersion,
    DateOnly PreviousAssessedOn,
    IReadOnlyList<Measurement> PreviousMeasurements,
    string? PreviousNotes,
    string Reason,
    Guid CorrectedByProfileId,
    DateTimeOffset CorrectedAt);

/// <summary>
/// A trainer's physical assessment of one client in one tenant (RF-24): date, assessor, measurements with method and
/// unit, and notes. Corrections bump <see cref="Version"/> and keep what was replaced, with the reason.
/// </summary>
public sealed class PhysicalAssessment : AggregateRoot<Guid>
{
    private PhysicalAssessment(Guid id) : base(id) { }

    private PhysicalAssessment() { }

    public Guid TenantId { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid AssessorProfileId { get; private set; }

    public Guid AssessorUserId { get; private set; }

    public DateOnly AssessedOn { get; private set; }

    public List<Measurement> Measurements { get; private set; } = [];

    public string? Notes { get; private set; }

    public int Version { get; private set; }

    public List<PhysicalAssessmentCorrection> Corrections { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<PhysicalAssessment> Record(
        Guid tenantId, ProfessionalProfile assessor, Guid clientId, DateOnly assessedOn, IReadOnlyList<Measurement> measurements, string? notes,
        DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A physical assessment always belongs to a tenant.", nameof(tenantId));
        }

        if (assessor.Discipline != Discipline.PersonalTrainer)
        {
            return ProfessionalErrors.MissingProfile(Discipline.PersonalTrainer);
        }

        if (!assessor.IsVerified)
        {
            return ProfessionalErrors.NotVerified;
        }

        var valid = Validate(assessedOn, measurements, now);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var assessment = new PhysicalAssessment(Guid.CreateVersion7(now))
        {
            TenantId = tenantId,
            ClientId = clientId,
            AssessorProfileId = assessor.Id,
            AssessorUserId = assessor.UserId,
            AssessedOn = assessedOn,
            Measurements = measurements.ToList(),
            Notes = notes?.Trim(),
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        assessment.Raise(new PhysicalAssessmentRecorded(assessment.Id, tenantId, clientId, now));
        return assessment;
    }

    public bool IsAuthoredBy(Guid profileId) => AssessorProfileId == profileId;

    public Result Correct(
        ProfessionalProfile assessor, DateOnly assessedOn, IReadOnlyList<Measurement> measurements, string? notes, string reason, DateTimeOffset now)
    {
        if (!IsAuthoredBy(assessor.Id))
        {
            return AssessmentErrors.NotAssessor;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return AssessmentErrors.ReasonRequired;
        }

        var valid = Validate(assessedOn, measurements, now);
        if (valid.IsFailure)
        {
            return valid;
        }

        Corrections.Add(new PhysicalAssessmentCorrection(Version, AssessedOn, Measurements, Notes, reason.Trim(), assessor.Id, now));
        AssessedOn = assessedOn;
        Measurements = measurements.ToList();
        Notes = notes?.Trim();
        Version++;
        UpdatedAt = now;
        return Result.Success();
    }

    internal static Result Validate(DateOnly assessedOn, IReadOnlyList<Measurement> measurements, DateTimeOffset now)
    {
        if (measurements.Count == 0)
        {
            return AssessmentErrors.NoMeasurements;
        }

        // One day of tolerance: the assessor's local date may be ahead of UTC.
        return assessedOn > DateOnly.FromDateTime(now.UtcDateTime).AddDays(1) ? AssessmentErrors.FutureDate : Result.Success();
    }
}

public sealed record PhysicalAssessmentRecorded(Guid AssessmentId, Guid TenantId, Guid ClientId, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public interface IPhysicalAssessmentRepository : IRepository<PhysicalAssessment, Guid>;

public static class AssessmentErrors
{
    public static readonly Error NotFound = Error.NotFound("Assessment.NotFound", "The assessment was not found.");
    public static readonly Error NotAssessor = Error.Forbidden("Assessment.NotAssessor", "Only the professional who recorded this assessment can correct it.");
    public static readonly Error ReasonRequired = Error.Validation("Assessment.ReasonRequired", "A correction needs a reason.");
    public static readonly Error NoMeasurements = Error.Validation("Assessment.NoMeasurements", "Record at least one measurement.");
    public static readonly Error FutureDate = Error.Validation("Assessment.FutureDate", "The assessment date cannot be in the future.");
}
