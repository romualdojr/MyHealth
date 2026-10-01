using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Assessments;
using MyHealth.Application.Notifications;
using MyHealth.Application.Nutrition;
using MyHealth.Application.Privacy;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Privacy;

namespace MyHealth.ApiService.Endpoints;

/// <summary>
/// The client's own health records (Phase 2): health history, assessments, progress, nutrition, privacy requests and
/// export. Like the rest of <c>/me</c>, these span every organization the client works with (MT-10).
/// </summary>
internal static class MyRecordsEndpoints
{
    public sealed record SubmitHealthHistoryRequest(
        string? Goals, string? ExerciseHistory, string? Limitations, string? ReportedInjuries, string? Availability, string? Equipment, bool Acknowledged);

    public sealed record FoodDiaryEntryRequest(
        Guid? MealPlanId, Guid? PlannedMealId, Guid? RelationshipId, string? MealName, DateTimeOffset EatenAt, string? TimeZoneId, string? Description,
        int? WaterMl, MealAdherence? Adherence, string? ClientNotes);

    public sealed record PrivacyRequestBody(PrivacyRequestType Type, string Details);

    public sealed record PushDeviceRequest(PushPlatform Platform, string Token);

    public static void MapMyRecordsEndpoints(this IEndpointRouteBuilder routes)
    {
        var me = routes.MapGroup("/me");

        var history = me.MapGroup("/health-history").WithTags("Health history");

        history.MapGet("/", async (IQueryHandler<GetMyHealthHistoryQuery, HealthHistoryResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetMyHealthHistoryQuery(), ct)).ToHttp());

        history.MapPut("/", async (SubmitHealthHistoryRequest r, ICommandHandler<SubmitHealthHistoryCommand, HealthHistoryRevisionItem> handler, CancellationToken ct) =>
            (await handler.Handle(
                new SubmitHealthHistoryCommand(
                    new HealthHistoryInput(r.Goals, r.ExerciseHistory, r.Limitations, r.ReportedInjuries, r.Availability, r.Equipment), r.Acknowledged),
                ct)).ToHttp());

        history.MapGet("/revisions/{revisionNumber:int}", async (
                int revisionNumber, IQueryHandler<GetMyHealthHistoryRevisionQuery, HealthHistoryRevisionResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetMyHealthHistoryRevisionQuery(revisionNumber), ct)).ToHttp());

        me.MapGet("/physical-assessments", async (
                    IQueryHandler<ListMyPhysicalAssessmentsQuery, IReadOnlyList<PhysicalAssessmentResponse>> handler, CancellationToken ct) =>
                (await handler.Handle(new ListMyPhysicalAssessmentsQuery(), ct)).ToHttp())
            .WithTags("Physical assessments");

        me.MapGet("/progress", async (DateOnly? from, DateOnly? to, IQueryHandler<GetMyProgressQuery, MyProgressResponse> handler, CancellationToken ct) =>
                (await handler.Handle(new GetMyProgressQuery(from, to), ct)).ToHttp())
            .WithTags("Me");

        var nutrition = me.MapGroup(string.Empty).WithTags("Nutrition");

        nutrition.MapGet("/nutrition-assessments", async (
                IQueryHandler<ListMyNutritionAssessmentsQuery, IReadOnlyList<NutritionAssessmentResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyNutritionAssessmentsQuery(), ct)).ToHttp());

        nutrition.MapGet("/meal-plans", async (IQueryHandler<ListMyMealPlansQuery, IReadOnlyList<ClientMealPlanListItem>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyMealPlansQuery(), ct)).ToHttp());

        nutrition.MapGet("/meal-plans/{planId:guid}", async (Guid planId, IQueryHandler<GetMyMealPlanQuery, ClientMealPlanResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetMyMealPlanQuery(planId), ct)).ToHttp());

        // The phone generates the entry id, so saving is an idempotent upsert that can be retried offline (RNF-09).
        nutrition.MapPut("/food-diary/{entryId:guid}", async (
                Guid entryId, FoodDiaryEntryRequest r, ICommandHandler<SaveFoodDiaryEntryCommand> handler, CancellationToken ct) =>
            (await handler.Handle(
                new SaveFoodDiaryEntryCommand(
                    entryId, r.MealPlanId, r.PlannedMealId, r.RelationshipId, r.MealName, r.EatenAt, r.TimeZoneId, r.Description, r.WaterMl, r.Adherence,
                    r.ClientNotes),
                ct)).ToHttp());

        nutrition.MapGet("/food-diary", async (
                DateOnly from, DateOnly to, IQueryHandler<ListMyFoodDiaryQuery, IReadOnlyList<FoodDiaryEntryResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyFoodDiaryQuery(from, to), ct)).ToHttp());

        var privacy = me.MapGroup(string.Empty).WithTags("Privacy");

        privacy.MapPost("/privacy-requests", async (PrivacyRequestBody body, ICommandHandler<SubmitPrivacyRequestCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(new SubmitPrivacyRequestCommand(body.Type, body.Details), ct))
            .ToHttp(id => TypedResults.Created("/api/v1/me/privacy-requests", new { id })));

        privacy.MapGet("/privacy-requests", async (IQueryHandler<ListMyPrivacyRequestsQuery, IReadOnlyList<PrivacyRequestResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyPrivacyRequestsQuery(), ct)).ToHttp());

        privacy.MapPost("/data-exports", async (ICommandHandler<RequestDataExportCommand, DataExportResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new RequestDataExportCommand(), ct)).ToHttp(export => TypedResults.Created(export.DownloadPath, export)));

        // A structured, readable JSON document with the user's records and their provenance (RNF-04).
        privacy.MapGet("/data-exports/{exportId:guid}/download", async (
                Guid exportId, ICommandHandler<DownloadDataExportCommand, DataExportPackage> handler, IOptions<JsonOptions> json, CancellationToken ct) =>
            (await handler.Handle(new DownloadDataExportCommand(exportId), ct)).ToHttp(package => Results.File(
                JsonSerializer.SerializeToUtf8Bytes(package, ExportJson.Get(json.Value.SerializerOptions)),
                "application/json",
                $"myhealth-export-{package.GeneratedAt:yyyyMMdd}.json")));

        var devices = me.MapGroup("/push-devices").WithTags("Notifications");

        devices.MapPut("/{installationId:guid}", async (
                Guid installationId, PushDeviceRequest request, ICommandHandler<RegisterPushDeviceCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new RegisterPushDeviceCommand(installationId, request.Platform, request.Token), ct)).ToHttp());

        devices.MapDelete("/{installationId:guid}", async (Guid installationId, ICommandHandler<UnregisterPushDeviceCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new UnregisterPushDeviceCommand(installationId), ct)).ToHttp());
    }
}

/// <summary>The API's JSON settings, indented so the export is readable as well as structured.</summary>
internal static class ExportJson
{
    private static JsonSerializerOptions? _options;

    public static JsonSerializerOptions Get(JsonSerializerOptions apiOptions) =>
        _options ??= new JsonSerializerOptions(apiOptions) { WriteIndented = true };
}
