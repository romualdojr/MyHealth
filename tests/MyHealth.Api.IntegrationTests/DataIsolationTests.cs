using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.TrainingPlans;
using MyHealth.Application.Workouts;
using MyHealth.Domain.Professionals;

namespace MyHealth.Api.IntegrationTests;

/// <summary>Acceptance test 1 and §6 access matrix: no data mixing between clients or professionals.</summary>
public sealed class DataIsolationTests(ApiFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Client_with_two_trainers_gets_two_plans_and_each_trainer_sees_only_their_own()
    {
        var client = await Scenario.ClientAsync();
        var (trainerA, _) = await Scenario.ProfessionalAsync("trainer-a");
        var (trainerB, _) = await Scenario.ProfessionalAsync("trainer-b");
        await Scenario.LinkAsync(trainerA, client);
        await Scenario.LinkAsync(trainerB, client);

        var planA = await Scenario.DraftPlanAsync(trainerA, client.Id, "Plano A");
        var planB = await Scenario.DraftPlanAsync(trainerB, client.Id, "Plano B");
        await Scenario.PublishAsync(trainerA, planA);
        await Scenario.PublishAsync(trainerB, planB);

        var clientPlans = await Scenario.Read<List<ClientPlanListItem>>(await client.Http.GetAsync("/api/v1/me/training-plans", Ct));
        clientPlans.Select(p => p.Name).ShouldBe(["Plano A", "Plano B"], ignoreOrder: true);

        var aPlans = await Scenario.Read<List<TrainerPlanListItem>>(await trainerA.Http.GetAsync("/api/v1/training-plans", Ct));
        aPlans.ShouldHaveSingleItem().Id.ShouldBe(planA);
        (await trainerA.Http.GetAsync($"/api/v1/training-plans/{planB}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var (sessionOnB, _) = await Scenario.StartAsync(client, planB);
        await Scenario.CompleteAsync(client, sessionOnB);

        var aView = await Scenario.Read<List<WorkoutSessionListItem>>(await trainerA.Http.GetAsync($"/api/v1/trainer/clients/{client.Id}/workout-sessions", Ct));
        aView.ShouldBeEmpty();
        (await trainerA.Http.GetAsync($"/api/v1/workout-sessions/{sessionOnB}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await trainerB.Http.GetAsync($"/api/v1/workout-sessions/{sessionOnB}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Clients_cannot_see_each_others_plans_or_sessions()
    {
        var alice = await Scenario.ClientAsync("alice");
        var bob = await Scenario.ClientAsync("bob");
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, alice);
        var plan = await Scenario.DraftPlanAsync(trainer, alice.Id);
        await Scenario.PublishAsync(trainer, plan);
        var (session, _) = await Scenario.StartAsync(alice, plan);

        (await bob.Http.GetAsync($"/api/v1/me/training-plans/{plan}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await bob.Http.GetAsync($"/api/v1/workout-sessions/{session}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await bob.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{session}/complete", new { completedAt = DateTimeOffset.UtcNow, perceivedExertion = 5 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Scenario.Read<List<ClientPlanListItem>>(await bob.Http.GetAsync("/api/v1/me/training-plans", Ct))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Trainer_without_relationship_cannot_prescribe()
    {
        var client = await Scenario.ClientAsync();
        var (stranger, _) = await Scenario.ProfessionalAsync("stranger");

        var response = await stranger.Http.PostAsJsonAsync("/api/v1/training-plans", new
        {
            clientId = client.Id,
            details = new { name = "X", goal = "Y", level = "Beginner", startDate = "2026-10-01", suggestedSessionsPerWeek = 3 },
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unverified_professional_cannot_invite_clients()
    {
        var (unverified, _) = await Scenario.ProfessionalAsync("pending", verified: false);

        var response = await unverified.Http.PostAsJsonAsync("/api/v1/care-relationships/invitations", new { discipline = "PersonalTrainer", clientEmail = "x@test.local" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Dietitian_cannot_create_workouts()
    {
        var client = await Scenario.ClientAsync();
        var (dietitian, _) = await Scenario.ProfessionalAsync("dietitian", Discipline.Dietitian);
        await Scenario.LinkAsync(dietitian, client, Discipline.Dietitian);

        var response = await dietitian.Http.PostAsJsonAsync("/api/v1/training-plans", new
        {
            clientId = client.Id,
            details = new { name = "X", goal = "Y", level = "Beginner", startDate = "2026-10-01", suggestedSessionsPerWeek = 3 },
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Ending_the_relationship_stops_new_prescriptions()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var relationship = await Scenario.LinkAsync(trainer, client);
        var plan = await Scenario.DraftPlanAsync(trainer, client.Id);

        await Scenario.EnsureSuccess(await client.Http.PostAsJsonAsync($"/api/v1/care-relationships/{relationship}/end", new { reason = "Fim do contrato" }, Ct));

        (await trainer.Http.PostAsJsonAsync($"/api/v1/training-plans/{plan}/publish", new { changeDescription = "v1" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
