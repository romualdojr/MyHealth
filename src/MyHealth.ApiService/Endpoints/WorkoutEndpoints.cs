using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.HealthData;
using MyHealth.Application.Workouts;

namespace MyHealth.ApiService.Endpoints;

/// <summary>Workout execution (RF-18/19). Every write is idempotent on client-generated ids so the phone can retry.</summary>
internal static class WorkoutEndpoints
{
    public sealed record TimestampRequest(DateTimeOffset At);

    public sealed record LogSetRequest(
        Guid ExerciseId, Guid? PlannedExerciseId, int SetNumber, int? Repetitions, decimal? LoadKg, int? DurationSeconds,
        decimal? DistanceMeters, int? Difficulty, string? Notes, DateTimeOffset LoggedAt);

    public sealed record CompleteRequest(DateTimeOffset CompletedAt, int PerceivedExertion, bool ReportedDiscomfort, string? DiscomfortDetails, string? Comments);

    public sealed record ReplyRequest(string Message);

    /// <summary>Wire format of the iOS <c>WorkoutHeartRateReport</c> (property matching is case-insensitive).</summary>
    public sealed record HeartRateReportRequest(
        Guid WorkoutId,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        double? AverageBeatsPerMinute,
        double? MaximumBeatsPerMinute,
        IReadOnlyList<HeartRatePointRequest> Points);

    public sealed record HeartRatePointRequest(
        Guid HealthKitId, DateTimeOffset Timestamp, double BeatsPerMinute, string SourceName, string? DeviceName, bool? WasUserEntered);

    public sealed record AppleHealthWorkoutRequest(Guid HealthKitWorkoutId, DateTimeOffset SavedAt);

    public static void MapWorkoutEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/workout-sessions").WithTags("Workout sessions");

        group.MapPost("/", async (StartWorkoutCommand command, ICommandHandler<StartWorkoutCommand, StartWorkoutResponse> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp(response => response.Created
                ? TypedResults.Created($"/api/v1/workout-sessions/{response.SessionId}", response)
                : (IResult)TypedResults.Ok(response)));

        group.MapGet("/{sessionId:guid}", async (Guid sessionId, IQueryHandler<GetWorkoutSessionQuery, WorkoutSessionResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetWorkoutSessionQuery(sessionId), ct)).ToHttp());

        foreach (var transition in Enum.GetValues<WorkoutTransition>())
        {
            group.MapPost($"/{{sessionId:guid}}/{transition.ToString().ToLowerInvariant()}", async (
                    Guid sessionId, TimestampRequest request, ICommandHandler<ChangeWorkoutStateCommand> handler, CancellationToken ct) =>
                (await handler.Handle(new ChangeWorkoutStateCommand(sessionId, transition, request.At), ct)).ToHttp());
        }

        group.MapPut("/{sessionId:guid}/sets/{setLogId:guid}", async (
                Guid sessionId, Guid setLogId, LogSetRequest request, ICommandHandler<LogSetCommand> handler, CancellationToken ct) =>
            (await handler.Handle(
                new LogSetCommand(
                    sessionId, setLogId, request.ExerciseId, request.PlannedExerciseId, request.SetNumber, request.Repetitions, request.LoadKg,
                    request.DurationSeconds, request.DistanceMeters, request.Difficulty, request.Notes, request.LoggedAt),
                ct)).ToHttp());

        group.MapPost("/{sessionId:guid}/complete", async (
                Guid sessionId, CompleteRequest request, ICommandHandler<CompleteWorkoutCommand> handler, CancellationToken ct) =>
            (await handler.Handle(
                new CompleteWorkoutCommand(sessionId, request.CompletedAt, request.PerceivedExertion, request.ReportedDiscomfort, request.DiscomfortDetails, request.Comments),
                ct)).ToHttp());

        group.MapPost("/{sessionId:guid}/feedback-reply", async (
                Guid sessionId, ReplyRequest request, ICommandHandler<ReplyToFeedbackCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ReplyToFeedbackCommand(sessionId, request.Message), ct)).ToHttp());

        // HealthKit contract (docs/initial_requirements/ios/README.md): idempotent PUT of the full current snapshot.
        group.MapPut("/{sessionId:guid}/heart-rate", async Task<IResult> (
                Guid sessionId, HeartRateReportRequest request, ICommandHandler<SyncWorkoutHeartRateCommand, HeartRateSyncResponse> handler, CancellationToken ct) =>
            {
                if (request.WorkoutId != Guid.Empty && request.WorkoutId != sessionId)
                {
                    return TypedResults.Problem("The body workoutID does not match the URL.", statusCode: StatusCodes.Status400BadRequest, title: "HeartRate.IdMismatch");
                }

                var command = new SyncWorkoutHeartRateCommand(
                    sessionId, request.StartedAt, request.EndedAt,
                    (request.Points ?? [])
                        .Select(p => new HeartRatePointInput(p.HealthKitId, p.Timestamp, p.BeatsPerMinute, p.SourceName, p.DeviceName, p.WasUserEntered ?? false))
                        .ToList());
                return (await handler.Handle(command, ct)).ToHttp();
            });

        // HK-06: the phone reports the HKWorkout it saved; a second, different id is rejected as a duplicate.
        group.MapPut("/{sessionId:guid}/apple-health-workout", async (
                Guid sessionId, AppleHealthWorkoutRequest request, ICommandHandler<RecordAppleHealthWorkoutCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new RecordAppleHealthWorkoutCommand(sessionId, request.HealthKitWorkoutId, request.SavedAt), ct)).ToHttp());

        group.MapGet("/{sessionId:guid}/heart-rate", async (Guid sessionId, IQueryHandler<GetWorkoutHeartRateQuery, WorkoutHeartRateResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetWorkoutHeartRateQuery(sessionId), ct)).ToHttp());
    }
}
