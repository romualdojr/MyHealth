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
}
