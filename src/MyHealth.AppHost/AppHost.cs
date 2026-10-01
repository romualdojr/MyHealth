var builder = DistributedApplication.CreateBuilder(args);

// Health data is sensitive: the data volume keeps it across restarts; never expose this container publicly.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("myhealth-postgres-data")
    .WithPgAdmin();

var database = postgres.AddDatabase("myhealthdb");

var apiService = builder.AddProject<Projects.MyHealth_ApiService>("apiservice")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/health");

// Optional bootstrap administrator for local development:
//   dotnet user-secrets set "Parameters:seed-admin-email" "admin@myhealth.local" --project src/MyHealth.AppHost
//   dotnet user-secrets set "Parameters:seed-admin-password" "<strong password>" --project src/MyHealth.AppHost
if (!string.IsNullOrEmpty(builder.Configuration["Parameters:seed-admin-password"]))
{
    apiService
        .WithEnvironment("Seed__AdminEmail", builder.AddParameter("seed-admin-email"))
        .WithEnvironment("Seed__AdminPassword", builder.AddParameter("seed-admin-password", secret: true));
}

builder.AddProject<Projects.MyHealth_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

// MyHealth's own operators (billing, verification, platform catalogs). Separate app and cookie from the dashboard;
// in production it belongs behind the company VPN or an IP allow list.
builder.AddProject<Projects.MyHealth_Backoffice>("backoffice")
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
