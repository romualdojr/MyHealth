using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.Therapies;

/// <summary>
/// One published version of the consent text for a modality (RF-35). The wording and the disclosed risks are a legal
/// deliverable, so operators publish them; published versions are never changed. A new version supersedes the previous
/// one, and clients must consent again before their next session is completed.
/// </summary>
public sealed class ConsentTemplate : AggregateRoot<Guid>
{
    private ConsentTemplate(Guid id) : base(id) { }

    private ConsentTemplate() { }

    public Guid ModalityId { get; private set; }

    public int Version { get; private set; }

    public string Title { get; private set; } = null!;

    public string Body { get; private set; } = null!;

    public List<string> DisclosedRisks { get; private set; } = [];

    public DateTimeOffset PublishedAt { get; private set; }

    public Guid PublishedBy { get; private set; }

    public static Result<ConsentTemplate> Publish(
        TherapyModality modality, int previousVersion, string title, string body, IEnumerable<string> disclosedRisks, Guid operatorUserId, DateTimeOffset now)
    {
        var risks = disclosedRisks.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct().ToList();
        if (risks.Count == 0)
        {
            return ConsentErrors.RisksRequired;
        }

        return new ConsentTemplate(Guid.CreateVersion7(now))
        {
            ModalityId = modality.Id,
            Version = previousVersion + 1,
            Title = title.Trim(),
            Body = body.Trim(),
            DisclosedRisks = risks,
            PublishedAt = now,
            PublishedBy = operatorUserId,
        };
    }
}

public enum ConsentDecision
{
    Granted = 1,
    Refused = 2,
}

/// <summary>
/// The client's decision on one consent version, for one practitioner in one tenant (RF-35: version, date, practitioner,
/// disclosed risks, refusal or withdrawal). The risks shown are copied, so the record proves what was disclosed even if
/// the template is replaced. A refusal is recorded too; a withdrawal keeps the original decision and adds when and why.
/// </summary>
public sealed class TherapyConsent : AggregateRoot<Guid>
{
    private TherapyConsent(Guid id) : base(id) { }

    private TherapyConsent() { }

    public Guid ClientId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ModalityId { get; private set; }

    public Guid PractitionerProfileId { get; private set; }

    public Guid PractitionerUserId { get; private set; }

    public Guid TemplateId { get; private set; }

    public int TemplateVersion { get; private set; }

    public List<string> DisclosedRisks { get; private set; } = [];

    public ConsentDecision Decision { get; private set; }

    public DateTimeOffset DecidedAt { get; private set; }

    public DateTimeOffset? WithdrawnAt { get; private set; }

    public string? WithdrawalReason { get; private set; }

    /// <summary>Granted, not withdrawn, and for the template version that is current now.</summary>
    public bool IsValidFor(Guid practitionerProfileId, Guid modalityId, int currentTemplateVersion) =>
        Decision == ConsentDecision.Granted && WithdrawnAt is null && PractitionerProfileId == practitionerProfileId && ModalityId == modalityId
        && TemplateVersion == currentTemplateVersion;

    public static TherapyConsent Decide(
        Guid clientId, Guid tenantId, ProfessionalProfile practitioner, ConsentTemplate template, ConsentDecision decision, DateTimeOffset now)
    {
        var consent = new TherapyConsent(Guid.CreateVersion7(now))
        {
            ClientId = clientId,
            TenantId = tenantId,
            ModalityId = template.ModalityId,
            PractitionerProfileId = practitioner.Id,
            PractitionerUserId = practitioner.UserId,
            TemplateId = template.Id,
            TemplateVersion = template.Version,
            DisclosedRisks = template.DisclosedRisks.ToList(),
            Decision = decision,
            DecidedAt = now,
        };
        consent.Raise(new TherapyConsentChanged(consent.Id, tenantId, clientId, practitioner.UserId, decision, Withdrawn: false, now));
        return consent;
    }

    public Result Withdraw(Guid clientId, string? reason, DateTimeOffset now)
    {
        if (clientId != ClientId)
        {
            return ConsentErrors.NotFound;
        }

        if (Decision != ConsentDecision.Granted || WithdrawnAt is not null)
        {
            return ConsentErrors.NotActive;
        }

        WithdrawnAt = now;
        WithdrawalReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        Raise(new TherapyConsentChanged(Id, TenantId, ClientId, PractitionerUserId, Decision, Withdrawn: true, now));
        return Result.Success();
    }
}

public sealed record TherapyConsentChanged(
    Guid ConsentId, Guid TenantId, Guid ClientId, Guid PractitionerUserId, ConsentDecision Decision, bool Withdrawn, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

public interface IConsentTemplateRepository : IRepository<ConsentTemplate, Guid>
{
    Task<ConsentTemplate?> GetCurrentAsync(Guid modalityId, CancellationToken cancellationToken = default);
}

public interface ITherapyConsentRepository : IRepository<TherapyConsent, Guid>
{
    /// <summary>The client's latest decision for this practitioner, modality and tenant.</summary>
    Task<TherapyConsent?> GetLatestAsync(Guid clientId, Guid tenantId, Guid practitionerProfileId, Guid modalityId, CancellationToken cancellationToken = default);
}

public static class ConsentErrors
{
    public static readonly Error NotFound = Error.NotFound("Consent.NotFound", "The consent was not found.");
    public static readonly Error NotActive = Error.Conflict("Consent.NotActive", "Only a granted consent that is still in force can be withdrawn.");
    public static readonly Error RisksRequired = Error.Validation("Consent.RisksRequired", "A consent version must disclose its risks.");
    public static readonly Error NoTemplate = Error.Conflict(
        "Consent.NoTemplate", "No consent text has been published for this modality yet.");
    public static readonly Error OutdatedVersion = Error.Conflict(
        "Consent.OutdatedVersion", "A newer version of the consent text was published; review and decide on the current version.");
}
