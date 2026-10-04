using System.Text.Json;

namespace MyHealth.E2ETests;

/// <summary>
/// The web dashboard on phones (Playwright device emulation: viewport, touch, user agent). Every page must fit the
/// screen without sideways scrolling, keep touch targets at least 44 px tall (Apple's and Google's guidance) and use
/// 16 px text in fields (smaller text makes iOS zoom in on focus). Screenshots land in TestResults/phones.
/// </summary>
public abstract class PhoneTests(AppFixture app, string device) : BrowserTest(app)
{
    private const int MinimumTouchTarget = 44;

    protected override BrowserNewContextOptions ContextOptions(AppFixture app) => new(app.Devices[device]);

    private async Task CheckAsync(string name)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "TestResults", "phones");
        Directory.CreateDirectory(folder);
        await Page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(folder, $"{device.Replace(' ', '-')}-{name}.png"), FullPage = true });

        var report = await Page.EvaluateAsync<JsonElement>("""
            (min) => {
              const issues = [];
              const width = window.innerWidth;
              if (document.documentElement.scrollWidth > width + 1) {
                issues.push(`page is ${document.documentElement.scrollWidth}px wide on a ${width}px screen`);
              }
              const visible = el => {
                const r = el.getBoundingClientRect();
                const s = getComputedStyle(el);
                return r.width > 0 && r.height > 0 && r.right > 0 && r.left < window.innerWidth && s.visibility !== 'hidden' && s.display !== 'none' && !el.closest('[aria-hidden=true]');
              };
              const label = el => (el.getAttribute('aria-label') || el.innerText || el.id || el.name || el.tagName).trim().slice(0, 40);
              for (const el of document.querySelectorAll('button, input:not([type=hidden]):not([type=checkbox]):not([type=radio]), select, textarea, summary, nav a, a[class*="rounded"]')) {
                if (!visible(el)) continue;
                const h = el.getBoundingClientRect().height;
                if (h < min) issues.push(`touch target "${label(el)}" is ${Math.round(h)}px tall`);
              }
              for (const el of document.querySelectorAll('input:not([type=hidden]):not([type=checkbox]):not([type=radio]), select, textarea')) {
                if (!visible(el)) continue;
                const size = parseFloat(getComputedStyle(el).fontSize);
                if (size < 16) issues.push(`field "${label(el)}" uses ${size}px text (iOS zooms in)`);
              }
              for (const el of document.querySelectorAll('main *, header *')) {
                if (!visible(el)) continue;
                const r = el.getBoundingClientRect();
                const scrolls = node => { for (let a = node.parentElement; a; a = a.parentElement) { const o = getComputedStyle(a).overflowX; if (o === 'auto' || o === 'scroll') return true; } return false; };
                // Rows that scroll sideways on purpose (chips) may extend past the screen; the page itself may not.
                if (r.right > width + 1 && !scrolls(el)) {
                  issues.push(`"${label(el)}" runs off the screen (${Math.round(r.right)}px)`);
                  break;
                }
              }
              return issues;
            }
            """, MinimumTouchTarget);

        var issues = report.EnumerateArray().Select(i => i.GetString()!).Distinct().ToList();
        issues.ShouldBeEmpty($"{device} — {name} ({Page.Url}):\n{string.Join("\n", issues)}");
    }

    [Fact]
    public async Task Sign_in_fits_the_phone()
    {
        await Page.GotoAsync(new Uri(App.Web, "login").ToString());
        await Assertions.Expect(Page.Locator("#email")).ToBeVisibleAsync();
        await CheckAsync("login");
    }

    [Fact]
    public async Task Every_trainer_page_fits_the_phone()
    {
        await SignInAsync(App.Web, AppFixture.Trainer);

        var pages = new (string Path, string Heading, string Name)[]
        {
            ("", "Olá, Tina", "home"),
            ("trainer", "Clientes", "clients"),
            ("trainer/assistant", "Assistente IA", "assistant"),
            ("trainer/exercises", "Biblioteca de exercícios", "exercises"),
            ("organization/members", "Membros", "members"),
            ("organization/audit", "Auditoria", "audit"),
            ("organization/billing", "Plano e cobrança", "billing"),
            ("account/security", "Verificação em duas etapas", "security"),
        };

        foreach (var (path, heading, name) in pages)
        {
            await OpenAsync(App.Web, path, heading);
            await CheckAsync(name);
        }
    }

    [Fact]
    public async Task A_client_and_the_plan_editor_fit_the_phone()
    {
        await SignInAsync(App.Web, AppFixture.Trainer);
        await OpenAsync(App.Web, "trainer", "Clientes");
        await Page.GetByText("Ana Souza").First.ClickAsync();
        await WaitForPageAsync("Ana Souza");
        await CheckAsync("client");

        await Page.GetByText("Força — fase 1").First.ClickAsync();
        await WaitForPageAsync("Força — fase 1");
        await CheckAsync("plan-editor");
    }

    [Fact]
    public async Task The_assistant_chat_works_on_the_phone()
    {
        await SignInAsync(App.Web, AppFixture.Trainer);
        await OpenAsync(App.Web, "trainer/assistant", "Assistente IA");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Resumo da semana de todos" }).ClickAsync();
        await ExpectTextAsync("Ana Souza —");
        await CheckAsync("assistant-chat");
    }
}

public sealed class IPhoneTests(AppFixture app) : PhoneTests(app, "iPhone 14");

public sealed class AndroidPhoneTests(AppFixture app) : PhoneTests(app, "Pixel 7");
