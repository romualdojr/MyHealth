using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Assistant;

/// <summary>Which of the client's records the assistant may read to answer.</summary>
public enum AssistantScope
{
    Training = 1,
    Nutrition = 2,
    Therapies = 3,
}

public sealed record AssistantConsentChange(DateTimeOffset At, int? TermsVersion, IReadOnlyList<AssistantScope> Scopes, bool Active);

/// <summary>
/// The client's consent to the assistant, which sends the data needed for each request to an AI provider (LGPD
/// sensitive data). One per user. It is versioned like therapy consents: a new terms version needs a new acceptance,
/// and it can be withdrawn at any time. The app works fully without it.
/// </summary>
public sealed class AssistantConsent : AggregateRoot<Guid>
{
    private AssistantConsent(Guid id) : base(id) { }

    private AssistantConsent() { }

    public Guid UserId { get; private set; }

    public int? TermsVersion { get; private set; }

    public List<AssistantScope> Scopes { get; private set; } = [];

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset? WithdrawnAt { get; private set; }

    public List<AssistantConsentChange> History { get; private set; } = [];

    public bool IsActiveFor(int currentTermsVersion) => WithdrawnAt is null && TermsVersion == currentTermsVersion;

    public bool Allows(AssistantScope scope, int currentTermsVersion) => IsActiveFor(currentTermsVersion) && Scopes.Contains(scope);

    public static Result<AssistantConsent> Accept(Guid userId, int termsVersion, int currentTermsVersion, IEnumerable<AssistantScope> scopes, DateTimeOffset now)
    {
        var consent = new AssistantConsent(Guid.CreateVersion7(now)) { UserId = userId };
        var accepted = consent.Renew(termsVersion, currentTermsVersion, scopes, now);
        return accepted.IsSuccess ? consent : accepted.Error;
    }

    /// <summary>Accepts again (a new terms version, after withdrawing) or changes what the assistant may read.</summary>
    public Result Renew(int termsVersion, int currentTermsVersion, IEnumerable<AssistantScope> scopes, DateTimeOffset now)
    {
        if (termsVersion != currentTermsVersion)
        {
            return AssistantErrors.OutdatedTerms;
        }

        var chosen = scopes.Distinct().Order().ToList();
        if (chosen.Count == 0)
        {
            return AssistantErrors.NoScopes;
        }

        if (IsActiveFor(currentTermsVersion) && chosen.SequenceEqual(Scopes))
        {
            return Result.Success();
        }

        if (!IsActiveFor(currentTermsVersion))
        {
            AcceptedAt = now;
        }

        TermsVersion = termsVersion;
        Scopes = chosen;
        WithdrawnAt = null;
        History = [.. History, new AssistantConsentChange(now, termsVersion, chosen, Active: true)];
        return Result.Success();
    }

    public void Withdraw(DateTimeOffset now)
    {
        if (WithdrawnAt is not null)
        {
            return;
        }

        WithdrawnAt = now;
        History = [.. History, new AssistantConsentChange(now, TermsVersion, Scopes, Active: false)];
    }
}

public interface IAssistantConsentRepository : IRepository<AssistantConsent, Guid>
{
    Task<AssistantConsent?> GetByUserAsync(Guid userId, CancellationToken cancellationToken = default);
}

public static class AssistantErrors
{
    public static readonly Error ConsentRequired = Error.Forbidden(
        "Assistant.ConsentRequired", "Accept the assistant terms (current version) before using it.");
    public static readonly Error OutdatedTerms = Error.Validation("Assistant.OutdatedTerms", "Accept the current version of the assistant terms.");
    public static readonly Error NoScopes = Error.Validation("Assistant.NoScopes", "Choose at least one area the assistant may read.");
    public static readonly Error ProposalNotFound = Error.NotFound("Assistant.ProposalNotFound", "The suggestion was not found.");
    public static readonly Error ProposalClosed = Error.Conflict("Assistant.ProposalClosed", "This suggestion was already confirmed, discarded or has expired.");
    public static readonly Error Unavailable = Error.Failure("Assistant.Unavailable", "The assistant is not available right now. Try again or use the forms.");
}
