using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MyHealth.ApiService.Endpoints;
using MyHealth.ApiService.Infrastructure;
using MyHealth.Application;
using MyHealth.Infrastructure;
using MyHealth.Infrastructure.Identity;
using MyHealth.Infrastructure.Persistence;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructure();
builder.Services.AddApplication();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthorizationPolicies.Administrator, policy => policy.RequireRole(Roles.Administrator));

// Brute-force protection for login, registration and password reset.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.Authentication, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapGet("/", () => TypedResults.Ok(new { service = "MyHealth API", version = "v1" })).ExcludeFromDescription();

var api = app.MapGroup("/api/v1");
api.MapGroup("/auth").WithTags("Auth").RequireRateLimiting(RateLimitPolicies.Authentication).MapIdentityApi<ApplicationUser>();

var authenticated = api.MapGroup(string.Empty).RequireAuthorization();
authenticated.MapMeEndpoints();
authenticated.MapProfessionalEndpoints();
authenticated.MapCareRelationshipEndpoints();
authenticated.MapExerciseEndpoints();
authenticated.MapTrainingPlanEndpoints();
authenticated.MapWorkoutEndpoints();
authenticated.MapTrainerEndpoints();
authenticated.MapAdminEndpoints();

if (app.Configuration.GetValue("Database:InitializeOnStartup", app.Environment.IsDevelopment()))
{
    await DatabaseInitializer.InitializeAsync(app.Services);
}

await app.RunAsync();

/// <summary>Entry point marker for integration tests (WebApplicationFactory).</summary>
public partial class Program;
