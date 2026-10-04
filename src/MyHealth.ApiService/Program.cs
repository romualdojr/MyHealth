using MyHealth.Application.Abstractions.Media;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
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
builder.Services.AddOpenApi(options => options
    .AddDocumentTransformer<BearerSecuritySchemeTransformer>()
    .AddOperationTransformer<TenantHeaderOperationTransformer>());

builder.Services.AddSingleton<IAuthorizationHandler, TwoFactorRequirementHandler>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthorizationPolicies.Administrator, policy => policy.RequireRole(Roles.Administrator).AddRequirements(new TwoFactorRequirement()));

// Brute-force protection for login, registration and password reset (per client IP and minute).
var authPermitLimit = builder.Configuration.GetValue("RateLimiting:AuthenticationPerMinute", 20);
var assistantPermitLimit = builder.Configuration.GetValue("RateLimiting:AssistantPerMinute", 20);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.Authentication, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPermitLimit, Window = TimeSpan.FromMinutes(1) }));

    // Each assistant message calls the AI provider: limited per signed-in user.
    options.AddPolicy(RateLimitPolicies.Assistant, context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = assistantPermitLimit, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// After authentication, so per-user limits (assistant) see who is calling; login stays limited per IP.
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapGet("/", () => TypedResults.Ok(new { service = "MyHealth API", version = "v1" })).ExcludeFromDescription();

// Public media (exercise videos and 3D models) from local storage, for development: production serves them from the CDN
// at Media:PublicBaseUrl. Only keys under public/ are reachable; private files (source videos) never are.
app.MapGet("/media/{**path}", async (string path, IMediaStorage storage, CancellationToken ct) =>
        await storage.OpenAsync(IMediaStorage.PublicPrefix + path, ct) is { } file
            ? Results.Stream(file.Content, file.ContentType, enableRangeProcessing: true)
            : Results.NotFound())
    .ExcludeFromDescription();

var api = app.MapGroup("/api/v1");
api.MapGroup("/auth").WithTags("Auth").RequireRateLimiting(RateLimitPolicies.Authentication).MapIdentityApi<ApplicationUser>();

var authenticated = api.MapGroup(string.Empty).RequireAuthorization();
authenticated.MapMeEndpoints();
authenticated.MapTenantEndpoints();
authenticated.MapProfessionalEndpoints();
authenticated.MapCareRelationshipEndpoints();
authenticated.MapExerciseEndpoints();
authenticated.MapTrainingPlanEndpoints();
authenticated.MapWorkoutEndpoints();
authenticated.MapTrainerEndpoints();
authenticated.MapCoachEndpoints();
authenticated.MapTrainerAssistantEndpoints();
authenticated.MapWhatsAppEndpoints(api);
authenticated.MapMotionCaptureEndpoints();
authenticated.MapMyRecordsEndpoints();
authenticated.MapAssessmentEndpoints();
authenticated.MapNutritionEndpoints();
authenticated.MapTherapyEndpoints();
authenticated.MapBillingEndpoints(api);
authenticated.MapClientExperienceEndpoints();
authenticated.MapAdminEndpoints();

if (app.Configuration.GetValue("Database:InitializeOnStartup", app.Environment.IsDevelopment()))
{
    await DatabaseInitializer.InitializeAsync(app.Services);
}

await app.RunAsync();

/// <summary>Entry point marker for integration tests (WebApplicationFactory).</summary>
public partial class Program;
