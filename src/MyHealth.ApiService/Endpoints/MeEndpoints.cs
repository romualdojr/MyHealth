using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.HealthData;
using MyHealth.Application.Notifications;
using MyHealth.Application.TrainingPlans;
using MyHealth.Application.Users;
using MyHealth.Application.Workouts;

namespace MyHealth.ApiService.Endpoints;

/// <summary>Everything the signed-in user sees about themselves as a client.</summary>
internal static class MeEndpoints
{
    public sealed record WorkoutSavingRequest(bool Enabled);

    public static void MapMeEndpoints(this IEndpointRouteBuilder routes)
    {
        var me = routes.MapGroup("/me").WithTags("Me");

        me.MapGet("/", async (IQueryHandler<GetMeQuery, MeResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetMeQuery(), ct)).ToHttp());

        me.MapPost("/profile", async (CompleteProfileCommand command, ICommandHandler<CompleteProfileCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp(id => TypedResults.Created("/api/v1/me", new { id })));

        me.MapPut("/profile", async (UpdateProfileCommand command, ICommandHandler<UpdateProfileCommand> handler, CancellationToken ct) =>
            (await handler.Handle(command, ct)).ToHttp());

        me.MapGet("/home", async (IQueryHandler<GetClientHomeQuery, ClientHomeResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetClientHomeQuery(), ct)).ToHttp());

        me.MapGet("/training-plans", async (IQueryHandler<ListMyPlansQuery, IReadOnlyList<ClientPlanListItem>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyPlansQuery(), ct)).ToHttp());

        me.MapGet("/training-plans/{planId:guid}", async (Guid planId, IQueryHandler<GetMyPlanQuery, ClientPlanResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetMyPlanQuery(planId), ct)).ToHttp());

        me.MapGet("/workout-sessions", async (
                DateTimeOffset? from, DateTimeOffset? to,
                IQueryHandler<ListMyWorkoutSessionsQuery, IReadOnlyList<WorkoutSessionListItem>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyWorkoutSessionsQuery(from, to), ct)).ToHttp());

        me.MapGet("/attendance", async (DateOnly from, DateOnly to, IQueryHandler<GetAttendanceQuery, WeeklyAttendance> handler, CancellationToken ct) =>
            (await handler.Handle(new GetAttendanceQuery(from, to), ct)).ToHttp());

        me.MapGet("/progress/exercises/{exerciseId:guid}", async (
                Guid exerciseId, DateOnly? from, DateOnly? to,
                IQueryHandler<GetExerciseProgressQuery, ExerciseProgressResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetExerciseProgressQuery(exerciseId, from, to), ct)).ToHttp());

        me.MapGet("/notifications", async (bool? unreadOnly, IQueryHandler<ListMyNotificationsQuery, IReadOnlyList<NotificationResponse>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListMyNotificationsQuery(unreadOnly ?? false), ct)).ToHttp());

        me.MapPost("/notifications/{notificationId:guid}/read", async (
                Guid notificationId, ICommandHandler<MarkNotificationReadCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new MarkNotificationReadCommand(notificationId), ct)).ToHttp());

        var appleHealth = me.MapGroup("/health-connections/apple-health").WithTags("Apple Health");

        appleHealth.MapGet("/", async (IQueryHandler<GetHealthConnectionQuery, HealthConnectionResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new GetHealthConnectionQuery(), ct)).ToHttp());

        appleHealth.MapPut("/workout-saving", async (WorkoutSavingRequest request, ICommandHandler<SetAppleHealthWorkoutSavingCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new SetAppleHealthWorkoutSavingCommand(request.Enabled), ct)).ToHttp());

        appleHealth.MapGet("/reconciliation", async (
                IQueryHandler<ListReconciliationCandidatesQuery, ReconciliationCandidatesResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new ListReconciliationCandidatesQuery(), ct)).ToHttp());

        foreach (var action in Enum.GetValues<HealthConnectionAction>())
        {
            appleHealth.MapPost($"/{action.ToString().ToLowerInvariant()}", async (ICommandHandler<ChangeHealthConnectionCommand> handler, CancellationToken ct) =>
                (await handler.Handle(new ChangeHealthConnectionCommand(action), ct)).ToHttp());
        }
    }
}
