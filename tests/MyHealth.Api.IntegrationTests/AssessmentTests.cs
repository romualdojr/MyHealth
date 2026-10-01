using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Assessments;
using MyHealth.Application.Notifications;
using MyHealth.Application.TrainingPlans;
using MyHealth.Application.Workouts;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;

namespace MyHealth.Api.IntegrationTests;

/// <summary>Health history, physical assessments and My Progress (RF-23 → RF-25).</summary>
public sealed class AssessmentTests(ApiFactory factory) : IntegrationTest(factory)
{
    private static object Assessment(Guid clientId, string assessedOn, decimal bodyFat, string method = "bioimpedance") => new
    {
        clientId,
        assessedOn,
        notes = "Avaliação inicial",
        measurements = new[]
        {
            new { metric = "body_mass", method = "digital_scale", unit = "kg", value = 80.5m },
            new { metric = "body_fat_percentage", method, unit = "%", value = bodyFat },
        },
    };

    private static object History(string? injuries = null, string goals = "Correr 10 km") => new
    {
        goals, exerciseHistory = "Caminhada 3x/semana", limitations = (string?)null, reportedInjuries = injuries, availability = "Manhãs",
        equipment = "Halteres", acknowledged = true,
    };

    [Fact]
    public async Task Health_history_is_versioned_needs_acknowledgement_and_is_shared_only_with_a_grant()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var relationship = await Scenario.LinkAsync(trainer, client);

        (await client.Http.PutAsJsonAsync("/api/v1/me/health-history", new { goals = "x", acknowledged = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var first = await Scenario.Read<HealthHistoryRevisionItem>(await client.Http.PutAsJsonAsync("/api/v1/me/health-history", History(), Ct));
        var retry = await Scenario.Read<HealthHistoryRevisionItem>(await client.Http.PutAsJsonAsync("/api/v1/me/health-history", History(), Ct));
        retry.Number.ShouldBe(first.Number);
        var second = await Scenario.Read<HealthHistoryRevisionItem>(await client.Http.PutAsJsonAsync("/api/v1/me/health-history", History(goals: "Maratona"), Ct));
        second.Number.ShouldBe(2);

        // Without the grant the trainer cannot read it, even with an active relationship.
        (await trainer.Http.GetAsync($"/api/v1/care-relationships/{relationship}/health-history", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await Scenario.ShareAsync(client, relationship, DataCategory.HealthHistory);
        var shared = await Scenario.Read<HealthHistoryResponse>(await trainer.Http.GetAsync($"/api/v1/care-relationships/{relationship}/health-history", Ct));
        shared.Current.Content.Goals.ShouldBe("Maratona");
        shared.Revisions.Select(r => r.Number).ShouldBe([2, 1]);

        await Scenario.EnsureSuccess(await trainer.Http.PostAsJsonAsync(
            $"/api/v1/care-relationships/{relationship}/health-history/reviews", new { revisionNumber = 2, notes = "Lido" }, Ct));

        var mine = await Scenario.Read<HealthHistoryResponse>(await client.Http.GetAsync("/api/v1/me/health-history", Ct));
        var review = mine.Reviews.ShouldHaveSingleItem();
        (review.RevisionNumber, review.ReviewerName, review.Discipline).ShouldBe((2, "trainer Pro", Discipline.PersonalTrainer));
        var revision1 = await Scenario.Read<HealthHistoryRevisionResponse>(await client.Http.GetAsync("/api/v1/me/health-history/revisions/1", Ct));
        revision1.Content.Goals.ShouldBe("Correr 10 km");
    }

    [Fact]
    public async Task A_new_injury_flags_the_training_plan_for_review_until_the_trainer_acknowledges_it()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var relationship = await Scenario.LinkAsync(trainer, client);
        await Scenario.ShareAsync(client, relationship, DataCategory.HealthHistory);
        var plan = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, plan);

        // A trainer the client does not share the history with learns nothing, not even that something changed.
        var (otherTrainer, _) = await Scenario.ProfessionalAsync("other-trainer");
        await Scenario.LinkAsync(otherTrainer, client);
        var otherPlan = await Scenario.DraftPlanAsync(otherTrainer, client.Id);
        await Scenario.PublishAsync(otherTrainer, otherPlan);

        // Goals and availability are not significant: no flag.
        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync("/api/v1/me/health-history", History(), Ct));
        (await Scenario.Read<TrainerPlanResponse>(await trainer.Http.GetAsync($"/api/v1/training-plans/{plan}", Ct))).ReviewRequestedAt.ShouldBeNull();

        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync("/api/v1/me/health-history", History(injuries: "Tendinite no ombro direito"), Ct));

        var flagged = await Scenario.Read<TrainerPlanResponse>(await trainer.Http.GetAsync($"/api/v1/training-plans/{plan}", Ct));
        flagged.ReviewRequestedAt.ShouldNotBeNull();
        flagged.ReviewReason.ShouldNotBeNull().ShouldNotContain("Tendinite");
        (await Scenario.Read<TrainerPlanResponse>(await otherTrainer.Http.GetAsync($"/api/v1/training-plans/{otherPlan}", Ct))).ReviewRequestedAt.ShouldBeNull();
        (await Scenario.Read<List<NotificationResponse>>(await otherTrainer.Http.GetAsync("/api/v1/me/notifications", Ct)))
            .ShouldNotContain(n => n.Type == NotificationType.TrainingPlanReviewRequested);
        var dashboard = await Scenario.Read<TrainerDashboardResponse>(await trainer.Http.GetAsync("/api/v1/trainer/dashboard", Ct));
        dashboard.TotalPlansNeedingReview.ShouldBe(1);
        var notifications = await Scenario.Read<List<NotificationResponse>>(await trainer.Http.GetAsync("/api/v1/me/notifications", Ct));
        notifications.ShouldContain(n => n.Type == NotificationType.TrainingPlanReviewRequested && n.ResourceId == plan);

        await Scenario.EnsureSuccess(await trainer.Http.PostAsync($"/api/v1/training-plans/{plan}/acknowledge-review", null, Ct));
        (await Scenario.Read<TrainerPlanResponse>(await trainer.Http.GetAsync($"/api/v1/training-plans/{plan}", Ct))).ReviewRequestedAt.ShouldBeNull();
        (await trainer.Http.PostAsync($"/api/v1/training-plans/{plan}/acknowledge-review", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Physical_assessments_are_corrected_with_history_and_shared_only_inside_the_organization_with_a_grant()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync(ownTenant: false);
        var (dietitian, _) = await Scenario.ProfessionalAsync("dietitian", Discipline.Dietitian, ownTenant: false);
        var clinic = await Scenario.CreateTenantAsync(trainer, "Clínica", TenantKind.Organization, Discipline.PersonalTrainer);
        Scenario.UseTenant(trainer, clinic);
        await Scenario.AddMemberAsync(trainer, dietitian, Discipline.Dietitian);
        Scenario.UseTenant(dietitian, clinic);
        await Scenario.LinkAsync(trainer, client);
        var dietitianRelationship = await Scenario.LinkAsync(dietitian, client, Discipline.Dietitian);

        var id = (await Scenario.Read<Scenario.IdResponse>(await trainer.Http.PostAsJsonAsync("/api/v1/physical-assessments", Assessment(client.Id, "2026-09-01", 22.5m), Ct))).Id;
        await Scenario.EnsureSuccess(await trainer.Http.PostAsJsonAsync($"/api/v1/physical-assessments/{id}/corrections", new
        {
            assessedOn = "2026-09-01", reason = "Valor transcrito errado",
            measurements = new[] { new { metric = "body_fat_percentage", method = "bioimpedance", unit = "%", value = 23.5m } },
        }, Ct));

        var mine = (await Scenario.Read<List<PhysicalAssessmentResponse>>(await client.Http.GetAsync("/api/v1/me/physical-assessments", Ct))).ShouldHaveSingleItem();
        (mine.Version, mine.OrganizationName, mine.AssessorName).ShouldBe((2, "Clínica", "trainer Pro"));
        mine.Measurements.ShouldHaveSingleItem().Value.ShouldBe(23.5m);
        mine.Corrections.ShouldHaveSingleItem().PreviousMeasurements.Count.ShouldBe(2);

        (await dietitian.Http.GetAsync($"/api/v1/care-relationships/{dietitianRelationship}/physical-assessments", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await Scenario.ShareAsync(client, dietitianRelationship, DataCategory.PhysicalAssessments);
        var shared = await Scenario.Read<List<PhysicalAssessmentResponse>>(
            await dietitian.Http.GetAsync($"/api/v1/care-relationships/{dietitianRelationship}/physical-assessments", Ct));
        shared.ShouldHaveSingleItem().Id.ShouldBe(id);

        // A trainer of another organization knows the id but gets nothing.
        var (outsider, _) = await Scenario.ProfessionalAsync("outsider");
        await Scenario.LinkAsync(outsider, client);
        (await outsider.Http.GetAsync($"/api/v1/physical-assessments/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await outsider.Http.PostAsJsonAsync($"/api/v1/physical-assessments/{id}/corrections", new
        {
            assessedOn = "2026-09-01", reason = "x", measurements = new[] { new { metric = "body_mass", method = "digital_scale", unit = "kg", value = 1m } },
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Scenario.Read<List<PhysicalAssessmentResponse>>(await outsider.Http.GetAsync($"/api/v1/physical-assessments?clientId={client.Id}", Ct))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_trainers_record_physical_assessments_and_units_must_be_known()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var (dietitian, _) = await Scenario.ProfessionalAsync("dietitian", Discipline.Dietitian);
        await Scenario.LinkAsync(trainer, client);
        await Scenario.LinkAsync(dietitian, client, Discipline.Dietitian);

        (await dietitian.Http.PostAsJsonAsync("/api/v1/physical-assessments", Assessment(client.Id, "2026-09-01", 20m), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var badUnit = new { clientId = client.Id, assessedOn = "2026-09-01", measurements = new[] { new { metric = "body_mass", method = "scale", unit = "lb", value = 180m } } };
        var response = await trainer.Http.PostAsJsonAsync("/api/v1/physical-assessments", badUnit, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Measurement.UnknownUnit");
    }

    [Fact]
    public async Task My_progress_keeps_measurement_methods_apart_and_shows_every_week_without_estimates()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        await Scenario.EnsureSuccess(await trainer.Http.PostAsJsonAsync("/api/v1/physical-assessments", Assessment(client.Id, "2026-08-03", 22m, "bioimpedance"), Ct));
        await Scenario.EnsureSuccess(await trainer.Http.PostAsJsonAsync("/api/v1/physical-assessments", Assessment(client.Id, "2026-08-31", 18m, "skinfold_jackson_pollock_7"), Ct));
        var plan = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, plan);

        var progress = await Scenario.Read<MyProgressResponse>(await client.Http.GetAsync("/api/v1/me/progress?from=2026-08-01&to=2026-08-31", Ct));

        progress.Measurements.Where(s => s.Metric == "body_fat_percentage").Select(s => s.Method)
            .ShouldBe(["bioimpedance", "skinfold_jackson_pollock_7"], ignoreOrder: true);
        progress.Measurements.Single(s => s.Metric == "body_mass").Points.Count.ShouldBe(2);
        progress.Measurements.SelectMany(s => s.Points).ShouldAllBe(p => p.Source == "physical-assessment" && p.AssessorName == "trainer Pro");

        // 1 Aug 2026 is a Saturday: weeks are clipped to the period and empty ones are listed with zero, not skipped.
        progress.Weeks[0].From.ShouldBe(new DateOnly(2026, 8, 1));
        progress.Weeks[0].To.ShouldBe(new DateOnly(2026, 8, 2));
        progress.Weeks[^1].To.ShouldBe(new DateOnly(2026, 8, 31));
        progress.Weeks.Sum(w => w.CompletedSessions).ShouldBe(0);
        progress.Goals.ShouldContain(g => g.Source == "training-plan" && g.Description == "Força");

        // The default period (last 12 weeks) includes today's workout and its self-reported load.
        var (session, planned) = await Scenario.StartAsync(client, plan);
        var exercise = planned.Session.Exercises[0];
        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync($"/api/v1/workout-sessions/{session}/sets/{Guid.CreateVersion7()}", new
        {
            exerciseId = exercise.ExerciseId, plannedExerciseId = exercise.Id, setNumber = 1, repetitions = 8, loadKg = 42.5m,
            loggedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
        }, Ct));
        await Scenario.CompleteAsync(client, session);

        var recent = await Scenario.Read<MyProgressResponse>(await client.Http.GetAsync("/api/v1/me/progress", Ct));
        (recent.CompletedSessions, recent.ActiveDays).ShouldBe((1, 1));
        var lift = recent.Exercises.ShouldHaveSingleItem();
        (lift.ExerciseId, lift.Sessions, lift.BestLoadKg).ShouldBe((exercise.ExerciseId, 1, 42.5m));
        lift.Name.ShouldNotBeNullOrEmpty();
    }
}
