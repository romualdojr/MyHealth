using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.HealthData;
using MyHealth.Application.Workouts;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Api.IntegrationTests;

/// <summary>
/// End-to-end iPhone flows (docs/initial_requirements/ios/README.md): acceptance test 12, HK-06/HK-08,
/// per-client dedup, the authorized period, self-reported provenance and revocation stopping sharing.
/// </summary>
public sealed class IphoneIntegrationTests(ApiFactory factory) : IntegrationTest(factory)
{
    private sealed record Setup(TestUser Client, TestUser Trainer, Guid RelationshipId, Guid PlanId, Guid SessionId, DateTimeOffset StartedAt);

    private async Task<Setup> ConnectedWorkoutAsync()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var relationship = await Scenario.LinkAsync(trainer, client);
        var planId = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, planId);
        await Scenario.EnsureSuccess(await client.Http.PostAsync("/api/v1/me/health-connections/apple-health/connect", null, Ct));
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var (sessionId, _) = await Scenario.StartAsync(client, planId, startedAt);
        return new Setup(client, trainer, relationship, planId, sessionId, startedAt);
    }

    private static object Report(Guid workoutId, DateTimeOffset startedAt, DateTimeOffset? endedAt, params object[] points) =>
        new { workoutID = workoutId, startedAt, endedAt, points };

    private static object Point(Guid id, DateTimeOffset at, double bpm = 120, bool wasUserEntered = false) =>
        new { healthKitID = id, timestamp = at, beatsPerMinute = bpm, sourceName = "Apple Watch", deviceName = "Watch", wasUserEntered };

    private static Task<HttpResponseMessage> Upload(Setup s, Guid sessionId, object report) =>
        s.Client.Http.PutAsJsonAsync($"/api/v1/workout-sessions/{sessionId}/heart-rate", report, Ct);

    [Fact]
    public async Task Denied_write_means_no_health_workout_and_with_write_exactly_one_is_recorded()
    {
        var s = await ConnectedWorkoutAsync();
        await Scenario.CompleteAsync(s.Client, s.SessionId);
        var url = $"/api/v1/workout-sessions/{s.SessionId}/apple-health-workout";
        var healthKitWorkout = Guid.NewGuid();

        // Acceptance 12a: the client never opted in — completion already succeeded, nothing may be recorded.
        (await s.Client.Http.PutAsJsonAsync(url, new { healthKitWorkoutId = healthKitWorkout, savedAt = DateTimeOffset.UtcNow }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Acceptance 12b: opt in; one workout is recorded, the retry is idempotent, a second workout is a duplicate.
        await Scenario.EnsureSuccess(await s.Client.Http.PutAsJsonAsync("/api/v1/me/health-connections/apple-health/workout-saving", new { enabled = true }, Ct));
        (await s.Client.Http.PutAsJsonAsync(url, new { healthKitWorkoutId = healthKitWorkout, savedAt = DateTimeOffset.UtcNow }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await s.Client.Http.PutAsJsonAsync(url, new { healthKitWorkoutId = healthKitWorkout, savedAt = DateTimeOffset.UtcNow }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await s.Client.Http.PutAsJsonAsync(url, new { healthKitWorkoutId = Guid.NewGuid(), savedAt = DateTimeOffset.UtcNow }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var session = await Scenario.Read<WorkoutSessionResponse>(await s.Client.Http.GetAsync($"/api/v1/workout-sessions/{s.SessionId}", Ct));
        session.AppleHealthWorkout!.ExternalWorkoutId.ShouldBe(healthKitWorkout);
    }

    [Fact]
    public async Task Workout_saving_can_be_enabled_without_heart_rate_import()
    {
        var client = await Scenario.ClientAsync();

        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync("/api/v1/me/health-connections/apple-health/workout-saving", new { enabled = true }, Ct));

        var connection = await Scenario.Read<HealthConnectionResponse>(await client.Http.GetAsync("/api/v1/me/health-connections/apple-health", Ct));
        connection.Status.ShouldBe(HealthConnectionStatus.NotConnected);
        connection.SaveWorkoutsToAppleHealth.ShouldBeTrue();
    }

    [Fact]
    public async Task Reconciliation_lists_finished_sessions_only_while_import_is_connected()
    {
        var s = await ConnectedWorkoutAsync();
        var url = "/api/v1/me/health-connections/apple-health/reconciliation";

        (await Scenario.Read<ReconciliationCandidatesResponse>(await s.Client.Http.GetAsync(url, Ct))).Sessions.ShouldBeEmpty();

        await Scenario.CompleteAsync(s.Client, s.SessionId);
        var candidates = await Scenario.Read<ReconciliationCandidatesResponse>(await s.Client.Http.GetAsync(url, Ct));
        var candidate = candidates.Sessions.ShouldHaveSingleItem();
        candidate.SessionId.ShouldBe(s.SessionId);
        candidate.HeartRateFinalized.ShouldBeFalse(); // the final upload never arrived: the phone must retry it
        candidate.ReconcileUntil.ShouldBe(candidate.EndedAt + TimeSpan.FromDays(30));

        await Scenario.EnsureSuccess(await s.Client.Http.PostAsync("/api/v1/me/health-connections/apple-health/pause", null, Ct));
        var paused = await Scenario.Read<ReconciliationCandidatesResponse>(await s.Client.Http.GetAsync(url, Ct));
        paused.ImportAllowed.ShouldBeFalse();
        paused.Sessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sample_is_stored_once_per_client_even_across_sessions()
    {
        var s = await ConnectedWorkoutAsync();
        var shared = Guid.NewGuid();
        await Scenario.EnsureSuccess(await Upload(s, s.SessionId, Report(s.SessionId, s.StartedAt, null, Point(shared, s.StartedAt.AddMinutes(25)))));
        await Scenario.CompleteAsync(s.Client, s.SessionId, DateTimeOffset.UtcNow.AddMinutes(-2));

        // A second, overlapping session started by mistake on the same plan receives the same HealthKit sample.
        var (second, _) = await Scenario.StartAsync(s.Client, s.PlanId, s.StartedAt.AddMinutes(20));
        var outcome = await Scenario.Read<HeartRateSyncResponse>(
            await Upload(s, second, Report(second, s.StartedAt.AddMinutes(20), null, Point(shared, s.StartedAt.AddMinutes(25)))));

        outcome.ClaimedByOtherSession.ShouldBe(1);
        outcome.Accepted.ShouldBe(0);
    }

    [Fact]
    public async Task Samples_recorded_while_paused_are_rejected_even_when_uploaded_later()
    {
        var s = await ConnectedWorkoutAsync();
        await Scenario.EnsureSuccess(await s.Client.Http.PostAsync("/api/v1/me/health-connections/apple-health/pause", null, Ct));
        var duringPause = DateTimeOffset.UtcNow;
        await Task.Delay(50, Ct);
        await Scenario.EnsureSuccess(await s.Client.Http.PostAsync("/api/v1/me/health-connections/apple-health/connect", null, Ct));

        var outcome = await Scenario.Read<HeartRateSyncResponse>(await Upload(s, s.SessionId, Report(
            s.SessionId, s.StartedAt, null, Point(Guid.NewGuid(), s.StartedAt.AddMinutes(1)), Point(Guid.NewGuid(), duringPause))));

        outcome.Accepted.ShouldBe(1);
        outcome.Rejected.ShouldBe(1);
        var connection = await Scenario.Read<HealthConnectionResponse>(await s.Client.Http.GetAsync("/api/v1/me/health-connections/apple-health", Ct));
        connection.ExcludedPeriods.ShouldHaveSingleItem().To.ShouldNotBeNull();
    }

    [Fact]
    public async Task User_entered_samples_are_shown_as_self_reported_and_both_means_are_returned()
    {
        var s = await ConnectedWorkoutAsync();
        var t = s.StartedAt.AddMinutes(1);

        var outcome = await Scenario.Read<HeartRateSyncResponse>(await Upload(s, s.SessionId, Report(
            s.SessionId, s.StartedAt, null,
            Point(Guid.NewGuid(), t, 100), Point(Guid.NewGuid(), t.AddSeconds(10), 100), Point(Guid.NewGuid(), t.AddSeconds(60), 160, wasUserEntered: true))));

        outcome.Summary!.AverageBeatsPerMinute.ShouldBe(120);
        outcome.Summary.TimeWeightedAverageBeatsPerMinute.ShouldBe(125);
        outcome.Summary.SelfReportedSampleCount.ShouldBe(1);

        var view = await Scenario.Read<WorkoutHeartRateResponse>(await s.Client.Http.GetAsync($"/api/v1/workout-sessions/{s.SessionId}/heart-rate", Ct));
        view.DataKind.ShouldBe("mixed");
        view.Points.Select(p => p.Kind).ShouldBe(["measured", "measured", "self-reported"]);
    }

    [Fact]
    public async Task Revoking_apple_health_stops_sharing_even_with_an_active_grant()
    {
        var s = await ConnectedWorkoutAsync();
        await Scenario.EnsureSuccess(await Upload(s, s.SessionId, Report(s.SessionId, s.StartedAt, null, Point(Guid.NewGuid(), s.StartedAt.AddMinutes(1)))));
        await Scenario.EnsureSuccess(await s.Client.Http.PutAsync($"/api/v1/care-relationships/{s.RelationshipId}/sharing/HeartRate", null, Ct));
        var sharedUrl = $"/api/v1/care-relationships/{s.RelationshipId}/heart-rate/sessions/{s.SessionId}";
        (await s.Trainer.Http.GetAsync(sharedUrl, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await Scenario.EnsureSuccess(await s.Client.Http.PostAsync("/api/v1/me/health-connections/apple-health/revoke", null, Ct));

        var denied = await s.Trainer.Http.GetAsync(sharedUrl, Ct);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync(Ct)).ShouldContain("HealthData.SharingStopped");
        (await s.Trainer.Http.GetAsync($"/api/v1/care-relationships/{s.RelationshipId}/heart-rate/sessions", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The client keeps access to their own data.
        (await s.Client.Http.GetAsync($"/api/v1/workout-sessions/{s.SessionId}/heart-rate", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var connection = await Scenario.Read<HealthConnectionResponse>(await s.Client.Http.GetAsync("/api/v1/me/health-connections/apple-health", Ct));
        connection.Recipients.ShouldBeEmpty();
    }

    [Fact]
    public async Task Final_report_sent_before_api_completion_is_accepted_and_completion_still_works()
    {
        // The README order is finish → complete → upload, but the phone may upload first when completion is retried.
        var s = await ConnectedWorkoutAsync();
        var endedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        var final = await Scenario.Read<HeartRateSyncResponse>(await Upload(s, s.SessionId,
            Report(s.SessionId, s.StartedAt, endedAt, Point(Guid.NewGuid(), s.StartedAt.AddMinutes(5)))));
        await Scenario.CompleteAsync(s.Client, s.SessionId, endedAt);

        final.Kind.ShouldBe(HeartRateSyncKind.Final);
        var session = await Scenario.Read<WorkoutSessionResponse>(await s.Client.Http.GetAsync($"/api/v1/workout-sessions/{s.SessionId}", Ct));
        session.Status.ShouldBe(WorkoutStatus.Completed);
    }
}
