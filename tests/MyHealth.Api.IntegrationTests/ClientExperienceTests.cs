using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Assistant;
using MyHealth.Application.ClientExperience;
using MyHealth.Application.Exercises;
using MyHealth.Application.Notifications;
using MyHealth.Application.Nutrition;
using MyHealth.Application.Privacy;
using MyHealth.Application.TrainingPlans;
using MyHealth.Application.Wellbeing;
using MyHealth.Application.Workouts;
using MyHealth.Domain.Assistant;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Api.IntegrationTests;

/// <summary>
/// The client prototype's backend: exercise videos, questions answered during rest, wellbeing check-ins, body checks
/// and the assistant that prepares records the client confirms.
/// </summary>
public sealed class ClientExperienceTests(ApiFactory factory) : IntegrationTest(factory)
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private static DateTimeOffset LocalNow => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SaoPaulo);

    private static DateOnly Today => DateOnly.FromDateTime(LocalNow.DateTime);

    private sealed record Care(TestUser Client, TestUser Trainer, TestUser Dietitian, Guid TrainerRelationship, Guid TrainingPlan, Guid MealPlan, Guid LunchId, Guid PreviousWorkout);

    /// <summary>
    /// Ana: a trainer and a dietitian in different organizations, a published training plan, a meal plan whose lunch was
    /// half an hour ago, and a finished workout in which she logged a set and reported knee discomfort.
    /// </summary>
    private async Task<Care> CareAsync()
    {
        var client = await Scenario.ClientAsync("ana");
        var (trainer, _) = await Scenario.ProfessionalAsync("carlos");
        var (dietitian, _) = await Scenario.ProfessionalAsync("marina", Discipline.Dietitian, council: "CRN");
        var trainerRelationship = await Scenario.LinkAsync(trainer, client);
        await Scenario.LinkAsync(dietitian, client, Discipline.Dietitian);

        var plan = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, plan);

        var mealPlan = await Scenario.DraftMealPlanAsync(dietitian, client.Id);
        var lunchAt = LocalNow.AddMinutes(-30);
        var lunchTime = lunchAt.Date == LocalNow.Date ? TimeOnly.FromDateTime(lunchAt.DateTime) : TimeOnly.MinValue;
        await Scenario.EnsureSuccess(await dietitian.Http.PutAsJsonAsync($"/api/v1/meal-plans/{mealPlan}/meals", new object[]
        {
            new { name = "Almoço", time = lunchTime.ToString("HH:mm:00"), items = new object[] { new { food = "Arroz integral", householdMeasure = "4 colheres de sopa" } } },
        }, Ct));
        await Scenario.PublishMealPlanAsync(dietitian, mealPlan);
        var lunchId = (await Scenario.Read<ClientMealPlanResponse>(await client.Http.GetAsync($"/api/v1/me/meal-plans/{mealPlan}", Ct))).Current.Content.Meals[0].Id;

        var (previous, planned) = await Scenario.StartAsync(client, plan, DateTimeOffset.UtcNow.AddMinutes(-90));
        var exercise = planned.Session.Exercises[0];
        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync($"/api/v1/workout-sessions/{previous}/sets/{Guid.CreateVersion7()}", new
        {
            exerciseId = exercise.ExerciseId, plannedExerciseId = exercise.Id, setNumber = 1, repetitions = 12, loadKg = 40m, loggedAt = DateTimeOffset.UtcNow.AddMinutes(-80),
        }, Ct));
        await Scenario.CompleteAsync(client, previous, DateTimeOffset.UtcNow.AddMinutes(-40), discomfort: true);

        return new Care(client, trainer, dietitian, trainerRelationship, plan, mealPlan, lunchId, previous);
    }

    private async Task<int> DiscomfortNotificationsAsync(TestUser trainer) =>
        (await Scenario.Read<List<NotificationResponse>>(await trainer.Http.GetAsync("/api/v1/me/notifications", Ct))).Count(n => n.Type == NotificationType.DiscomfortReported);

    [Fact]
    public async Task Exercises_carry_videos_from_the_media_host_and_the_client_plan_includes_them_for_offline_use()
    {
        var care = await CareAsync();
        var admin = Scenario.Factory.NewUser("operator", administrator: true);
        var library = await Scenario.AnyLibraryExerciseAsync(care.Trainer);
        var loop = new { id = Guid.CreateVersion7(), kind = "Loop", angle = "Side", url = "https://media.myhealth.app/supino-lado.mp4", durationSeconds = 12 };
        var full = new
        {
            id = Guid.CreateVersion7(), kind = "FullVideo", angle = "Side", url = "https://media.myhealth.app/supino.mp4", posterUrl = "https://media.myhealth.app/supino.jpg",
            durationSeconds = 84, captionsUrl = "https://media.myhealth.app/supino.vtt", chapters = new[] { new { atSeconds = 0, title = "Posição" }, new { atSeconds = 21, title = "Descida" } },
        };

        // Video needs a text description first (accessibility), and only MyHealth's media host is accepted.
        (await admin.Http.PutAsJsonAsync($"/api/v1/admin/exercises/{library}/media", new { media = new object[] { loop } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var current = await Scenario.Read<ExerciseResponse>(await admin.Http.GetAsync($"/api/v1/exercises/{library}", Ct));
        await Scenario.EnsureSuccess(await admin.Http.PutAsJsonAsync($"/api/v1/admin/exercises/{library}", new
        {
            current.Name, current.Description, current.MuscleGroups, current.Equipment, current.Purpose, current.Precautions,
            mediaCaption = "Deitado no banco, desça a barra até o peito e empurre.",
        }, Ct));
        (await admin.Http.PutAsJsonAsync($"/api/v1/admin/exercises/{library}/media", new { media = new object[] { loop with { url = "https://youtube.com/watch?v=x" } } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Scenario.EnsureSuccess(await admin.Http.PutAsJsonAsync($"/api/v1/admin/exercises/{library}/media", new { media = new object[] { loop, full } }, Ct));

        // Trainers cannot change library media; they manage the media of their own exercises.
        (await care.Trainer.Http.PutAsJsonAsync($"/api/v1/exercises/{library}/media", new { media = new object[] { loop } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var custom = (await Scenario.Read<Scenario.IdResponse>(await care.Trainer.Http.PostAsJsonAsync("/api/v1/exercises", new
        {
            name = "Afundo búlgaro", description = "Pé de trás apoiado no banco.", muscleGroups = new[] { "quadriceps" }, equipment = new[] { "banco" },
            mediaCaption = "Desça o joelho de trás em direção ao chão.",
        }, Ct))).Id;
        await Scenario.EnsureSuccess(await care.Trainer.Http.PutAsJsonAsync($"/api/v1/exercises/{custom}/media", new { media = new object[] { loop with { id = Guid.CreateVersion7() } } }, Ct));
        (await Scenario.Read<ExerciseResponse>(await care.Trainer.Http.GetAsync($"/api/v1/exercises/{custom}", Ct))).Media.ShouldHaveSingleItem().DurationSeconds.ShouldBe(12);

        var plan = await Scenario.Read<ClientPlanResponse>(await care.Client.Http.GetAsync($"/api/v1/me/training-plans/{care.TrainingPlan}", Ct));
        var exercise = plan.Exercises.ShouldHaveSingleItem();
        exercise.MediaCaption.ShouldNotBeNull();
        exercise.Media.Select(m => m.Kind.ToString()).ShouldBe(["Loop", "FullVideo"]);
        exercise.Media[1].Chapters.Select(c => c.Title).ShouldBe(["Posição", "Descida"]);
    }

    [Fact]
    public async Task During_rest_the_client_gets_the_days_questions_each_saying_who_receives_the_answer()
    {
        var care = await CareAsync();
        var (session, planned) = await Scenario.StartAsync(care.Client, care.TrainingPlan);

        var prompts = await Scenario.Read<List<PromptItem>>(await care.Client.Http.GetAsync($"/api/v1/me/prompts?workoutSessionId={session}", Ct));
        prompts.Select(p => p.Kind).ShouldBe([PromptKind.BodyCheck, PromptKind.Meal, PromptKind.Wellbeing]);

        var knee = prompts[0];
        knee.Context.ShouldNotBeNull().ShouldContain("Dor no joelho");
        knee.Recipients.ShouldHaveSingleItem().Name.ShouldBe("carlos Pro");
        knee.Questions.ShouldHaveSingleItem().Options.Select(o => o.Value).ShouldBe(["Better", "Same", "Worse"]);

        var lunch = prompts[1];
        (lunch.Target.MealPlanId, lunch.Target.PlannedMealId).ShouldBe((care.MealPlan, care.LunchId));
        lunch.Recipients.ShouldHaveSingleItem().Name.ShouldBe("marina Pro");
        lunch.RecipientNote.ShouldBe("Seu treinador não vê o diário alimentar.");

        prompts[2].RecipientNote.ShouldBe("Fica só com você: nenhum profissional tem acesso.");
        prompts[2].Questions.Select(q => q.Field).ShouldBe(["sleepQuality", "energy"]);

        // Answering: the knee is worse and Ana tells Carlos; lunch as planned; how she slept.
        var notified = await DiscomfortNotificationsAsync(care.Trainer);
        await Scenario.EnsureSuccess(await care.Client.Http.PutAsJsonAsync($"/api/v1/workout-sessions/{session}/body-checks/{Guid.CreateVersion7()}", new
        {
            subject = knee.Target.Subject, answer = "Worse", details = "Dói na descida", reportToTrainer = true,
        }, Ct));
        (await DiscomfortNotificationsAsync(care.Trainer)).ShouldBe(notified + 1);
        await Scenario.EnsureSuccess(await care.Client.Http.PutAsJsonAsync($"/api/v1/me/food-diary/{Guid.CreateVersion7()}", new
        {
            mealPlanId = lunch.Target.MealPlanId, plannedMealId = lunch.Target.PlannedMealId, eatenAt = DateTimeOffset.UtcNow, adherence = "AsPlanned",
        }, Ct));
        (await care.Client.Http.PutAsJsonAsync($"/api/v1/me/wellbeing/{Today:yyyy-MM-dd}", new { sleepQuality = 6 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Scenario.EnsureSuccess(await care.Client.Http.PutAsJsonAsync($"/api/v1/me/wellbeing/{Today:yyyy-MM-dd}", new { sleepQuality = 3, workoutSessionId = session }, Ct));
        var checkIn = await Scenario.Read<WellbeingCheckInResponse>(
            await care.Client.Http.PutAsJsonAsync($"/api/v1/me/wellbeing/{Today:yyyy-MM-dd}", new { energy = 4 }, Ct));
        (checkIn.SleepQuality, checkIn.Energy, checkIn.WorkoutSessionId).ShouldBe((3, 4, session));
        (await Scenario.Read<List<WellbeingCheckInResponse>>(await care.Client.Http.GetAsync($"/api/v1/me/wellbeing?from={Today.AddDays(-7):yyyy-MM-dd}", Ct)))
            .ShouldHaveSingleItem().Id.ShouldBe(checkIn.Id);

        (await Scenario.Read<List<PromptItem>>(await care.Client.Http.GetAsync($"/api/v1/me/prompts?workoutSessionId={session}", Ct))).ShouldBeEmpty();
        var workout = await Scenario.Read<WorkoutSessionResponse>(await care.Client.Http.GetAsync($"/api/v1/workout-sessions/{session}", Ct));
        workout.BodyChecks.ShouldHaveSingleItem().ShouldSatisfyAllConditions(c => c.Answer.ShouldBe(BodyCheckAnswer.Worse), c => c.ReportedToTrainer.ShouldBeTrue());

        // Someone else's workout is not found; the trainer sees check-ins only once Ana shares them.
        var other = await Scenario.ClientAsync("other");
        (await other.Http.GetAsync($"/api/v1/me/prompts?workoutSessionId={session}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await care.Trainer.Http.GetAsync($"/api/v1/care-relationships/{care.TrainerRelationship}/wellbeing", Ct)).IsSuccessStatusCode.ShouldBeFalse();
        await Scenario.ShareAsync(care.Client, care.TrainerRelationship, DataCategory.Wellbeing);
        (await Scenario.Read<List<WellbeingCheckInResponse>>(await care.Trainer.Http.GetAsync($"/api/v1/care-relationships/{care.TrainerRelationship}/wellbeing", Ct)))
            .ShouldHaveSingleItem().Energy.ShouldBe(4);

        // Everything is in Ana's data export.
        var export = await Scenario.Read<DataExportResponse>(await care.Client.Http.PostAsync("/api/v1/me/data-exports", null, Ct));
        var package = System.Text.Json.JsonSerializer.Deserialize<DataExportPackage>(await (await care.Client.Http.GetAsync(export.DownloadPath, Ct)).Content.ReadAsStringAsync(Ct), Scenario.Json)!;
        package.WellbeingCheckIns.ShouldHaveSingleItem().SleepQuality.ShouldBe(3);
        package.WorkoutSessions.Single(s => s.Id == session).BodyChecks.ShouldHaveSingleItem();
        planned.Session.Exercises.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_assistant_needs_consent_prepares_records_from_what_the_client_says_and_saves_only_what_they_confirm()
    {
        var care = await CareAsync();
        var client = care.Client;

        var status = await Scenario.Read<AssistantStatusResponse>(await client.Http.GetAsync("/api/v1/me/assistant", Ct));
        (status.Terms.Version, status.Consent).ShouldBe((AssistantTerms.CurrentVersion, null));
        var refused = await client.Http.PostAsJsonAsync("/api/v1/me/assistant/messages", new { message = "Oi" }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("Assistant.ConsentRequired");
        (await client.Http.PutAsJsonAsync("/api/v1/me/assistant/consent", new { termsVersion = 0, scopes = new[] { "Training" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.Http.PutAsJsonAsync("/api/v1/me/assistant/consent", new { termsVersion = 1, scopes = Array.Empty<string>() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Scenario.Read<AssistantConsentResponse>(await client.Http.PutAsJsonAsync(
            "/api/v1/me/assistant/consent", new { termsVersion = 1, scopes = new[] { "Training", "Nutrition" } }, Ct))).Active.ShouldBeTrue();

        // A meal and a pain in one message become two cards; nothing is saved yet.
        var reply = await Scenario.Read<AssistantReplyResponse>(await client.Http.PostAsJsonAsync("/api/v1/me/assistant/messages", new
        {
            message = "Almocei arroz, feijão e frango, mas troquei a salada por legumes refogados. E ontem doeu o joelho direito no agachamento.",
        }, Ct));
        reply.Proposals.Select(p => p.Kind).ShouldBe([ProposalKind.FoodDiaryEntry, ProposalKind.BodyCheck]);
        reply.Reply.ShouldContain("não consigo avaliar dor");
        var meal = reply.Proposals[0];
        meal.Fields.ShouldContain(f => f.Label == "Plano" && f.Value == "Segui em parte");
        meal.Fields.ShouldContain(f => f.Label == "Quem vê" && f.Value.StartsWith("marina Pro"));
        var pain = reply.Proposals[1];
        pain.Notice.ShouldNotBeNull().ShouldContain("procure atendimento médico");
        pain.Fields.ShouldContain(f => f.Label == "Para" && f.Value == "carlos Pro");
        var diary = await client.Http.GetAsync($"/api/v1/me/food-diary?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}", Ct);
        (await Scenario.Read<List<FoodDiaryEntryResponse>>(diary)).ShouldBeEmpty();

        // Confirming runs the regular command; confirming again returns the same record; another person cannot.
        var other = await Scenario.ClientAsync("other");
        (await other.Http.PostAsync($"/api/v1/me/assistant/proposals/{meal.Id}/confirm", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var saved = await Scenario.Read<ConfirmProposalResponse>(await client.Http.PostAsync($"/api/v1/me/assistant/proposals/{meal.Id}/confirm", null, Ct));
        (await Scenario.Read<ConfirmProposalResponse>(await client.Http.PostAsync($"/api/v1/me/assistant/proposals/{meal.Id}/confirm", null, Ct))).ResourceId.ShouldBe(saved.ResourceId);
        (await client.Http.PostAsync($"/api/v1/me/assistant/proposals/{meal.Id}/discard", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var entry = (await Scenario.Read<List<FoodDiaryEntryResponse>>(
            await client.Http.GetAsync($"/api/v1/me/food-diary?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}", Ct))).ShouldHaveSingleItem();
        (entry.Id, entry.PlannedMealId, entry.Adherence).ShouldBe((saved.ResourceId, care.LunchId, Domain.Nutrition.MealAdherence.Partly));

        var notified = await DiscomfortNotificationsAsync(care.Trainer);
        await Scenario.EnsureSuccess(await client.Http.PostAsync($"/api/v1/me/assistant/proposals/{pain.Id}/confirm", null, Ct));
        (await DiscomfortNotificationsAsync(care.Trainer)).ShouldBe(notified + 1);
        (await Scenario.Read<WorkoutSessionResponse>(await client.Http.GetAsync($"/api/v1/workout-sessions/{care.PreviousWorkout}", Ct)))
            .BodyChecks.ShouldHaveSingleItem().Subject.ShouldBe("joelho direito");

        // Questions are answered from Ana's own records.
        (await Scenario.Read<AssistantReplyResponse>(await client.Http.PostAsJsonAsync("/api/v1/me/assistant/messages", new
        {
            message = "Quanto peguei no supino da última vez?",
        }, Ct))).Reply.ShouldContain("12 × 40 kg");

        // Hands-free during a workout: the screen sends the session, exercise and set.
        var (session, planned) = await Scenario.StartAsync(client, care.TrainingPlan);
        var exerciseId = planned.Session.Exercises[0].ExerciseId;
        var voice = await Scenario.Read<AssistantReplyResponse>(await client.Http.PostAsJsonAsync("/api/v1/me/assistant/messages", new
        {
            message = "Feito, dez com quarenta e dois e meio", workoutSessionId = session, exerciseId, setNumber = 3,
        }, Ct));
        var set = voice.Proposals.ShouldHaveSingleItem();
        set.Fields.ShouldContain(f => f.Label == "Repetições" && f.Value == "10");
        set.Fields.ShouldContain(f => f.Label == "Carga" && f.Value == "42,5 kg");
        await Scenario.EnsureSuccess(await client.Http.PostAsync($"/api/v1/me/assistant/proposals/{set.Id}/confirm", null, Ct));
        var logged = (await Scenario.Read<WorkoutSessionResponse>(await client.Http.GetAsync($"/api/v1/workout-sessions/{session}", Ct))).Sets.ShouldHaveSingleItem();
        (logged.SetNumber, logged.Repetitions, logged.LoadKg).ShouldBe((3, 10, 42.5m));

        // Without the nutrition scope it does not prepare meals; after withdrawing, it does nothing.
        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync("/api/v1/me/assistant/consent", new { termsVersion = 1, scopes = new[] { "Training" } }, Ct));
        (await Scenario.Read<AssistantReplyResponse>(await client.Http.PostAsJsonAsync("/api/v1/me/assistant/messages", new { message = "Jantei sopa de legumes" }, Ct)))
            .Proposals.ShouldBeEmpty();
        var pending = (await Scenario.Read<AssistantReplyResponse>(await client.Http.PostAsJsonAsync("/api/v1/me/assistant/messages", new
        {
            message = "Feito, oito com quarenta", workoutSessionId = session, exerciseId, setNumber = 4,
        }, Ct))).Proposals.ShouldHaveSingleItem();
        var wrong = (await Scenario.Read<AssistantReplyResponse>(await client.Http.PostAsJsonAsync("/api/v1/me/assistant/messages", new
        {
            message = "Feito, oitenta com quatro", workoutSessionId = session, exerciseId, setNumber = 4,
        }, Ct))).Proposals.ShouldHaveSingleItem();
        await Scenario.EnsureSuccess(await client.Http.PostAsync($"/api/v1/me/assistant/proposals/{wrong.Id}/discard", null, Ct));
        (await client.Http.PostAsync($"/api/v1/me/assistant/proposals/{wrong.Id}/confirm", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await Scenario.EnsureSuccess(await client.Http.DeleteAsync("/api/v1/me/assistant/consent", Ct));
        (await client.Http.PostAsync($"/api/v1/me/assistant/proposals/{pending.Id}/confirm", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.Http.PostAsJsonAsync("/api/v1/me/assistant/messages", new { message = "Oi" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Scenario.Read<AssistantStatusResponse>(await client.Http.GetAsync("/api/v1/me/assistant", Ct))).Consent!.Active.ShouldBeFalse();
    }
}
