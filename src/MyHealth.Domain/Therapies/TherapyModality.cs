using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.Therapies;

/// <summary>A part of the session record that a modality can make mandatory before completion (RF-34, RF-35).</summary>
public enum TherapyRecordField
{
    ReportedConcern = 1,
    ProfessionalAssessment = 2,
    ContraindicationsAndPrecautions = 3,
    Technique = 4,
    PointsOrRegions = 5,
    DurationMinutes = 6,
    AdverseEvents = 7,
    PostSessionGuidance = 8,
}

public enum ModalityStatus
{
    /// <summary>Configured but not usable: no services, appointments or records.</summary>
    Disabled = 1,

    /// <summary>Usable after legal and clinical review (§9, RF-37).</summary>
    Enabled = 2,
}

/// <summary>
/// A therapy offered on the platform, such as acupuncture (RF-33, RF-37). Everything that depends on legal review is
/// configuration here rather than code: which council registrations may practise it, which record fields are required,
/// and whether it is enabled at all. A modality can only be enabled with a reference to the legal review that approved it.
/// </summary>
public sealed class TherapyModality : AggregateRoot<Guid>
{
    public const string Acupuncture = "acupuncture";

    /// <summary>RF-34 lists points or regions "when relevant", so they are not required by default.</summary>
    public static readonly IReadOnlyList<TherapyRecordField> AcupunctureDefaultRequiredFields =
    [
        TherapyRecordField.ReportedConcern, TherapyRecordField.ProfessionalAssessment, TherapyRecordField.ContraindicationsAndPrecautions,
        TherapyRecordField.Technique, TherapyRecordField.DurationMinutes, TherapyRecordField.AdverseEvents, TherapyRecordField.PostSessionGuidance,
    ];

    private TherapyModality(Guid id) : base(id) { }

    private TherapyModality() { }

    /// <summary>Stable lower-case code, e.g. <c>acupuncture</c>.</summary>
    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public string? Description { get; private set; }

    /// <summary>Councils whose registered professionals may practise it (e.g. CFM, COFFITO), upper-case.</summary>
    public List<string> AcceptedCouncils { get; private set; } = [];

    public List<TherapyRecordField> RequiredRecordFields { get; private set; } = [];

    public bool RequiresConsent { get; private set; }

    public ModalityStatus Status { get; private set; }

    /// <summary>The legal/clinical review that approved enabling it (e.g. an opinion number); kept for accountability.</summary>
    public string? LegalReviewReference { get; private set; }

    public DateTimeOffset? EnabledAt { get; private set; }

    public Guid? EnabledBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsEnabled => Status == ModalityStatus.Enabled;

    public static Result<TherapyModality> Define(
        string code, string name, string? description, IEnumerable<string> acceptedCouncils, IEnumerable<TherapyRecordField> requiredFields,
        bool requiresConsent, DateTimeOffset now)
    {
        var normalized = code.Trim().ToLowerInvariant();
        if (normalized.Length is < 2 or > 64 || !normalized.All(c => char.IsAsciiLetterLower(c) || c == '_'))
        {
            return TherapyErrors.InvalidModalityCode;
        }

        var modality = new TherapyModality(Guid.CreateVersion7(now)) { Code = normalized, Status = ModalityStatus.Disabled };
        modality.Apply(name, description, acceptedCouncils, requiredFields, requiresConsent, now);
        return modality;
    }

    /// <summary>
    /// Changes the configuration. Changing who may practise or what is documented on an enabled modality disables it
    /// again, because the legal review covered the previous configuration.
    /// </summary>
    public void Configure(
        string name, string? description, IEnumerable<string> acceptedCouncils, IEnumerable<TherapyRecordField> requiredFields, bool requiresConsent,
        DateTimeOffset now)
    {
        var councilsBefore = AcceptedCouncils.ToList();
        var fieldsBefore = RequiredRecordFields.ToList();
        var consentBefore = RequiresConsent;
        Apply(name, description, acceptedCouncils, requiredFields, requiresConsent, now);

        if (IsEnabled && (!councilsBefore.SequenceEqual(AcceptedCouncils) || !fieldsBefore.SequenceEqual(RequiredRecordFields) || consentBefore != RequiresConsent))
        {
            Disable(now);
        }
    }

    public Result Enable(string legalReviewReference, Guid operatorUserId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(legalReviewReference))
        {
            return TherapyErrors.LegalReviewRequired;
        }

        if (AcceptedCouncils.Count == 0)
        {
            return TherapyErrors.NoAcceptedCouncils;
        }

        Status = ModalityStatus.Enabled;
        LegalReviewReference = legalReviewReference.Trim();
        EnabledAt = now;
        EnabledBy = operatorUserId;
        UpdatedAt = now;
        return Result.Success();
    }

    public void Disable(DateTimeOffset now)
    {
        Status = ModalityStatus.Disabled;
        EnabledAt = null;
        EnabledBy = null;
        UpdatedAt = now;
    }

    /// <summary>Whether a professional's council registration is within this modality's permitted scope.</summary>
    public bool AcceptsRegistration(ProfessionalRegistration registration) => AcceptedCouncils.Contains(registration.Council);

    /// <summary>The fields of <paramref name="record"/> that are required here but still empty.</summary>
    public IReadOnlyList<TherapyRecordField> MissingFields(TherapyRecord? record) =>
        RequiredRecordFields.Where(field => record is null || !record.Has(field)).ToList();

    private void Apply(
        string name, string? description, IEnumerable<string> acceptedCouncils, IEnumerable<TherapyRecordField> requiredFields, bool requiresConsent,
        DateTimeOffset now)
    {
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        AcceptedCouncils = acceptedCouncils.Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct().Order().ToList();
        RequiredRecordFields = requiredFields.Distinct().Order().ToList();
        RequiresConsent = requiresConsent;
        UpdatedAt = now;
    }
}

public interface ITherapyModalityRepository : IRepository<TherapyModality, Guid>
{
    Task<TherapyModality?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
}

public static class TherapyErrors
{
    public static readonly Error ModalityNotFound = Error.NotFound("Therapy.ModalityNotFound", "The therapy modality was not found.");
    public static readonly Error ModalityExists = Error.Conflict("Therapy.ModalityExists", "A modality with this code already exists.");
    public static readonly Error InvalidModalityCode = Error.Validation("Therapy.InvalidModalityCode", "Use a lower-case code such as 'acupuncture'.");
    public static readonly Error ModalityDisabled = Error.Forbidden(
        "Therapy.ModalityDisabled", "This modality is not enabled on the platform yet; it needs legal and clinical review first.");
    public static readonly Error LegalReviewRequired = Error.Validation(
        "Therapy.LegalReviewRequired", "Reference the legal and clinical review that approved this modality.");
    public static readonly Error NoAcceptedCouncils = Error.Validation(
        "Therapy.NoAcceptedCouncils", "Configure which council registrations may practise this modality before enabling it.");
    public static readonly Error CouncilNotAccepted = Error.Forbidden(
        "Therapy.CouncilNotAccepted", "The professional's council registration is not within this modality's permitted scope.");
    public static readonly Error NotQualified = Error.Forbidden(
        "Therapy.NotQualified", "You need a verified qualification for this modality before offering it or providing care.");
}
