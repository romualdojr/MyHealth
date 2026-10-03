using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Nutrition;

namespace MyHealth.ApiService.Endpoints;

/// <summary>
/// Dietitian workflow (RF-28 → RF-30): assessments, versioned meal plans and the client's food diary. Only verified
/// dietitians acting for the tenant can prescribe. Clients use <c>/me/meal-plans</c> and <c>/me/food-diary</c>.
/// </summary>
internal static class NutritionEndpoints
{
    public sealed record CorrectNutritionAssessmentRequest(NutritionAssessmentInput Assessment, string Reason);

    public sealed record PublishMealPlanRequest(string ChangeDescription);

    public sealed record CommentRequest(string Message);

    public static void MapNutritionEndpoints(this IEndpointRouteBuilder routes)
    {
        var assessments = routes.MapGroup("/nutrition-assessments").WithTags("Nutrition");

        assessments.MapPost("/", async (
                RecordNutritionAssessmentCommand command, ICommandHandler<RecordNutritionAssessmentCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp(id => TypedResults.Created($"/api/v1/nutrition-assessments/{id}", new { id })));

        assessments.MapGet("/", async (
                Guid clientId, IQueryHandler<ListClientNutritionAssessmentsQuery, IReadOnlyList<NutritionAssessmentResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListClientNutritionAssessmentsQuery(clientId), ct)).ToHttp());

        assessments.MapGet("/{assessmentId:guid}", async (
                Guid assessmentId, IQueryHandler<GetNutritionAssessmentQuery, NutritionAssessmentResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetNutritionAssessmentQuery(assessmentId), ct)).ToHttp());

        assessments.MapPost("/{assessmentId:guid}/corrections", async (
                Guid assessmentId, CorrectNutritionAssessmentRequest request, ICommandHandler<CorrectNutritionAssessmentCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new CorrectNutritionAssessmentCommand(assessmentId, request.Assessment, request.Reason), ct)).ToHttp());

        var plans = routes.MapGroup("/meal-plans").WithTags("Nutrition");

        plans.MapPost("/", async (CreateMealPlanCommand command, ICommandHandler<CreateMealPlanCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp(id => TypedResults.Created($"/api/v1/meal-plans/{id}", new { id })));

        plans.MapGet("/", async (Guid? clientId, IQueryHandler<ListDietitianMealPlansQuery, IReadOnlyList<DietitianMealPlanListItem>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListDietitianMealPlansQuery(clientId), ct)).ToHttp());

        plans.MapGet("/{planId:guid}", async (Guid planId, IQueryHandler<GetDietitianMealPlanQuery, DietitianMealPlanResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetDietitianMealPlanQuery(planId), ct)).ToHttp());

        plans.MapPut("/{planId:guid}", async (
                Guid planId, MealPlanDetailsInput details, ICommandHandler<UpdateMealPlanDetailsCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new UpdateMealPlanDetailsCommand(planId, details), ct)).ToHttp());

        plans.MapPut("/{planId:guid}/meals", async (
                Guid planId, IReadOnlyList<PlannedMealInput> meals, ICommandHandler<UpdateMealPlanMealsCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new UpdateMealPlanMealsCommand(planId, meals), ct)).ToHttp());

        plans.MapPost("/{planId:guid}/publish", async (
                Guid planId, PublishMealPlanRequest request, ICommandHandler<PublishMealPlanCommand, PublishedMealPlanVersionResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new PublishMealPlanCommand(planId, request.ChangeDescription), ct)).ToHttp());

        plans.MapPost("/{planId:guid}/end", async (Guid planId, ICommandHandler<EndMealPlanCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new EndMealPlanCommand(planId), ct)).ToHttp());

        // Readable by the owning dietitian and by the client.
        plans.MapGet("/{planId:guid}/versions/{versionNumber:int}", async (
                Guid planId, int versionNumber, IQueryHandler<GetMealPlanVersionQuery, MealPlanVersionResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetMealPlanVersionQuery(planId, versionNumber), ct)).ToHttp());

        var dietitian = routes.MapGroup("/dietitian").WithTags("Nutrition");

        dietitian.MapGet("/clients/{clientId:guid}/food-diary", async (
                Guid clientId, DateOnly from, DateOnly to, IQueryHandler<ListClientFoodDiaryQuery, DietitianFoodDiaryResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new ListClientFoodDiaryQuery(clientId, from, to), ct)).ToHttp());

        routes.MapPost("/food-diary/{entryId:guid}/comments", async (
                    Guid entryId, CommentRequest request, ICommandHandler<CommentOnFoodDiaryEntryCommand> handler, CancellationToken ct) =>
                (await handler.Handle(new CommentOnFoodDiaryEntryCommand(entryId, request.Message), ct)).ToHttp())
            .WithTags("Nutrition");
    }
}
