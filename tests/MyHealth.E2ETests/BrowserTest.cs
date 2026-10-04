using System.Text.RegularExpressions;

namespace MyHealth.E2ETests;

/// <summary>
/// One browser context per test (its own cookies), and the checks every page must pass: no Blazor error banner, no
/// API problem shown, no unhandled exception text and no errors in the browser console.
/// </summary>
public abstract class BrowserTest(AppFixture app) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly List<string> _consoleErrors = [];
    private IBrowserContext? _context;

    protected AppFixture App => app;

    protected IPage Page { get; private set; } = null!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var options = ContextOptions(app);
        options.IgnoreHTTPSErrors = true;
        options.Locale = "pt-BR";
        _context = await app.Browser.NewContextAsync(options);
        _context.SetDefaultTimeout((float)Timeout.TotalMilliseconds);
        Page = await _context.NewPageAsync();
        Page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                _consoleErrors.Add(message.Text);
            }
        };
        Page.PageError += (_, error) => _consoleErrors.Add(error);
    }

    /// <summary>The browser context: a desktop browser by default; phone tests use a device descriptor.</summary>
    protected virtual BrowserNewContextOptions ContextOptions(AppFixture app) => new();

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Signs in through the static sign-in form and waits for the app to open.</summary>
    protected async Task SignInAsync(Uri site, string email, string? returnPath = null)
    {
        await Page.GotoAsync(new Uri(site, returnPath is null ? "login" : $"login?returnUrl={Uri.EscapeDataString(returnPath)}").ToString());
        await Page.Locator("#email").FillAsync(email);
        await Page.Locator("#password").FillAsync(AppFixture.Password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();
        await Page.WaitForURLAsync(url => !url.Contains("/login", StringComparison.OrdinalIgnoreCase));
        await AssertHealthyAsync();
    }

    /// <summary>Opens a page, waits for its heading and for its data to load, then checks it is healthy.</summary>
    protected async Task OpenAsync(Uri site, string path, string heading)
    {
        await Page.GotoAsync(new Uri(site, path).ToString());
        await WaitForPageAsync(heading);
    }

    protected async Task WaitForPageAsync(string heading)
    {
        await Assertions.Expect(Page.Locator("h1").First).ToContainTextAsync(heading);
        await Assertions.Expect(Page.GetByText("Carregando…")).ToHaveCountAsync(0);
        await AssertHealthyAsync();
    }

    // Only visible matches count: phone-only copies (drawer, card lists) exist hidden on the desktop and vice versa.
    protected async Task ExpectTextAsync(string text) => await Assertions.Expect(Page.GetByText(text).Filter(new() { Visible = true }).First).ToBeVisibleAsync();

    protected ILocator Button(string name) => Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    protected ILocator Link(string name) => Page.GetByRole(AriaRole.Link, new() { Name = name, Exact = true });

    protected async Task AssertHealthyAsync()
    {
        await Assertions.Expect(Page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
        await Assertions.Expect(Page.Locator(".blazor-error-boundary")).ToHaveCountAsync(0);
        await Assertions.Expect(Page.GetByText(new Regex("NavigationException|Unhandled exception|An error occurred while processing your request"))).ToHaveCountAsync(0);

        var problems = await Page.Locator("[data-problem]").AllInnerTextsAsync();
        problems.ShouldBeEmpty($"The page {Page.Url} shows an API problem.");
        _consoleErrors.ShouldBeEmpty($"The browser console has errors on {Page.Url}.");
    }
}
