using MyHealth.Domain.Abstractions;

namespace MyHealth.Application.Abstractions.Authentication;

/// <summary>Account-security rules that depend on the environment (RNF-02).</summary>
public sealed record SecurityPolicy(bool RequireTwoFactorForProfessionals)
{
    public static readonly SecurityPolicy Strict = new(RequireTwoFactorForProfessionals: true);
}

public static class SecurityErrors
{
    public static readonly Error TwoFactorRequired = Error.Forbidden(
        "Security.TwoFactorRequired",
        "Professionals and administrators must enable two-factor authentication (/api/v1/auth/manage/2fa) before using this feature.");
}
