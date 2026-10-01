using MyHealth.Web.Api;
using MyHealth.Web.Common;
using MyHealth.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Professionals and organization administrators. Platform operators use the back office (MyHealth.Backoffice).
builder.Services.AddMyHealthPortal(builder.Configuration, new PortalOptions
{
    Name = "MyHealth",
    Tagline = "Painel para profissionais e organizações. Clientes usam o aplicativo.",
    CookieName = "myhealth.web",
});
builder.Services.AddScoped<WorkspaceState>();

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
