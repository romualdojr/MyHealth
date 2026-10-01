using MyHealth.Backoffice.Components;
using MyHealth.Web.Common;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// MyHealth's own operators: who uses the platform, who pays, and the platform-wide catalogs and requests. Every call
// is also authorized by the API (Administrator role with two-factor), so this app holds no privilege of its own.
builder.Services.AddMyHealthPortal(builder.Configuration, new PortalOptions
{
    Name = "MyHealth Back office",
    Tagline = "Acesso restrito aos operadores da plataforma.",
    CookieName = "myhealth.backoffice",
    OperatorsOnly = true,
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(PortalOptions).Assembly);

app.MapMyHealthAccountEndpoints();

app.MapDefaultEndpoints();

app.Run();
