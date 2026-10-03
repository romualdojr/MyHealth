using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Privacy;

/// <summary>
/// How long one kind of record is kept and why (RF-50, RNF-01). Configured by platform operators once legal counsel sets
/// the periods (§12.5); until then a record type has no policy. Policies are recorded and published, not yet enforced by
/// an automatic purge.
/// </summary>
public sealed class RetentionPolicy : AggregateRoot<Guid>
{
    /// <summary>The record types a policy can be set for.</summary>
    public static readonly IReadOnlyList<string> RecordTypes =
    [
        "HealthHistory", "PhysicalAssessment", "NutritionAssessment", "MealPlan", "FoodDiaryEntry", "TrainingPlan", "WorkoutSession",
        "WorkoutHeartRate", "CareRelationship", "Notification", "AuditEntry", "PrivacyRequest",
    ];

    private RetentionPolicy(Guid id) : base(id) { }

    private RetentionPolicy() { }

    public string RecordType { get; private set; } = null!;

    /// <summary>Months after the record's last relevant event (e.g. the end of the care relationship).</summary>
    public int RetentionMonths { get; private set; }

    /// <summary>The LGPD legal basis and the obligation that requires keeping the record.</summary>
    public string LegalBasis { get; private set; } = null!;

    public string? Notes { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<RetentionPolicy> Define(string recordType, int retentionMonths, string legalBasis, string? notes, Guid operatorUserId, DateTimeOffset now)
    {
        if (!RecordTypes.Contains(recordType))
        {
            return RetentionErrors.UnknownRecordType(recordType);
        }

        var policy = new RetentionPolicy(Guid.CreateVersion7(now)) { RecordType = recordType };
        var updated = policy.Update(retentionMonths, legalBasis, notes, operatorUserId, now);
        return updated.IsFailure ? updated.Error : policy;
    }

    public Result Update(int retentionMonths, string legalBasis, string? notes, Guid operatorUserId, DateTimeOffset now)
    {
        if (retentionMonths is < 1 or > 1200)
        {
            return RetentionErrors.InvalidPeriod;
        }

        if (string.IsNullOrWhiteSpace(legalBasis))
        {
            return RetentionErrors.LegalBasisRequired;
        }

        RetentionMonths = retentionMonths;
        LegalBasis = legalBasis.Trim();
        Notes = notes?.Trim();
        UpdatedBy = operatorUserId;
        UpdatedAt = now;
        return Result.Success();
    }
}

public interface IRetentionPolicyRepository : IRepository<RetentionPolicy, Guid>
{
    Task<RetentionPolicy?> GetByRecordTypeAsync(string recordType, CancellationToken cancellationToken = default);
}

public static class RetentionErrors
{
    public static readonly Error InvalidPeriod = Error.Validation("Retention.InvalidPeriod", "The retention period must be between 1 and 1200 months.");
    public static readonly Error LegalBasisRequired = Error.Validation("Retention.LegalBasisRequired", "State the legal basis for keeping the records.");

    public static Error UnknownRecordType(string recordType) =>
        Error.Validation("Retention.UnknownRecordType", $"Unknown record type '{recordType}'. Use one of: {string.Join(", ", RetentionPolicy.RecordTypes)}.");
}
