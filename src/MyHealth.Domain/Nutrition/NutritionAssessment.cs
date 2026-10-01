using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.Nutrition;

/// <summary>Everything a dietitian records in one assessment (RF-28). Restrictions and allergies are as reported by the client.</summary>
public sealed record NutritionAssessmentContent(
    DateOnly AssessedOn,
    string? Goals,
    string? ReportedRestrictions,
    string? ReportedAllergies,
    string? Preferences,
    string? MealTimes,
    IReadOnlyList<Measurement> Measurements,
    string? FollowUp,
    DateOnly? NextFollowUpOn,
    string? Notes);

public sealed record NutritionAssessmentCorrection(
    int ReplacedVersion, NutritionAssessmentContent PreviousContent, string Reason, Guid CorrectedByProfileId, DateTimeOffset CorrectedAt);

/// <summary>
/// A verified dietitian's assessment of one client in one tenant. Never visible to trainers: they see at most the
/// nutrition summary the client shares with them (RF-28, §6).
/// </summary>
public sealed class NutritionAssessment : AggregateRoot<Guid>
{
    private NutritionAssessment(Guid id) : base(id) { }

    private NutritionAssessment() { }

    public Guid TenantId { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid DietitianProfileId { get; private set; }

    public Guid DietitianUserId { get; private set; }

    /// <summary>Kept as a column, next to <see cref="Content"/>, for ordering and date filters.</summary>
    public DateOnly AssessedOn { get; private set; }

    public NutritionAssessmentContent Content { get; private set; } = null!;

    public int Version { get; private set; }

    public List<NutritionAssessmentCorrection> Corrections { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<NutritionAssessment> Record(
        Guid tenantId, ProfessionalProfile dietitian, Guid clientId, NutritionAssessmentContent content, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A nutrition assessment always belongs to a tenant.", nameof(tenantId));
        }

        var check = NutritionRules.EnsureDietitian(dietitian);
        if (check.IsFailure)
        {
            return check.Error;
        }

        var valid = Validate(content, now);
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        var assessment = new NutritionAssessment(Guid.CreateVersion7(now))
        {
            TenantId = tenantId,
            ClientId = clientId,
            DietitianProfileId = dietitian.Id,
            DietitianUserId = dietitian.UserId,
            AssessedOn = content.AssessedOn,
            Content = content,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        assessment.Raise(new NutritionAssessmentRecorded(assessment.Id, tenantId, clientId, now));
        return assessment;
    }

    public bool IsAuthoredBy(Guid profileId) => DietitianProfileId == profileId;

    public Result Correct(ProfessionalProfile dietitian, NutritionAssessmentContent content, string reason, DateTimeOffset now)
    {
        if (!IsAuthoredBy(dietitian.Id))
        {
            return AssessmentErrors.NotAssessor;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return AssessmentErrors.ReasonRequired;
        }

        var valid = Validate(content, now);
        if (valid.IsFailure)
        {
            return valid;
        }

        Corrections.Add(new NutritionAssessmentCorrection(Version, Content, reason.Trim(), dietitian.Id, now));
        Content = content;
        AssessedOn = content.AssessedOn;
        Version++;
        UpdatedAt = now;
        return Result.Success();
    }

    private static Result Validate(NutritionAssessmentContent content, DateTimeOffset now)
    {
        if (content.AssessedOn > DateOnly.FromDateTime(now.UtcDateTime).AddDays(1))
        {
            return AssessmentErrors.FutureDate;
        }

        return content.NextFollowUpOn < content.AssessedOn ? NutritionErrors.FollowUpBeforeAssessment : Result.Success();
    }
}

public sealed record NutritionAssessmentRecorded(Guid AssessmentId, Guid TenantId, Guid ClientId, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public interface INutritionAssessmentRepository : IRepository<NutritionAssessment, Guid>;

internal static class NutritionRules
{
    /// <summary>Only a qualified, verified dietitian may assess or prescribe (Law 8,234/1991, RF-29).</summary>
    public static Result EnsureDietitian(ProfessionalProfile professional)
    {
        if (professional.Discipline != Discipline.Dietitian)
        {
            return ProfessionalErrors.MissingProfile(Discipline.Dietitian);
        }

        return professional.IsVerified ? Result.Success() : ProfessionalErrors.NotVerified;
    }
}
