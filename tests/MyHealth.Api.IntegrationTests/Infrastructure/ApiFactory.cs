using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(MyHealth.Api.IntegrationTests.Infrastructure.ApiFactory))]

namespace MyHealth.Api.IntegrationTests.Infrastructure;

/// <summary>
/// One PostgreSQL container and one API host for the whole test assembly. Tests isolate themselves by creating
/// their own users, so no database reset is needed between them.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string SelectorScheme = "TestOrIdentity";

    private PostgreSqlContainer? _postgres;
    private string? _unavailableReason;

    public async ValueTask InitializeAsync()
    {
        try
        {
            // Building the container already probes the Docker endpoint, so it happens inside the try.
            _postgres = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("myhealth_tests").Build();
            await _postgres.StartAsync();
        }
        catch (Exception ex)
        {
            _unavailableReason = $"Docker is required for integration tests: {ex.Message}";
            return;
        }

        // Builds the host: applies migrations and seeds the exercise library.
        _ = Services;
    }

    /// <summary>Skips (rather than fails) when the machine has no Docker engine running.</summary>
    public void EnsureAvailable()
    {
        if (_unavailableReason is not null)
        {
            Assert.Skip(_unavailableReason);
        }
    }

    public TestUser NewUser(string? name = null, bool administrator = false)
    {
        EnsureAvailable();
        var id = Guid.CreateVersion7();
        var email = $"{name ?? "user"}-{id:N}@test.local";
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Test", TestAuthHandler.Encode(id, email, administrator));
        return new TestUser(id, email, client);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:myhealthdb", _postgres!.GetConnectionString());
        builder.UseSetting("Database:InitializeOnStartup", "true");
        builder.UseSetting("Identity:RequireConfirmedEmail", "false");

        builder.ConfigureServices(services =>
        {
            // "Authorization: Test ..." uses the test handler; anything else goes to the real Identity bearer scheme.
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { })
                .AddPolicyScheme(SelectorScheme, SelectorScheme, options => options.ForwardDefaultSelector = context =>
                    context.Request.Headers.Authorization.ToString().StartsWith(TestAuthHandler.SchemeName + " ", StringComparison.Ordinal)
                        ? TestAuthHandler.SchemeName
                        : IdentityConstants.BearerScheme);

            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = SelectorScheme;
                options.DefaultAuthenticateScheme = SelectorScheme;
                options.DefaultChallengeScheme = SelectorScheme;
            });
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }
}

public sealed record TestUser(Guid Id, string Email, HttpClient Http)
{
    /// <summary>The tenant currently selected on <see cref="Http"/>, if any.</summary>
    public Guid? TenantId =>
        Http.DefaultRequestHeaders.TryGetValues(MyHealth.Infrastructure.Identity.TenantHeader.Name, out var values) ? Guid.Parse(values.Single()) : null;
}
