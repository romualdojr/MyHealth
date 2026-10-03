using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Coaching;
using MyHealth.Application.Common;
using MyHealth.Application.Exercises;
using MyHealth.Application.MotionCapture;
using Microsoft.AspNetCore.Http.Features;
using MyHealth.Application.TrainingPlans;
using MyHealth.Application.Workouts;

namespace MyHealth.ApiService.Endpoints;

internal static class ExerciseEndpoints
{
    public sealed record ExerciseMediaRequest(IReadOnlyList<ExerciseMediaInput> Media);

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

        // Videos and images of an exercise the trainer created; library media is managed under /admin/exercises.
        group.MapPut("/{exerciseId:guid}/media", async (Guid exerciseId, ExerciseMediaRequest request, ICommandHandler<SetExerciseMediaCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new SetExerciseMediaCommand(exerciseId, request.Media), ct)).ToHttp());
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

/// <summary>The trainer's AI coach: suggestions for a client's plan, applied to the draft only by the trainer.</summary>
internal static class CoachEndpoints
{
    public sealed record AnalysisRequest(string? Focus);

    public static void MapCoachEndpoints(this IEndpointRouteBuilder routes)
    {
        var plans = routes.MapGroup("/training-plans/{planId:guid}/coach").WithTags("AI coach");

        plans.MapGet("/", async (Guid planId, IQueryHandler<GetCoachOverviewQuery, CoachOverviewResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetCoachOverviewQuery(planId), ct)).ToHttp());

        // Each analysis calls the AI provider: limited per user like the client assistant, and per tenant per day.
        plans.MapPost("/analyses", async (
                Guid planId, AnalysisRequest? request, ICommandHandler<RequestCoachAnalysisCommand, CoachAnalysisResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new RequestCoachAnalysisCommand(planId, request?.Focus), ct)).ToHttp())
            .RequireRateLimiting(RateLimitPolicies.Assistant);

        var analyses = routes.MapGroup("/coach-analyses/{analysisId:guid}/suggestions/{suggestionId:guid}").WithTags("AI coach");

        analyses.MapPost("/apply", async (
                Guid analysisId, Guid suggestionId, ICommandHandler<ResolveCoachSuggestionCommand, CoachAnalysisResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new ResolveCoachSuggestionCommand(analysisId, suggestionId, Apply: true), ct)).ToHttp());

        analyses.MapPost("/dismiss", async (
                Guid analysisId, Guid suggestionId, ICommandHandler<ResolveCoachSuggestionCommand, CoachAnalysisResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new ResolveCoachSuggestionCommand(analysisId, suggestionId, Apply: false), ct)).ToHttp());
    }
}

/// <summary>3D exercise videos: the trainer films a movement, the motion-capture provider turns it into a 3D model.</summary>
internal static class MotionCaptureEndpoints
{
    public static void MapMotionCaptureEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/motion-capture/terms", async (IQueryHandler<GetMotionCaptureTermsQuery, MotionCaptureTermsResponse> handler, CancellationToken ct) =>
                (await handler.Handle(new GetMotionCaptureTermsQuery(), ct)).ToHttp())
            .WithTags("3D exercise videos");

        var group = routes.MapGroup("/exercises/{exerciseId:guid}/motion-captures").WithTags("3D exercise videos");

        group.MapGet("/", async (Guid exerciseId, IQueryHandler<ListMotionCapturesQuery, IReadOnlyList<MotionCaptureJobResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMotionCapturesQuery(exerciseId), ct)).ToHttp());

        // The raw video is the body (Content-Type video/mp4 or video/quicktime); the rest travels in the query string.
        group.MapPost("/", async (
                Guid exerciseId, string fileName, int durationSeconds, int consentVersion, HttpRequest request, MotionCaptureSettings settings,
                ICommandHandler<StartMotionCaptureCommand, MotionCaptureJobResponse> handler, CancellationToken ct) =>
            {
                if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                {
                    limit.MaxRequestBodySize = settings.MaxUploadBytes;
                }

                var size = request.ContentLength ?? 0;
                var command = new StartMotionCaptureCommand(
                    exerciseId, fileName, request.ContentType ?? string.Empty, size, durationSeconds, consentVersion, request.Body);
                return (await handler.Handle(command, ct)).ToHttp(job => TypedResults.Created($"/api/v1/exercises/{exerciseId}/motion-captures", job));
            })
            .Accepts<Stream>("video/mp4", "video/quicktime", "video/webm")
            .RequireRateLimiting(RateLimitPolicies.Assistant);

        routes.MapPost("/motion-captures/{jobId:guid}/accept", async (
                Guid jobId, ICommandHandler<AcceptMotionCaptureCommand, MotionCaptureJobResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new AcceptMotionCaptureCommand(jobId), ct)).ToHttp())
            .WithTags("3D exercise videos");
    }
}
