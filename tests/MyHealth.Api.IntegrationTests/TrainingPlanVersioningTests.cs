using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Notifications;
using MyHealth.Application.TrainingPlans;
using MyHealth.Application.Workouts;
using MyHealth.Domain.Notifications;

namespace MyHealth.Api.IntegrationTests;

/// <summary>Acceptance test 2 and RF-13: executions stay linked to the version performed.</summary>
public sealed class TrainingPlanVersioningTests(ApiFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Session_started_on_version_A_stays_on_A_while_the_next_opening_shows_B()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        var planId = await Scenario.DraftPlanAsync(trainer, client.Id);
        (await Scenario.PublishAsync(trainer, planId)).VersionNumber.ShouldBe(1);

        var (sessionId, plannedA) = await Scenario.StartAsync(client, planId);

        // The trainer adds a session and publishes version 2 while the client is training on version 1.
        await Scenario.SetSessionsAsync(trainer, planId, ["Treino B"], keep: [plannedA.Session]);
        (await Scenario.PublishAsync(trainer, planId, "Adicionado treino B")).VersionNumber.ShouldBe(2);
        await Scenario.CompleteAsync(client, sessionId);

        var session = await Scenario.Read<WorkoutSessionResponse>(await client.Http.GetAsync($"/api/v1/workout-sessions/{sessionId}", Ct));
        session.PlanVersionNumber.ShouldBe(1);

        var current = await Scenario.MyPlanAsync(client, planId);
        current.VersionNumber.ShouldBe(2);
        current.ChangeDescription.ShouldBe("Adicionado treino B");
        current.Sessions.Count.ShouldBe(2);
        // Planned-session ids are stable across versions, so history follows the session.
        current.Sessions.Single(s => s.Session.Id == plannedA.Session.Id).Stats.CompletedCount.ShouldBe(1);

        var v1 = await Scenario.Read<PlanVersionResponse>(await client.Http.GetAsync($"/api/v1/training-plans/{planId}/versions/1", Ct));
        v1.Content.Sessions.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Drafts_are_never_visible_to_the_client()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        var planId = await Scenario.DraftPlanAsync(trainer, client.Id);

        (await client.Http.GetAsync($"/api/v1/me/training-plans/{planId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Publishing_notifies_the_client()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        var planId = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, planId);

        var notifications = await Scenario.Read<List<NotificationResponse>>(await client.Http.GetAsync("/api/v1/me/notifications?unreadOnly=true", Ct));
        var published = notifications.ShouldHaveSingleItem();
        published.Type.ShouldBe(NotificationType.TrainingPlanPublished);
        published.ResourceId.ShouldBe(planId);

        await Scenario.EnsureSuccess(await client.Http.PostAsync($"/api/v1/me/notifications/{published.Id}/read", null, Ct));
        (await Scenario.Read<List<NotificationResponse>>(await client.Http.GetAsync("/api/v1/me/notifications?unreadOnly=true", Ct))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Copying_a_plan_creates_an_independent_draft()
    {
        var client = await Scenario.ClientAsync();
        var otherClient = await Scenario.ClientAsync("other");
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        await Scenario.LinkAsync(trainer, otherClient);
        var planId = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, planId);

        var copy = await Scenario.Read<Scenario.IdResponse>(await trainer.Http.PostAsJsonAsync(
            $"/api/v1/training-plans/{planId}/copy", new { targetClientId = otherClient.Id, newName = "Plano copiado" }, Ct));

        var copied = await Scenario.Read<TrainerPlanResponse>(await trainer.Http.GetAsync($"/api/v1/training-plans/{copy.Id}", Ct));
        copied.ClientId.ShouldBe(otherClient.Id);
        copied.CopiedFromPlanId.ShouldBe(planId);
        copied.Versions.ShouldBeEmpty();
        copied.Draft.Sessions.Count.ShouldBe(1);
    }
}
