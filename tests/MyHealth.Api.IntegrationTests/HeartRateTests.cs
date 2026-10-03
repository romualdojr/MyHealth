using System.Text;
using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Auditing;
using MyHealth.Application.Common;
using MyHealth.Application.HealthData;
using MyHealth.Domain.HealthData;

namespace MyHealth.Api.IntegrationTests;

/// <summary>HealthKit server contract and acceptance tests 4, 5, 11 and 13.</summary>
public sealed class HeartRateTests(ApiFactory factory) : IntegrationTest(factory)
{
    private sealed record Setup(TestUser Client, TestUser Trainer, Guid RelationshipId, Guid SessionId, DateTimeOffset StartedAt);

    private async Task<Setup> ActiveWorkoutAsync(bool connectAppleHealth = true)
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var relationship = await Scenario.LinkAsync(trainer, client);
        var planId = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, planId);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var (sessionId, _) = await Scenario.StartAsync(client, planId, startedAt);
        if (connectAppleHealth)
        {
            await Scenario.EnsureSuccess(await client.Http.PostAsync("/api/v1/me/health-connections/apple-health/connect", null, Ct));
        }

        return new Setup(client, trainer, relationship, sessionId, startedAt);
    }

    /// <summary>Exactly what the iOS module sends: ISO-8601 dates and its own property casing (workoutID, healthKitID).</summary>
    private static StringContent IosReport(Guid workoutId, DateTimeOffset startedAt, DateTimeOffset? endedAt, params (Guid Id, DateTimeOffset At, double Bpm, string Source)[] points)
    {
        var json = new StringBuilder();
        json.Append($$"""{"workoutID":"{{workoutId.ToString().ToUpperInvariant()}}","startedAt":"{{startedAt:yyyy-MM-ddTHH:mm:ss.fffZ}}",""");
        json.Append(endedAt is null ? "\"endedAt\":null," : $"\"endedAt\":\"{endedAt:yyyy-MM-ddTHH:mm:ss.fffZ}\",");
        json.Append("\"averageBeatsPerMinute\":999,\"maximumBeatsPerMinute\":999,\"points\":[");
        json.Append(string.Join(",", points.Select(p =>
            $$"""{"healthKitID":"{{p.Id.ToString().ToUpperInvariant()}}","timestamp":"{{p.At.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}}","beatsPerMinute":{{p.Bpm.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"sourceName":"{{p.Source}}","deviceName":null}""")));
        json.Append("]}");
        return new StringContent(json.ToString(), Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task Repeated_sync_imports_each_sample_once_and_summary_uses_accepted_points()
    {
        var s = await ActiveWorkoutAsync();
        var a = (Guid.NewGuid(), s.StartedAt.AddMinutes(1), 100.0, "Apple Watch");
        var b = (Guid.NewGuid(), s.StartedAt.AddMinutes(2), 140.0, "Polar H10");
        var url = $"/api/v1/workout-sessions/{s.SessionId}/heart-rate";

        var first = await Scenario.Read<HeartRateSyncResponse>(await s.Client.Http.PutAsync(url, IosReport(s.SessionId, s.StartedAt, null, a, b), Ct));
        var retry = await Scenario.Read<HeartRateSyncResponse>(await s.Client.Http.PutAsync(url, IosReport(s.SessionId, s.StartedAt, null, a, b), Ct));

        first.Accepted.ShouldBe(2);
        retry.Accepted.ShouldBe(0);
        retry.Duplicates.ShouldBe(2);
        retry.Summary!.AverageBeatsPerMinute.ShouldBe(120); // the phone's 999 values are ignored
        retry.Summary.MaximumBeatsPerMinute.ShouldBe(140);

        var view = await Scenario.Read<WorkoutHeartRateResponse>(await s.Client.Http.GetAsync(url, Ct));
        view.Points.Count.ShouldBe(2);
        view.Sources.ShouldBe(["Apple Watch", "Polar H10"]);
        view.DataKind.ShouldBe("measured");
        view.LastSyncedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Completion_is_not_reverted_by_a_late_in_progress_upload_and_reconciliation_adds_late_samples()
    {
        var s = await ActiveWorkoutAsync();
        var url = $"/api/v1/workout-sessions/{s.SessionId}/heart-rate";
        var early = (Guid.NewGuid(), s.StartedAt.AddMinutes(1), 110.0, "Apple Watch");
        var deleted = (Guid.NewGuid(), s.StartedAt.AddMinutes(2), 180.0, "Apple Watch");
        var late = (Guid.NewGuid(), s.StartedAt.AddMinutes(20), 150.0, "Apple Watch");
        var endedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        await Scenario.EnsureSuccess(await s.Client.Http.PutAsync(url, IosReport(s.SessionId, s.StartedAt, null, early, deleted), Ct));
        await Scenario.CompleteAsync(s.Client, s.SessionId, endedAt);
        var final = await Scenario.Read<HeartRateSyncResponse>(await s.Client.Http.PutAsync(url, IosReport(s.SessionId, s.StartedAt, endedAt, early), Ct));
        final.Kind.ShouldBe(HeartRateSyncKind.Final);
        final.Removed.ShouldBe(1);

        // Network retry of the earlier in-progress snapshot arrives after completion.
        var stale = await Scenario.Read<HeartRateSyncResponse>(await s.Client.Http.PutAsync(url, IosReport(s.SessionId, s.StartedAt, null, early, deleted), Ct));
        stale.Kind.ShouldBe(HeartRateSyncKind.IgnoredStale);
        stale.IsFinalized.ShouldBeTrue();

        // A sample delivered late by the source is reconciled.
        var reconciled = await Scenario.Read<HeartRateSyncResponse>(await s.Client.Http.PutAsync(url, IosReport(s.SessionId, s.StartedAt, endedAt, early, late), Ct));
        reconciled.Kind.ShouldBe(HeartRateSyncKind.Reconciliation);
        reconciled.Summary!.SampleCount.ShouldBe(2);

        var view = await Scenario.Read<WorkoutHeartRateResponse>(await s.Client.Http.GetAsync(url, Ct));
        view.IsFinalized.ShouldBeTrue();
        view.LastRevisedAt.ShouldNotBeNull();
        view.Gaps.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Body_and_url_ids_must_match()
    {
        var s = await ActiveWorkoutAsync();

        var response = await s.Client.Http.PutAsync($"/api/v1/workout-sessions/{s.SessionId}/heart-rate", IosReport(Guid.NewGuid(), s.StartedAt, null), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Professionals_see_heart_rate_only_while_the_client_shares_it_and_reads_are_audited()
    {
        var s = await ActiveWorkoutAsync();
        await Scenario.EnsureSuccess(await s.Client.Http.PutAsync(
            $"/api/v1/workout-sessions/{s.SessionId}/heart-rate", IosReport(s.SessionId, s.StartedAt, null, (Guid.NewGuid(), s.StartedAt.AddMinutes(1), 120, "Apple Watch")), Ct));
        var sharedUrl = $"/api/v1/care-relationships/{s.RelationshipId}/heart-rate/sessions/{s.SessionId}";

        (await s.Trainer.Http.GetAsync(sharedUrl, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await Scenario.EnsureSuccess(await s.Client.Http.PutAsync($"/api/v1/care-relationships/{s.RelationshipId}/sharing/HeartRate", null, Ct));
        var shared = await Scenario.Read<WorkoutHeartRateResponse>(await s.Trainer.Http.GetAsync(sharedUrl, Ct));
        shared.Points.ShouldHaveSingleItem();

        var connection = await Scenario.Read<HealthConnectionResponse>(await s.Client.Http.GetAsync("/api/v1/me/health-connections/apple-health", Ct));
        connection.Recipients.ShouldHaveSingleItem().RelationshipId.ShouldBe(s.RelationshipId);

        await Scenario.EnsureSuccess(await s.Client.Http.DeleteAsync($"/api/v1/care-relationships/{s.RelationshipId}/sharing/HeartRate", Ct));
        (await s.Trainer.Http.GetAsync(sharedUrl, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var admin = Scenario.Factory.NewUser("auditor", administrator: true);
        var audit = await Scenario.Read<PagedResponse<AuditEntryResponse>>(await admin.Http.GetAsync($"/api/v1/admin/audit-entries?subjectUserId={s.Client.Id}", Ct));
        audit.Items.ShouldContain(e => e.Action == "read" && e.ActorUserId == s.Trainer.Id && e.ResourceId == s.SessionId);
        audit.Items.ShouldContain(e => e.Action == "grant" && e.ResourceType == "DataSharing.HeartRate");
        audit.Items.ShouldContain(e => e.Action == "revoke" && e.ResourceType == "DataSharing.HeartRate");
    }

    [Fact]
    public async Task Revoked_apple_health_stops_imports_but_workouts_keep_working()
    {
        var s = await ActiveWorkoutAsync();
        await Scenario.EnsureSuccess(await s.Client.Http.PostAsync("/api/v1/me/health-connections/apple-health/revoke", null, Ct));

        var upload = await s.Client.Http.PutAsync(
            $"/api/v1/workout-sessions/{s.SessionId}/heart-rate", IosReport(s.SessionId, s.StartedAt, null, (Guid.NewGuid(), s.StartedAt.AddMinutes(1), 120, "Apple Watch")), Ct);
        upload.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await Scenario.CompleteAsync(s.Client, s.SessionId);
        (await s.Client.Http.GetAsync($"/api/v1/workout-sessions/{s.SessionId}/heart-rate", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Workouts_work_without_ever_connecting_apple_health()
    {
        var s = await ActiveWorkoutAsync(connectAppleHealth: false);

        await Scenario.CompleteAsync(s.Client, s.SessionId);

        var connection = await Scenario.Read<HealthConnectionResponse>(await s.Client.Http.GetAsync("/api/v1/me/health-connections/apple-health", Ct));
        connection.Status.ShouldBe(HealthConnectionStatus.NotConnected);
    }
}
