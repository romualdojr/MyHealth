using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MyHealth.Web.Common.Api;

public enum SignInOutcome
{
    Succeeded = 1,
    RequiresTwoFactor = 2,
    LockedOut = 3,
    Failed = 4,
}

public sealed record SignInResult(SignInOutcome Outcome, ClaimsPrincipal? Principal);

/// <summary>
/// Signs in against the API's Identity endpoints (bearer tokens, never API cookies) and builds the dashboard's cookie
/// identity: who the person is, whether they are a platform operator, and the tokens to call the API with.
/// </summary>
public sealed class SignInService(IHttpClientFactory httpClients, TimeProvider clock)
{
    /// <param name="clientAddress">The browser's address, sent as <c>X-Forwarded-For</c> so the API limits sign-in attempts per person, not per dashboard.</param>
    public async Task<SignInResult> SignInAsync(string email, string password, string? twoFactorCode, CancellationToken ct, System.Net.IPAddress? clientAddress = null)
    {
        var http = httpClients.CreateClient(MyHealthApiClient.HttpClientName);
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(
                new { email, password, twoFactorCode = string.IsNullOrWhiteSpace(twoFactorCode) ? null : twoFactorCode.Trim() }, options: MyHealthApiClient.Json),
        };
        if (clientAddress is not null)
        {
            loginRequest.Headers.Add("X-Forwarded-For", clientAddress.ToString());
        }

        using var login = await http.SendAsync(loginRequest, ct);

        if (!login.IsSuccessStatusCode)
        {
            // Identity reports the reason in the problem detail.
            var detail = await login.Content.ReadAsStringAsync(ct);
            var outcome = detail.Contains("RequiresTwoFactor", StringComparison.Ordinal) ? SignInOutcome.RequiresTwoFactor
                : detail.Contains("LockedOut", StringComparison.Ordinal) ? SignInOutcome.LockedOut
                : SignInOutcome.Failed;
            return new SignInResult(outcome, null);
        }

        var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>(MyHealthApiClient.Json, ct))!;
        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, email),
            new(SessionClaims.AccessToken, tokens.AccessToken),
            new(SessionClaims.RefreshToken, tokens.RefreshToken),
            new(SessionClaims.ExpiresAt, clock.GetUtcNow().AddSeconds(tokens.ExpiresIn).ToString("O", CultureInfo.InvariantCulture)),
        };

        using var meRequest = Authorized(HttpMethod.Get, "/api/v1/me", tokens.AccessToken);
        using var me = await http.SendAsync(meRequest, ct);
        var isAdministrator = false;
        if (me.IsSuccessStatusCode)
        {
            var profile = (await me.Content.ReadFromJsonAsync<MeResponse>(MyHealthApiClient.Json, ct))!;
            claims.Add(new Claim(ClaimTypes.NameIdentifier, profile.UserId.ToString()));
            claims.Add(new Claim(ClaimTypes.Name, profile.FullName));
            claims.Add(new Claim(SessionClaims.HasProfile, "true"));
            isAdministrator = profile.IsAdministrator;
        }
        else
        {
            // Operators may have no personal profile; ask the API whether they hold the operator role.
            claims.Add(new Claim(ClaimTypes.Name, email));
            using var probe = Authorized(HttpMethod.Get, "/api/v1/admin/tenants", tokens.AccessToken);
            using var admin = await http.SendAsync(probe, ct);
            isAdministrator = admin.StatusCode == HttpStatusCode.OK;
        }

        if (isAdministrator)
        {
            claims.Add(new Claim(ClaimTypes.Role, SessionClaims.AdministratorRole));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new SignInResult(SignInOutcome.Succeeded, new ClaimsPrincipal(identity));
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
