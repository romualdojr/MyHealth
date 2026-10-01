using System.Globalization;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace MyHealth.Web.Api;

/// <summary>
/// The signed-in user's API credentials for one Blazor circuit. The tokens live only in the encrypted, HTTP-only auth
/// cookie and in server memory; they are never sent to the browser's JavaScript. An access token close to expiry is
/// refreshed in memory; the cookie is refreshed on the next full page load.
/// </summary>
public sealed class ApiSession(AuthenticationStateProvider authentication, IHttpClientFactory httpClients, TimeProvider clock)
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;
    private bool _loaded;

    /// <summary>The organization the user acts for in this circuit, sent as <c>X-Tenant-Id</c>.</summary>
    public Guid? TenantId { get; set; }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        await LoadAsync();
        if (_accessToken is null)
        {
            return null;
        }

        return clock.GetUtcNow() + RefreshMargin < _expiresAt ? _accessToken : await RefreshAsync(ct);
    }

    /// <summary>Exchanges the refresh token for a new access token; <c>null</c> when the session can no longer be renewed.</summary>
    public async Task<string?> RefreshAsync(CancellationToken ct)
    {
        await LoadAsync();
        if (_refreshToken is null)
        {
            return null;
        }

        using var response = await httpClients.CreateClient(MyHealthApiClient.HttpClientName)
            .PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = _refreshToken }, MyHealthApiClient.Json, ct);
        if (!response.IsSuccessStatusCode)
        {
            _accessToken = null;
            _refreshToken = null;
            return null;
        }

        var tokens = (await response.Content.ReadFromJsonAsync<TokenResponse>(MyHealthApiClient.Json, ct))!;
        _accessToken = tokens.AccessToken;
        _refreshToken = tokens.RefreshToken;
        _expiresAt = clock.GetUtcNow().AddSeconds(tokens.ExpiresIn);
        return _accessToken;
    }

    private async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }

        var user = (await authentication.GetAuthenticationStateAsync()).User;
        _accessToken = user.FindFirstValue(SessionClaims.AccessToken);
        _refreshToken = user.FindFirstValue(SessionClaims.RefreshToken);
        _expiresAt = DateTimeOffset.TryParse(user.FindFirstValue(SessionClaims.ExpiresAt), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : DateTimeOffset.MinValue;
        _loaded = true;
    }
}

/// <summary>Claims stored in the dashboard's own (encrypted) auth cookie.</summary>
public static class SessionClaims
{
    public const string AccessToken = "myhealth:access_token";
    public const string RefreshToken = "myhealth:refresh_token";
    public const string ExpiresAt = "myhealth:expires_at";
    public const string HasProfile = "myhealth:has_profile";
    public const string AdministratorRole = "Administrator";
}
