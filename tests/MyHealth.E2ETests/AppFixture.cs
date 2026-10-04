using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: AssemblyFixture(typeof(MyHealth.E2ETests.AppFixture))]

namespace MyHealth.E2ETests;

/// <summary>
/// Starts the whole application once for all browser tests: PostgreSQL (a throwaway container, never the developer's
/// data volume), the API (which seeds the Development accounts on a fresh database), the web dashboard and the back
/// office, plus a headless Microsoft Edge driven by Playwright. Set <c>E2E_HEADED=1</c> to watch the browser.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    /// <summary>The shared password of the Development seed (src/MyHealth.ApiService/appsettings.Development.json).</summary>
    public const string Password = "MyHealth-dev-2026";

    public const string Operator = "operador@myhealth.dev";
    public const string Trainer = "treinador@myhealth.dev";
    public const string Dietitian = "nutri@myhealth.dev";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);

    private DistributedApplication? _app;
    private IPlaywright? _playwright;

    public Uri Web { get; private set; } = null!;

    public Uri Backoffice { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>Playwright's device descriptors (viewport, touch, user agent), for the phone tests.</summary>
    public IReadOnlyDictionary<string, BrowserNewContextOptions> Devices => _playwright!.Devices;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.MyHealth_AppHost>(ct);
        builder.Services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        // A clean database for every run: drop the named data volume the AppHost uses for local development.
        foreach (var resource in builder.Resources.Where(r => r.Name == "postgres"))
        {
            foreach (var mount in resource.Annotations.OfType<ContainerMountAnnotation>().ToList())
            {
                resource.Annotations.Remove(mount);
            }
        }

        // Every sign-in of the browser tests reaches the API from the web apps' single address: lift the per-IP limit.
        foreach (var api in builder.Resources.Where(r => r.Name == "apiservice"))
        {
            api.Annotations.Add(new EnvironmentCallbackAnnotation("RateLimiting__AuthenticationPerMinute", () => "1000"));
        }

        _app = await builder.BuildAsync(ct).WaitAsync(StartTimeout, ct);
        await _app.StartAsync(ct).WaitAsync(StartTimeout, ct);

        // The API is healthy only after migrations and the Development seed ran.
        foreach (var name in new[] { "apiservice", "webfrontend", "backoffice" })
        {
            await _app.ResourceNotifications.WaitForResourceHealthyAsync(name, ct).WaitAsync(StartTimeout, ct);
        }

        Web = _app.GetEndpoint("webfrontend", "https");
        Backoffice = _app.GetEndpoint("backoffice", "https");

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = "msedge",
            Headless = Environment.GetEnvironmentVariable("E2E_HEADED") is null,
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }
}
