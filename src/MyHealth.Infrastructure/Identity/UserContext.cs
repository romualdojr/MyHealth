using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MyHealth.Application.Abstractions.Authentication;

namespace MyHealth.Infrastructure.Identity;

internal sealed class UserContext(IHttpContextAccessor httpContextAccessor) : IUserContext
{
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
}

public static class TenantHeader
{
    /// <summary>Selects the tenant a professional acts for. Authorizes nothing by itself (MT-04).</summary>
    public const string Name = "X-Tenant-Id";
}
