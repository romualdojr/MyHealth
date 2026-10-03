using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Coaching;
using MyHealth.Application.TrainingPlans;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Coaching;
using MyHealth.Infrastructure.Persistence;

namespace MyHealth.Api.IntegrationTests;

/// <summary>The trainer's AI coach (rule-based model): consent, suggestions from logged sets, safety rules and applying to the draft.</summary>
public sealed class CoachTests(ApiFactory factory) : IntegrationTest(factory)
{
    private sealed record Setup(TestUser Trainer, TestUser Client, Guid RelationshipId, Guid PlanId);

    private async Task<Setup> PublishedPlanAsync()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var relationshipId = await Scenario.LinkAsync(trainer, client);
        var planId = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, planId);
        return new Setup(trainer, client, relationshipId, planId);
    }

    /// <summary>A completed workout in which the client logged every prescribed set (one set of 8–12 at 40 kg in the scenario plan).</summary>
    private async Task WorkoutAsync(Setup setup, int daysAgo, int repetitions, int exertion, bool discomfort = false)
    {
        var startedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo).AddMinutes(-50);
        var (sessionId, planned) = await Scenario.StartAsync(setup.Client, setup.PlanId, startedAt);
        var exercise = planned.Session.Exercises[0];
        await Scenario.EnsureSuccess(await setup.Client.Http.PutAsJsonAsync($"/api/v1/workout-sessions/{sessionId}/sets/{Guid.CreateVersion7()}", new
        {
            exerciseId = exercise.ExerciseId, plannedExerciseId = exercise.Id, setNumber = 1, repetitions, loadKg = 40m, difficulty = exertion,
            loggedAt = startedAt.AddMinutes(10),
        }, Ct));
        await Scenario.EnsureSuccess(await setup.Client.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{sessionId}/complete", new
        {
            completedAt = startedAt.AddMinutes(45), perceivedExertion = exertion, reportedDiscomfort = discomfort,
            discomfortDetails = discomfort ? "Dor no ombro" : null,
        }, Ct));
    }

    private async Task<CoachAnalysisResponse> AnalyzeAsync(Setup setup, string? focus = null) =>
        await Scenario.Read<CoachAnalysisResponse>(await setup.Trainer.Http.PostAsJsonAsync($"/api/v1/training-plans/{setup.PlanId}/coach/analyses", new { focus }, Ct));

    [Fact]
    public async Task The_coach_needs_the_clients_permission_and_proposes_a_progression_the_trainer_applies_to_the_draft()
    {
        var setup = await PublishedPlanAsync();

        // Without the client's AI-analysis grant nothing is sent anywhere.
        var overview = await Scenario.Read<CoachOverviewResponse>(await setup.Trainer.Http.GetAsync($"/api/v1/training-plans/{setup.PlanId}/coach", Ct));
        overview.SharingGranted.ShouldBeFalse();
        var refused = await setup.Trainer.Http.PostAsJsonAsync($"/api/v1/training-plans/{setup.PlanId}/coach/analyses", new { focus = (string?)null }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("Coach.SharingRequired");

        await Scenario.ShareAsync(setup.Client, setup.RelationshipId, DataCategory.AiCoaching);

        // No workouts yet: nothing to adjust.
        var early = await AnalyzeAsync(setup);
        early.Suggestions.ShouldBeEmpty();
        early.Notices.ShouldContain(CoachSuggestionPolicy.NoDataNotice);

        // Two workouts at the top of the range (12 reps) with RPE 6: a small load increase, 40 → 42 kg.
        await WorkoutAsync(setup, daysAgo: 3, repetitions: 12, exertion: 6);
        await WorkoutAsync(setup, daysAgo: 1, repetitions: 12, exertion: 6);
        var analysis = await AnalyzeAsync(setup, focus: "progressão de carga");
        analysis.Model.ShouldBe("rules");
        analysis.Focus.ShouldBe("progressão de carga");
        analysis.PlanVersionNumber.ShouldBe(1);
        var load = analysis.Suggestions.ShouldHaveSingleItem();
        (load.Kind, load.LoadKg, load.Status).ShouldBe((CoachSuggestionKind.ChangeLoad, 42m, CoachSuggestionStatus.Pending));
        load.Change.ShouldNotBeNull().ShouldContain("42 kg");
        load.Reason.ShouldContain("40 kg");

        // Applying changes the draft only; the client keeps performing version 1 until the trainer publishes.
        var applied = await Scenario.Read<CoachAnalysisResponse>(
            await setup.Trainer.Http.PostAsync($"/api/v1/coach-analyses/{analysis.Id}/suggestions/{load.Id}/apply", null, Ct));
        applied.Suggestions.Single().Status.ShouldBe(CoachSuggestionStatus.Applied);
        await Scenario.EnsureSuccess(await setup.Trainer.Http.PostAsync($"/api/v1/coach-analyses/{analysis.Id}/suggestions/{load.Id}/apply", null, Ct));
        (await setup.Trainer.Http.PostAsync($"/api/v1/coach-analyses/{analysis.Id}/suggestions/{load.Id}/dismiss", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var plan = await Scenario.Read<TrainerPlanResponse>(await setup.Trainer.Http.GetAsync($"/api/v1/training-plans/{setup.PlanId}", Ct));
        plan.HasUnpublishedChanges.ShouldBeTrue();
        plan.Draft.Sessions[0].Exercises[0].Sets.ShouldAllBe(s => s.SuggestedLoadKg == 42m);
        (await Scenario.MyPlanAsync(setup.Client, setup.PlanId)).Sessions[0].Session.Exercises[0].Sets.ShouldAllBe(s => s.SuggestedLoadKg == 40m);

        overview = await Scenario.Read<CoachOverviewResponse>(await setup.Trainer.Http.GetAsync($"/api/v1/training-plans/{setup.PlanId}/coach", Ct));
        (overview.SharingGranted, overview.UsedToday, overview.Analyses.Count).ShouldBe((true, 2, 2));

        // Every analysis is a disclosure of the client's data to the model: audited with the client as subject, no content.
        await using var scope = Scenario.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.AuditEntries.CountAsync(e => e.Action == "assist" && e.ResourceType == nameof(CoachAnalysis) && e.SubjectUserId == setup.Client.Id, Ct)).ShouldBe(2);

        // Revoking the grant stops new analyses at once.
        await Scenario.EnsureSuccess(await setup.Client.Http.DeleteAsync($"/api/v1/care-relationships/{setup.RelationshipId}/sharing/{DataCategory.AiCoaching}", Ct));
        (await setup.Trainer.Http.PostAsJsonAsync($"/api/v1/training-plans/{setup.PlanId}/coach/analyses", new { focus = (string?)null }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task After_reported_discomfort_the_coach_never_proposes_a_progression()
    {
        var setup = await PublishedPlanAsync();
        await Scenario.ShareAsync(setup.Client, setup.RelationshipId, DataCategory.AiCoaching);
        await WorkoutAsync(setup, daysAgo: 3, repetitions: 12, exertion: 6);
        await WorkoutAsync(setup, daysAgo: 1, repetitions: 12, exertion: 6, discomfort: true);

        var analysis = await AnalyzeAsync(setup);

        analysis.Notices.ShouldContain(CoachSuggestionPolicy.DiscomfortNotice);
        analysis.Suggestions.ShouldNotContain(s => s.Kind == CoachSuggestionKind.ChangeLoad);
        var talk = analysis.Suggestions.ShouldHaveSingleItem();
        talk.Kind.ShouldBe(CoachSuggestionKind.TalkToClient);

        // "Talk to the client" changes nothing in the plan.
        await Scenario.EnsureSuccess(await setup.Trainer.Http.PostAsync($"/api/v1/coach-analyses/{analysis.Id}/suggestions/{talk.Id}/apply", null, Ct));
        (await Scenario.Read<TrainerPlanResponse>(await setup.Trainer.Http.GetAsync($"/api/v1/training-plans/{setup.PlanId}", Ct))).HasUnpublishedChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task A_trainer_of_another_organization_cannot_see_or_apply_an_analysis()
    {
        var setup = await PublishedPlanAsync();
        await Scenario.ShareAsync(setup.Client, setup.RelationshipId, DataCategory.AiCoaching);
        await WorkoutAsync(setup, daysAgo: 3, repetitions: 12, exertion: 6);
        await WorkoutAsync(setup, daysAgo: 1, repetitions: 12, exertion: 6);
        var analysis = await AnalyzeAsync(setup);
        var suggestion = analysis.Suggestions.ShouldHaveSingleItem();

        var (intruder, _) = await Scenario.ProfessionalAsync("intruder");
        (await intruder.Http.GetAsync($"/api/v1/training-plans/{setup.PlanId}/coach", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await intruder.Http.PostAsJsonAsync($"/api/v1/training-plans/{setup.PlanId}/coach/analyses", new { focus = (string?)null }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await intruder.Http.PostAsync($"/api/v1/coach-analyses/{analysis.Id}/suggestions/{suggestion.Id}/apply", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
