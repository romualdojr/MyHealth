using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Common;
using MyHealth.Application.Exercises;
using MyHealth.Application.TrainingPlans;
using MyHealth.Application.Workouts;

namespace MyHealth.ApiService.Endpoints;

internal static class ExerciseEndpoints
{
    public static void MapExerciseEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/exercises").WithTags("Exercise library");

        group.MapGet("/", async (
                string? search, string? muscleGroup, string? equipment, string? purpose, int? page, int? pageSize,
                IQueryHandler<SearchExercisesQuery, PagedResponse<ExerciseResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new SearchExercisesQuery(search, muscleGroup, equipment, purpose, page ?? 1, pageSize ?? 50), ct)).ToHttp());

        group.MapGet("/{exerciseId:guid}", async (Guid exerciseId, IQueryHandler<GetExerciseQuery, ExerciseResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetExerciseQuery(exerciseId), ct)).ToHttp());

        group.MapPost("/", async (ExerciseInput exercise, ICommandHandler<CreateExerciseCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(new CreateExerciseCommand(exercise), ct)).ToHttp(id => TypedResults.Created($"/api/v1/exercises/{id}", new { id })));

        group.MapPut("/{exerciseId:guid}", async (Guid exerciseId, ExerciseInput exercise, ICommandHandler<UpdateExerciseCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new UpdateExerciseCommand(exerciseId, exercise), ct)).ToHttp());
    }
}

/// <summary>Trainer prescription workflow (RF-10 → RF-13). Clients read their plans under <c>/me/training-plans</c>.</summary>
internal static class TrainingPlanEndpoints
{
    public sealed record PublishRequest(string ChangeDescription);

    public sealed record CopyRequest(Guid TargetClientId, string? NewName);

    public static void MapTrainingPlanEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/training-plans").WithTags("Training plans");

        group.MapPost("/", async (CreateTrainingPlanCommand command, ICommandHandler<CreateTrainingPlanCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp(id => TypedResults.Created($"/api/v1/training-plans/{id}", new { id })));

        group.MapGet("/", async (Guid? clientId, IQueryHandler<ListTrainerPlansQuery, IReadOnlyList<TrainerPlanListItem>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListTrainerPlansQuery(clientId), ct)).ToHttp());

        group.MapGet("/{planId:guid}", async (Guid planId, IQueryHandler<GetTrainerPlanQuery, TrainerPlanResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetTrainerPlanQuery(planId), ct)).ToHttp());

        group.MapPut("/{planId:guid}", async (Guid planId, PlanDetailsInput details, ICommandHandler<UpdateTrainingPlanDetailsCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new UpdateTrainingPlanDetailsCommand(planId, details), ct)).ToHttp());

        group.MapPut("/{planId:guid}/sessions", async (
                Guid planId, IReadOnlyList<PlannedSessionInput> sessions, ICommandHandler<UpdateTrainingPlanSessionsCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new UpdateTrainingPlanSessionsCommand(planId, sessions), ct)).ToHttp());

        group.MapPost("/{planId:guid}/publish", async (
                Guid planId, PublishRequest request, ICommandHandler<PublishTrainingPlanCommand, PublishedVersionResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new PublishTrainingPlanCommand(planId, request.ChangeDescription), ct)).ToHttp());

        group.MapPost("/{planId:guid}/end", async (Guid planId, ICommandHandler<EndTrainingPlanCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new EndTrainingPlanCommand(planId), ct)).ToHttp());

        // The prescription is still appropriate after a flagged change (RF-23); publishing a new version also clears the flag.
        group.MapPost("/{planId:guid}/acknowledge-review", async (Guid planId, ICommandHandler<AcknowledgeTrainingPlanReviewCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new AcknowledgeTrainingPlanReviewCommand(planId), ct)).ToHttp());

        group.MapPost("/{planId:guid}/copy", async (Guid planId, CopyRequest request, ICommandHandler<CopyTrainingPlanCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(new CopyTrainingPlanCommand(planId, request.TargetClientId, request.NewName), ct))
            .ToHttp(id => TypedResults.Created($"/api/v1/training-plans/{id}", new { id })));

        // Readable by the owning trainer and by the client: lets clients see exactly which version they performed.
        group.MapGet("/{planId:guid}/versions/{versionNumber:int}", async (
                Guid planId, int versionNumber, IQueryHandler<GetPlanVersionQuery, PlanVersionResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetPlanVersionQuery(planId, versionNumber), ct)).ToHttp());
    }
}

internal static class TrainerEndpoints
{
    public static void MapTrainerEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/trainer").WithTags("Trainer dashboard");

        group.MapGet("/dashboard", async (IQueryHandler<GetTrainerDashboardQuery, TrainerDashboardResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetTrainerDashboardQuery(), ct)).ToHttp());

        group.MapGet("/clients/{clientId:guid}/workout-sessions", async (
                Guid clientId, DateTimeOffset? from, DateTimeOffset? to,
                IQueryHandler<ListClientWorkoutSessionsQuery, IReadOnlyList<WorkoutSessionListItem>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListClientWorkoutSessionsQuery(clientId, from, to), ct)).ToHttp());
    }
}
