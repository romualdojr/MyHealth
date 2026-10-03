using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using MyHealth.Application.Abstractions.Authentication;

namespace MyHealth.Infrastructure.Identity;

internal sealed class UserContext(IHttpContextAccessor httpContextAccessor, UserManager<ApplicationUser> users) : IUserContext
{
    /// <summary>The claim Identity adds on a sign-in that used a second factor (RFC 8176 "amr").</summary>
    public const string AuthenticationMethodClaim = "amr";
    public const string MultiFactorMethod = "mfa";

    private bool? _hasTwoFactor;

    private ClaimsPrincipal Principal =>
        httpContextAccessor.HttpContext?.User is { Identity.IsAuthenticated: true } principal
            ? principal
            : throw new InvalidOperationException("The user context is only available for authenticated requests.");

    public Guid UserId => Guid.Parse(Principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public string Email => Principal.FindFirstValue(ClaimTypes.Email) ?? Principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty;

    public bool IsAdministrator => Principal.IsInRole(Roles.Administrator);

    public Guid? RequestedTenantId
    {
        get
        {
            var values = httpContextAccessor.HttpContext?.Request.Headers[TenantHeader.Name] ?? default;
            if (values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
            {
                return null;
            }

            // Several values, or one that is not a GUID, can never match a membership.
            return values.Count == 1 && Guid.TryParse(values[0], out var tenantId) ? tenantId : Guid.Empty;
        }
    }

    /// <summary>
    /// True when this sign-in used a second factor, or when the account has two-factor enabled: Identity then refuses any
    /// sign-in without it, and a refreshed token (which drops the "amr" claim) still descends from an MFA sign-in.
    /// </summary>
    public async Task<bool> HasTwoFactorAsync(CancellationToken cancellationToken)
    {
        if (_hasTwoFactor is { } known)
        {
            return known;
        }

        if (Principal.HasClaim(AuthenticationMethodClaim, MultiFactorMethod))
        {
            return (_hasTwoFactor = true).Value;
        }

        var account = await users.FindByIdAsync(UserId.ToString());
        return (_hasTwoFactor = account is not null && await users.GetTwoFactorEnabledAsync(account)).Value;
    }
}

public static class TenantHeader
{
    /// <summary>Selects the tenant a professional acts for. Authorizes nothing by itself (MT-04).</summary>
    public const string Name = "X-Tenant-Id";
}
