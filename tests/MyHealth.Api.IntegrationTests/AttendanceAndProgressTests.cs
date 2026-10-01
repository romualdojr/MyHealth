using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Users;
using MyHealth.Application.Workouts;
using MyHealth.Domain.Notifications;
using MyHealth.Application.Notifications;

namespace MyHealth.Api.IntegrationTests;

/// <summary>Acceptance test 3 and RF-19/20/21: attendance counts completed sessions only, on the local date.</summary>
public sealed class AttendanceAndProgressTests(ApiFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Only_completed_sessions_count_on_the_clients_local_date()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        var planId = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, planId);

        // Opening the plan and starting/abandoning a session does not count.
        await Scenario.MyPlanAsync(client, planId);
        var (abandoned, _) = await Scenario.StartAsync(client, planId);
        await Scenario.EnsureSuccess(await client.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{abandoned}/abandon", new { at = DateTimeOffset.UtcNow.AddMinutes(-30) }, Ct));

        // 02:00 UTC is 23:00 of the previous day in São Paulo.
        var lateEvening = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1).AddHours(2), TimeSpan.Zero);
        var (completed, _) = await Scenario.StartAsync(client, planId, lateEvening.AddMinutes(-50));
        await Scenario.CompleteAsync(client, completed, lateEvening);

        var expectedDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-2));
        var attendance = await Scenario.Read<WeeklyAttendance>(
            await client.Http.GetAsync($"/api/v1/me/attendance?from={expectedDate.AddDays(-1):yyyy-MM-dd}&to={expectedDate.AddDays(1):yyyy-MM-dd}", Ct));

        attendance.CompletedSessions.ShouldBe(1);
        attendance.ActiveDays.ShouldBe(1);
        attendance.Days.Single(d => d.CompletedSessions == 1).Date.ShouldBe(expectedDate);
        attendance.Days.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Home_shows_professional_next_session_and_weekly_attendance()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        var planId = await Scenario.DraftPlanAsync(trainer, client.Id, "Plano", "Treino A", "Treino B");
        await Scenario.PublishAsync(trainer, planId);
        var (session, _) = await Scenario.StartAsync(client, planId);
        await Scenario.CompleteAsync(client, session);

        var home = await Scenario.Read<ClientHomeResponse>(await client.Http.GetAsync("/api/v1/me/home", Ct));

        home.Professionals.ShouldHaveSingleItem();
        home.ActivePlans.ShouldHaveSingleItem().NextSessionName.ShouldBe("Treino B");
        home.Attendance.Days.Count.ShouldBe(7);
        home.Attendance.CompletedSessions.ShouldBe(1);
    }

    [Fact]
    public async Task Set_logs_are_idempotent_and_feed_exercise_progress()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        var planId = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, planId);
        var (session, planned) = await Scenario.StartAsync(client, planId);
        var exercise = planned.Session.Exercises[0];

        var setId = Guid.CreateVersion7();
        var set = new { exerciseId = exercise.ExerciseId, plannedExerciseId = exercise.Id, setNumber = 1, repetitions = 10, loadKg = 50m, loggedAt = DateTimeOffset.UtcNow.AddMinutes(-20) };
        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync($"/api/v1/workout-sessions/{session}/sets/{setId}", set, Ct));
        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync($"/api/v1/workout-sessions/{session}/sets/{setId}", set, Ct));
        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync(
            $"/api/v1/workout-sessions/{session}/sets/{Guid.CreateVersion7()}", set with { setNumber = 2, loadKg = 55m, repetitions = 8 }, Ct));
        await Scenario.CompleteAsync(client, session);

        var progress = await Scenario.Read<ExerciseProgressResponse>(await client.Http.GetAsync($"/api/v1/me/progress/exercises/{exercise.ExerciseId}", Ct));

        var point = progress.Points.ShouldHaveSingleItem();
        point.Sets.ShouldBe(2);
        point.MaxLoadKg.ShouldBe(55m);
        point.TotalRepetitions.ShouldBe(18);
        point.TotalVolumeKg.ShouldBe(940m);
        point.TotalDurationSeconds.ShouldBeNull();
        point.Source.ShouldBe("self-reported");
        progress.HeaviestSet!.LoadKg.ShouldBe(55m);

        // After completion a set is corrected with a reason; progress follows and the history is kept (A6).
        var correction = new { repetitions = 10, loadKg = 60m, reason = "Carga anotada errada" };
        (await client.Http.PutAsJsonAsync($"/api/v1/workout-sessions/{session}/sets/{setId}", set with { loadKg = 60m }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await Scenario.EnsureSuccess(await client.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{session}/sets/{setId}/corrections", correction, Ct));
        (await trainer.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{session}/sets/{setId}/corrections", correction, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var corrected = await Scenario.Read<ExerciseProgressResponse>(await client.Http.GetAsync($"/api/v1/me/progress/exercises/{exercise.ExerciseId}", Ct));
        corrected.HeaviestSet!.LoadKg.ShouldBe(60m);
        var detail = await Scenario.Read<WorkoutSessionResponse>(await client.Http.GetAsync($"/api/v1/workout-sessions/{session}", Ct));
        var history = detail.Sets.Single(s => s.Id == setId).Corrections.ShouldHaveSingleItem();
        (history.Previous.LoadKg, history.Reason).ShouldBe((50m, "Carga anotada errada"));
    }

    [Fact]
    public async Task Discomfort_reaches_the_trainer_who_can_reply()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        var planId = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, planId);
        var (session, _) = await Scenario.StartAsync(client, planId);
        await Scenario.CompleteAsync(client, session, discomfort: true);

        var dashboard = await Scenario.Read<TrainerDashboardResponse>(await trainer.Http.GetAsync("/api/v1/trainer/dashboard", Ct));
        var summary = dashboard.Clients.ShouldHaveSingleItem();
        summary.UnrepliedDiscomfortReports.ShouldBe(1);
        summary.CompletedLast7Days.ShouldBe(1);
        summary.AdherenceLast7Days.ShouldBe(0.33);

        var trainerNotifications = await Scenario.Read<List<NotificationResponse>>(await trainer.Http.GetAsync("/api/v1/me/notifications", Ct));
        trainerNotifications.ShouldContain(n => n.Type == NotificationType.DiscomfortReported && n.ResourceId == session);

        await Scenario.EnsureSuccess(await trainer.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{session}/feedback-reply", new { message = "Vamos ajustar a carga." }, Ct));

        var after = await Scenario.Read<TrainerDashboardResponse>(await trainer.Http.GetAsync("/api/v1/trainer/dashboard", Ct));
        after.TotalDiscomfortReports.ShouldBe(0);
        var clientNotifications = await Scenario.Read<List<NotificationResponse>>(await client.Http.GetAsync("/api/v1/me/notifications", Ct));
        clientNotifications.ShouldContain(n => n.Type == NotificationType.FeedbackReplied);
    }

    [Fact]
    public async Task Starting_twice_with_the_same_id_is_idempotent()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        var planId = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, planId);
        var plan = await Scenario.MyPlanAsync(client, planId);
        var body = new { sessionId = Guid.CreateVersion7(), planId, plannedSessionId = plan.Sessions[0].Session.Id, startedAt = DateTimeOffset.UtcNow };

        (await client.Http.PostAsJsonAsync("/api/v1/workout-sessions", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await client.Http.PostAsJsonAsync("/api/v1/workout-sessions", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Scenario.Read<List<WorkoutSessionListItem>>(await client.Http.GetAsync("/api/v1/me/workout-sessions", Ct))).ShouldHaveSingleItem();
    }
}
