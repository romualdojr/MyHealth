namespace MyHealth.Application.Abstractions.Authentication;

/// <summary>The authenticated caller. Endpoints require authentication, so these are always available in handlers.</summary>
public interface IUserContext
{
    Guid UserId { get; }

    string Email { get; }

    /// <summary>Platform operator (support/verification). Grants no tenant or clinical access.</summary>
    bool IsAdministrator { get; }

    /// <summary>
    /// The tenant the caller asked to act for (the <c>X-Tenant-Id</c> header), or <c>null</c> when none was sent.
    /// This is only a selector: it authorizes nothing until <see cref="Tenancy.ITenantContext"/> confirms a membership.
    /// An unparsable value is reported as <see cref="Guid.Empty"/>, which never matches a membership.
    /// </summary>
    Guid? RequestedTenantId { get; }

    /// <summary>
    /// Whether the account is protected by a second factor. Sign-in to such an account always requires the second
    /// factor, so every token it holds came from an MFA sign-in (RNF-02).
    /// </summary>
    Task<bool> HasTwoFactorAsync(CancellationToken cancellationToken);
}
