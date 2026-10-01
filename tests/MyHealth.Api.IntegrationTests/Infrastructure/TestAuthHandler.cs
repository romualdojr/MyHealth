using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyHealth.Infrastructure.Identity;

namespace MyHealth.Api.IntegrationTests.Infrastructure;

/// <summary>Authenticates "Authorization: Test &lt;base64(id|email|admin)&gt;" so tests can act as any user quickly.</summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    public static string Encode(Guid userId, string email, bool administrator) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userId}|{email}|{administrator}"));

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(SchemeName + " ", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var parts = Encoding.UTF8.GetString(Convert.FromBase64String(header[(SchemeName.Length + 1)..])).Split('|');
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, parts[0]),
            new(ClaimTypes.Email, parts[1]),
            new(ClaimTypes.Name, parts[1]),
        ];
        if (bool.Parse(parts[2]))
        {
            claims.Add(new Claim(ClaimTypes.Role, Roles.Administrator));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
