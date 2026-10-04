using Microsoft.Extensions.DependencyInjection;
using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Coaching;
using MyHealth.Application.TrainerAssistant;
using MyHealth.Application.TrainingPlans;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Coaching;

namespace MyHealth.Api.IntegrationTests;

/// <summary>The trainer's chat (rule-based model): every student at a glance, summaries, and suggestions to evolve exercises.</summary>
public sealed class TrainerAssistantTests(ApiFactory factory) : IntegrationTest(factory)
{
    private sealed record Student(TestUser User, Guid RelationshipId, Guid PlanId);

    private async Task<Student> StudentAsync(TestUser trainer, string name, bool allowAi)
    {
        var client = await Scenario.ClientAsync(name);
        var relationshipId = await Scenario.LinkAsync(trainer, client);
        if (allowAi)
        {
            await Scenario.ShareAsync(client, relationshipId, DataCategory.AiCoaching);
        }

        var planId = await Scenario.DraftPlanAsync(trainer, client.Id, $"Plano de {name}");
        await Scenario.PublishAsync(trainer, planId);
        return new Student(client, relationshipId, planId);
    }

    /// <summary>A completed workout with the scenario plan's single set (8–12 at 40 kg) logged at <paramref name="repetitions"/>.</summary>
    private async Task WorkoutAsync(Student student, int daysAgo, int repetitions, int exertion, bool discomfort = false)
    {
        var startedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo).AddMinutes(-50);
        var (sessionId, planned) = await Scenario.StartAsync(student.User, student.PlanId, startedAt);
        var exercise = planned.Session.Exercises[0];
        await Scenario.EnsureSuccess(await student.User.Http.PutAsJsonAsync($"/api/v1/workout-sessions/{sessionId}/sets/{Guid.CreateVersion7()}", new
        {
            exerciseId = exercise.ExerciseId, plannedExerciseId = exercise.Id, setNumber = 1, repetitions, loadKg = 40m, difficulty = exertion,
            loggedAt = startedAt.AddMinutes(10),
        }, Ct));
        await Scenario.EnsureSuccess(await student.User.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{sessionId}/complete", new
        {
            completedAt = startedAt.AddMinutes(45), perceivedExertion = exertion, reportedDiscomfort = discomfort, discomfortDetails = discomfort ? "Dor no ombro" : null,
        }, Ct));
    }

    private async Task<TrainerAssistantReplyResponse> AskAsync(TestUser trainer, string message, Guid? clientId = null) =>
        await Scenario.Read<TrainerAssistantReplyResponse>(
            await trainer.Http.PostAsJsonAsync("/api/v1/trainer/assistant/messages", new { message, history = Array.Empty<object>(), clientId }, Ct));

    [Fact]
    public async Task Each_organization_has_a_daily_limit_of_chat_messages()
    {
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var tenantId = Guid.Parse(trainer.Http.DefaultRequestHeaders.GetValues(MyHealth.Infrastructure.Identity.TenantHeader.Name).Single());
        await Scenario.EnsureSuccess(await trainer.Http.PostAsJsonAsync("/api/v1/trainer/assistant/messages", new { message = "resumo" }, Ct));
        var overview = await Scenario.Read<TrainerAssistantOverview>(await trainer.Http.GetAsync("/api/v1/trainer/assistant", Ct));
        (overview.DailyLimit, overview.UsedToday).ShouldBe((300, 1));

        // The rest of the day's quota is used by colleagues of the same organization.
        await using (var scope = Scenario.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyHealth.Infrastructure.Persistence.ApplicationDbContext>();
            db.AuditEntries.AddRange(Enumerable.Range(0, 299).Select(_ => MyHealth.Application.Abstractions.Auditing.AuditEntry.Create(
                Guid.NewGuid(), "message", "TrainerAssistant.Message", Guid.NewGuid(), null, tenantId, DateTimeOffset.UtcNow.AddHours(-1))));
            await db.SaveChangesAsync(Ct);
        }

        var refused = await trainer.Http.PostAsJsonAsync("/api/v1/trainer/assistant/messages", new { message = "resumo" }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("Coach.DailyChatLimitReached");

        // Another organization is not affected.
        var (other, _) = await Scenario.ProfessionalAsync("other");
        await Scenario.EnsureSuccess(await other.Http.PostAsJsonAsync("/api/v1/trainer/assistant/messages", new { message = "resumo" }, Ct));
    }

    [Fact]
    public async Task The_trainer_sees_every_student_and_the_chat_summarizes_only_those_who_allow_ai()
    {
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var ana = await StudentAsync(trainer, "ana", allowAi: true);
        var bia = await StudentAsync(trainer, "bia", allowAi: true);
        var caio = await StudentAsync(trainer, "caio", allowAi: false);
        await WorkoutAsync(ana, daysAgo: 3, repetitions: 12, exertion: 6);
        await WorkoutAsync(bia, daysAgo: 2, repetitions: 9, exertion: 8, discomfort: true);
        await WorkoutAsync(caio, daysAgo: 1, repetitions: 10, exertion: 7);

        // The roster: everyone, with summaries computed from the records (no AI).
        var overview = await Scenario.Read<TrainerAssistantOverview>(await trainer.Http.GetAsync("/api/v1/trainer/assistant", Ct));
        overview.Model.ShouldBe("rules");
        overview.Students.Select(s => (s.Name, s.AiConsent)).ShouldBe([("ana Silva", true), ("bia Silva", true), ("caio Silva", false)]);
        var biaSummary = overview.Students.Single(s => s.Name == "bia Silva");
        // Expected sessions count only the days the plan has been running (at most 4 weeks).
        biaSummary.ExpectedLast28Days.ShouldBeInRange(1, 12);
        (biaSummary.CompletedLast28Days, biaSummary.RecentDiscomfort, biaSummary.PendingFeedback, biaSummary.AverageExertion14Days)
            .ShouldBe((1, true, 1, 8.0));
        biaSummary.PlanName.ShouldBe("Plano de bia");

        // A summary of everyone: the names come back in the answer; the student without consent is only counted.
        var all = await AskAsync(trainer, "Me dá um resumo da semana de todos");
        (all.StudentsIncluded, all.StudentsWithoutConsent).ShouldBe((2, 1));
        all.Reply.ShouldContain("ana Silva");
        all.Reply.ShouldContain("bia Silva");
        all.Reply.ShouldNotContain("caio");
        all.Reply.ShouldNotContain("Aluno 1");
        all.Reply.ShouldContain("Relatou desconforto");
        all.Suggestions.ShouldBeEmpty();

        // Asking by name about one student (the name is replaced by an alias before the model sees it).
        var one = await AskAsync(trainer, "Como está a bia?");
        one.Reply.ShouldContain("bia Silva");
        one.Reply.ShouldNotContain("ana Silva");

        // Focusing a student who has not allowed AI analysis is refused.
        var refused = await trainer.Http.PostAsJsonAsync("/api/v1/trainer/assistant/messages", new { message = "resumo", clientId = caio.User.Id }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("Coach.SharingRequired");
    }

    [Fact]
    public async Task The_chat_suggests_how_to_evolve_each_students_exercises_and_the_trainer_applies_them()
    {
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var ana = await StudentAsync(trainer, "ana", allowAi: true);
        var bia = await StudentAsync(trainer, "bia", allowAi: true);
        await WorkoutAsync(ana, daysAgo: 4, repetitions: 12, exertion: 6);
        await WorkoutAsync(ana, daysAgo: 1, repetitions: 12, exertion: 6);
        await WorkoutAsync(bia, daysAgo: 4, repetitions: 12, exertion: 6);
        await WorkoutAsync(bia, daysAgo: 1, repetitions: 12, exertion: 6, discomfort: true);

        // For everyone: Ana is ready to progress; Bia reported discomfort, so the chat proposes a conversation instead.
        var reply = await AskAsync(trainer, "Como evoluir os exercícios de cada aluno?");
        var anaLoad = reply.Suggestions.Where(s => s.ClientId == ana.User.Id).ShouldHaveSingleItem();
        (anaLoad.ClientName, anaLoad.PlanId, anaLoad.PlanName).ShouldBe(("ana Silva", ana.PlanId, "Plano de ana"));
        (anaLoad.Suggestion.Kind, anaLoad.Suggestion.LoadKg).ShouldBe((CoachSuggestionKind.ChangeLoad, 42m));
        reply.Suggestions.Where(s => s.ClientId == bia.User.Id).Select(s => s.Suggestion.Kind).ShouldBe([CoachSuggestionKind.TalkToClient]);
        reply.Reply.ShouldContain("ana Silva:");

        // Applying a chat suggestion changes that student's draft only, as with the coach panel.
        await Scenario.EnsureSuccess(await trainer.Http.PostAsync($"/api/v1/coach-analyses/{anaLoad.AnalysisId}/suggestions/{anaLoad.Suggestion.Id}/apply", null, Ct));
        var plan = await Scenario.Read<TrainerPlanResponse>(await trainer.Http.GetAsync($"/api/v1/training-plans/{ana.PlanId}", Ct));
        plan.HasUnpublishedChanges.ShouldBeTrue();
        plan.Draft.Sessions[0].Exercises[0].Sets.ShouldAllBe(s => s.SuggestedLoadKg == 42m);

        // Focused on one student, only that student's exercises are considered.
        var focused = await AskAsync(trainer, "sugira ajustes de carga", bia.User.Id);
        focused.StudentsIncluded.ShouldBe(2);
        focused.Suggestions.ShouldAllBe(s => s.ClientId == bia.User.Id);

        // Another organization's trainer has no students here.
        var (other, _) = await Scenario.ProfessionalAsync("other");
        (await Scenario.Read<TrainerAssistantOverview>(await other.Http.GetAsync("/api/v1/trainer/assistant", Ct))).Students.ShouldBeEmpty();
        (await other.Http.PostAsJsonAsync("/api/v1/trainer/assistant/messages", new { message = "resumo", clientId = ana.User.Id }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
