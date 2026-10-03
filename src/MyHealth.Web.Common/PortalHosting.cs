using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyHealth.Web.Common.Api;

namespace MyHealth.Web.Common;

/// <summary>What differs between the apps that share the sign-in and API client.</summary>
public sealed class PortalOptions
{
    /// <summary>Shown on the sign-in page and in page titles.</summary>
    public required string Name { get; init; }

    public required string Tagline { get; init; }

    /// <summary>The auth cookie; each app has its own, so signing in to one does not sign in to the other.</summary>
    public required string CookieName { get; init; }

    /// <summary>Only platform operators may sign in (the back office).</summary>
    public bool OperatorsOnly { get; init; }
}

public static class PortalHosting
{
    /// <summary>
    /// Cookie sign-in carrying the API tokens (encrypted, HTTP-only), the API client resolved through Aspire service
    /// discovery, and the account pages' services.
    /// </summary>
    public static IServiceCollection AddMyHealthPortal(this IServiceCollection services, IConfiguration configuration, PortalOptions portal)
    {
        services.AddSingleton(portal);
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/access-denied";
                options.Cookie.Name = portal.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.SlidingExpiration = false;
            });
        services.AddAuthorization();
        services.AddCascadingAuthenticationState();

        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient(MyHealthApiClient.HttpClientName, client =>
        {
            // "https+http://" prefers HTTPS; the name is resolved by Aspire service discovery (see AppHost).
            client.BaseAddress = new(configuration["MyHealthApi:BaseAddress"] ?? "https+http://apiservice");
        });

        // Large files (exercise videos): a body stream cannot be replayed, so no retries, and a long timeout instead of the
        // default resilience pipeline's 10-second attempts.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is marked experimental.
        services.AddHttpClient(MyHealthApiClient.UploadHttpClientName, client =>
            {
                client.BaseAddress = new(configuration["MyHealthApi:BaseAddress"] ?? "https+http://apiservice");
                client.Timeout = TimeSpan.FromMinutes(15);
            })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        services.AddScoped<ApiSession>();
        services.AddScoped<MyHealthApiClient>();
        services.AddScoped<SignInService>();
        return services;
    }

    public static IEndpointRouteBuilder MapMyHealthAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/account/logout", async (HttpContext context, [FromForm] string? returnUrl) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/login");
        });
        return app;
    }
}
