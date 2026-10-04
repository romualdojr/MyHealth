using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Notifications;
using MyHealth.Application.Nutrition;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;

namespace MyHealth.Api.IntegrationTests;

/// <summary>Nutrition module (RF-28 → RF-30): acceptance tests 6 and 7 (nutrition part).</summary>
public sealed class NutritionTests(ApiFactory factory) : IntegrationTest(factory)
{
    private async Task<(TestUser Client, TestUser Dietitian, Guid RelationshipId)> LinkedDietitianAsync()
    {
        var client = await Scenario.ClientAsync();
        var (dietitian, _) = await Scenario.ProfessionalAsync("dietitian", Discipline.Dietitian);
        var relationshipId = await Scenario.LinkAsync(dietitian, client, Discipline.Dietitian);
        return (client, dietitian, relationshipId);
    }

    [Fact]
    public async Task Acceptance_6_dietitian_revises_a_plan_client_sees_new_version_and_history_and_trainer_cannot_see_the_diary()
    {
        // A clinic where the client sees both a trainer and a dietitian.
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync(ownTenant: false);
        var (dietitian, _) = await Scenario.ProfessionalAsync("dietitian", Discipline.Dietitian, ownTenant: false);
        var clinic = await Scenario.CreateTenantAsync(trainer, "Clínica Vida", TenantKind.Organization, Discipline.PersonalTrainer);
        Scenario.UseTenant(trainer, clinic);
        await Scenario.AddMemberAsync(trainer, dietitian, Discipline.Dietitian);
        Scenario.UseTenant(dietitian, clinic);
        var trainerRelationship = await Scenario.LinkAsync(trainer, client);
        await Scenario.LinkAsync(dietitian, client, Discipline.Dietitian);

        var planId = await Scenario.DraftMealPlanAsync(dietitian, client.Id);
        await Scenario.PublishMealPlanAsync(dietitian, planId);
        var v1 = await Scenario.Read<ClientMealPlanResponse>(await client.Http.GetAsync($"/api/v1/me/meal-plans/{planId}", Ct));
        var breakfast = v1.Current.Content.Meals[0];

        // The client logs breakfast against version 1, with the plan's own meal id.
        var entryId = Guid.CreateVersion7();
        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync($"/api/v1/me/food-diary/{entryId}", new
        {
            mealPlanId = planId, plannedMealId = breakfast.Id, eatenAt = DateTimeOffset.UtcNow.AddHours(-1), adherence = "Partly",
            description = "Comi só o pão",
        }, Ct));

        // The dietitian revises the plan; the meal keeps its id so the diary stays linked.
        var draft = await Scenario.Read<DietitianMealPlanResponse>(await dietitian.Http.GetAsync($"/api/v1/meal-plans/{planId}", Ct));
        await Scenario.EnsureSuccess(await dietitian.Http.PutAsJsonAsync($"/api/v1/meal-plans/{planId}/meals", new object[]
        {
            new { id = breakfast.Id, name = breakfast.Name, time = "07:30:00", items = new[] { new { food = "Aveia", quantity = 30m, unit = "g" } } },
            new { id = (Guid?)draft.Draft.Meals[1].Id, name = "Almoço", time = "12:00:00", items = new[] { new { food = "Arroz", quantity = 100m, unit = "g" } } },
        }, Ct));
        await Scenario.PublishMealPlanAsync(dietitian, planId, "Troca do café da manhã");

        var v2 = await Scenario.Read<ClientMealPlanResponse>(await client.Http.GetAsync($"/api/v1/me/meal-plans/{planId}", Ct));
        v2.Current.VersionNumber.ShouldBe(2);
        v2.Current.Content.Meals[0].Items.ShouldHaveSingleItem().Food.ShouldBe("Aveia");
        v2.History.Select(h => h.VersionNumber).ShouldBe([2, 1]);
        v2.History.ShouldAllBe(h => h.AuthorName == "dietitian Pro");
        var oldVersion = await Scenario.Read<MealPlanVersionResponse>(await client.Http.GetAsync($"/api/v1/meal-plans/{planId}/versions/1", Ct));
        oldVersion.Content.Meals[0].Items[0].Food.ShouldBe("Pão integral");

        // Publishing notified the client each time.
        var notifications = await Scenario.Read<List<NotificationResponse>>(await client.Http.GetAsync("/api/v1/me/notifications", Ct));
        notifications.Count(n => n.Type == NotificationType.MealPlanPublished && n.ResourceId == planId).ShouldBe(2);

        // The trainer has no route to the diary: not as a dietitian, and not through the summary without a grant.
        (await trainer.Http.GetAsync($"/api/v1/dietitian/clients/{client.Id}/food-diary?from=2026-01-01&to=2026-03-01", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await trainer.Http.GetAsync($"/api/v1/care-relationships/{trainerRelationship}/nutrition-summary", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // With the client's grant the trainer sees counts only, never what was eaten.
        await Scenario.ShareAsync(client, trainerRelationship, DataCategory.NutritionSummary);
        var summaryResponse = await trainer.Http.GetAsync($"/api/v1/care-relationships/{trainerRelationship}/nutrition-summary", Ct);
        var raw = await summaryResponse.Content.ReadAsStringAsync(Ct);
        var summary = await Scenario.Read<NutritionSummaryResponse>(summaryResponse);
        summary.ActivePlans.ShouldHaveSingleItem().VersionNumber.ShouldBe(2);
        summary.Last7Days.Partly.ShouldBe(1);
        raw.ShouldNotContain("Comi só o pão");
        raw.ShouldNotContain("Aveia");

        // The dietitian sees the entry, linked to version 1 and the same meal.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var diary = await Scenario.Read<DietitianFoodDiaryResponse>(await dietitian.Http.GetAsync(
            $"/api/v1/dietitian/clients/{client.Id}/food-diary?from={today.AddDays(-2):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}", Ct));
        var entry = diary.Entries.ShouldHaveSingleItem();
        (entry.MealPlanVersionNumber, entry.PlannedMealId).ShouldBe((1, breakfast.Id));
        diary.Summary.Partly.ShouldBe(1);
    }

    [Fact]
    public async Task Acceptance_7_unverified_dietitian_cannot_prescribe_and_trainers_cannot_prescribe_diets()
    {
        var client = await Scenario.ClientAsync();
        var (unverified, _) = await Scenario.ProfessionalAsync("pending-dietitian", Discipline.Dietitian, verified: false);
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);

        var draft = new { clientId = client.Id, details = new { name = "Plano", startDate = "2026-10-01" } };
        (await unverified.Http.PostAsJsonAsync("/api/v1/meal-plans", draft, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await trainer.Http.PostAsJsonAsync("/api/v1/meal-plans", draft, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await trainer.Http.PostAsJsonAsync("/api/v1/nutrition-assessments", new { clientId = client.Id, assessment = new { assessedOn = "2026-09-30" } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_dietitian_whose_verification_is_suspended_can_no_longer_publish()
    {
        var client = await Scenario.ClientAsync();
        var (dietitian, profileId) = await Scenario.ProfessionalAsync("dietitian", Discipline.Dietitian);
        var relationshipId = await Scenario.LinkAsync(dietitian, client, Discipline.Dietitian);
        var planId = await Scenario.DraftMealPlanAsync(dietitian, client.Id);

        var admin = Scenario.Factory.NewUser("admin", administrator: true);
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync($"/api/v1/admin/professional-profiles/{profileId}/suspend", new { reason = "Registro vencido" }, Ct));

        var response = await dietitian.Http.PostAsJsonAsync($"/api/v1/meal-plans/{planId}/publish", new { changeDescription = "v1" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.Http.GetAsync($"/api/v1/me/meal-plans/{planId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Suspension stops reads too, not only new prescriptions.
        (await dietitian.Http.GetAsync($"/api/v1/meal-plans/{planId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await dietitian.Http.GetAsync($"/api/v1/nutrition-assessments?clientId={client.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await dietitian.Http.GetAsync($"/api/v1/dietitian/clients/{client.Id}/food-diary?from=2026-09-01&to=2026-09-30", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await Scenario.ShareAsync(client, relationshipId, DataCategory.PhysicalAssessments);
        (await dietitian.Http.GetAsync($"/api/v1/care-relationships/{relationshipId}/physical-assessments", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Nutrition_assessment_is_recorded_corrected_with_history_and_visible_to_the_client()
    {
        var (client, dietitian, _) = await LinkedDietitianAsync();

        var created = await dietitian.Http.PostAsJsonAsync("/api/v1/nutrition-assessments", new
        {
            clientId = client.Id,
            assessment = new
            {
                assessedOn = "2026-09-29", goals = "Mais energia", reportedAllergies = "Amendoim", mealTimes = "7h, 12h, 19h",
                measurements = new[] { new { metric = "body_mass", method = "digital_scale", unit = "kg", value = 72.4m } },
            },
        }, Ct);
        var id = (await Scenario.Read<Scenario.IdResponse>(created)).Id;

        await Scenario.EnsureSuccess(await dietitian.Http.PostAsJsonAsync($"/api/v1/nutrition-assessments/{id}/corrections", new
        {
            reason = "Peso digitado errado",
            assessment = new
            {
                assessedOn = "2026-09-29", goals = "Mais energia", reportedAllergies = "Amendoim",
                measurements = new[] { new { metric = "body_mass", method = "digital_scale", unit = "kg", value = 74.2m } },
            },
        }, Ct));

        var mine = await Scenario.Read<List<NutritionAssessmentResponse>>(await client.Http.GetAsync("/api/v1/me/nutrition-assessments", Ct));
        var assessment = mine.ShouldHaveSingleItem();
        assessment.Version.ShouldBe(2);
        assessment.Content.Measurements.ShouldHaveSingleItem().Value.ShouldBe(74.2m);
        var correction = assessment.Corrections.ShouldHaveSingleItem();
        correction.Reason.ShouldBe("Peso digitado errado");
        correction.PreviousContent.Measurements[0].Value.ShouldBe(72.4m);
        assessment.DietitianName.ShouldBe("dietitian Pro");

        var notifications = await Scenario.Read<List<NotificationResponse>>(await client.Http.GetAsync("/api/v1/me/notifications", Ct));
        notifications.ShouldContain(n => n.Type == NotificationType.NutritionAssessmentRecorded && n.ResourceId == id);
    }

    [Fact]
    public async Task Diary_entries_are_idempotent_private_by_default_and_commented_only_by_their_dietitian()
    {
        var (client, dietitian, relationshipId) = await LinkedDietitianAsync();
        var (otherDietitian, _) = await Scenario.ProfessionalAsync("other-dietitian", Discipline.Dietitian);
        await Scenario.LinkAsync(otherDietitian, client, Discipline.Dietitian);
        var eatenAt = DateTimeOffset.UtcNow.AddMinutes(-30);

        var privateEntry = Guid.CreateVersion7();
        var shared = Guid.CreateVersion7();
        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync($"/api/v1/me/food-diary/{privateEntry}", new { eatenAt, waterMl = 500 }, Ct));
        var body = new { relationshipId, eatenAt, description = "Pizza com amigos", adherence = "Unplanned", clientNotes = "Fiquei com fome à tarde" };
        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync($"/api/v1/me/food-diary/{shared}", body, Ct));
        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync($"/api/v1/me/food-diary/{shared}", body, Ct));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var range = $"from={today.AddDays(-2):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}";
        (await Scenario.Read<List<FoodDiaryEntryResponse>>(await client.Http.GetAsync($"/api/v1/me/food-diary?{range}", Ct))).Count.ShouldBe(2);

        var diary = await Scenario.Read<DietitianFoodDiaryResponse>(await dietitian.Http.GetAsync($"/api/v1/dietitian/clients/{client.Id}/food-diary?{range}", Ct));
        diary.Entries.ShouldHaveSingleItem().Id.ShouldBe(shared);
        (await Scenario.Read<DietitianFoodDiaryResponse>(await otherDietitian.Http.GetAsync($"/api/v1/dietitian/clients/{client.Id}/food-diary?{range}", Ct)))
            .Entries.ShouldBeEmpty();

        (await otherDietitian.Http.PostAsJsonAsync($"/api/v1/food-diary/{shared}/comments", new { message = "Oi" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await Scenario.EnsureSuccess(await dietitian.Http.PostAsJsonAsync($"/api/v1/food-diary/{shared}/comments", new { message = "Tudo bem! Vamos ajustar o lanche." }, Ct));

        var mine = await Scenario.Read<List<FoodDiaryEntryResponse>>(await client.Http.GetAsync($"/api/v1/me/food-diary?{range}", Ct));
        mine.Single(e => e.Id == shared).Comments.ShouldHaveSingleItem().AuthorName.ShouldBe("dietitian Pro");
        var notifications = await Scenario.Read<List<NotificationResponse>>(await client.Http.GetAsync("/api/v1/me/notifications", Ct));
        notifications.ShouldContain(n => n.Type == NotificationType.FoodDiaryCommented && n.ResourceId == shared);

        // Reusing an id for another destination is a conflict, not a silent move; another client's id is never touched.
        (await client.Http.PutAsJsonAsync($"/api/v1/me/food-diary/{shared}", new { eatenAt, waterMl = 200 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var stranger = await Scenario.ClientAsync("stranger");
        (await stranger.Http.PutAsJsonAsync($"/api/v1/me/food-diary/{shared}", new { relationshipId, eatenAt, waterMl = 200 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await stranger.Http.PutAsJsonAsync($"/api/v1/me/food-diary/{privateEntry}", new { eatenAt, waterMl = 200 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_dietitian_dashboard_lists_active_clients_with_plan_diary_counts_uncommented_entries_and_follow_ups()
    {
        var (ana, dietitian, relationshipId) = await LinkedDietitianAsync();
        var bia = await Scenario.ClientAsync("bia");
        await Scenario.LinkAsync(dietitian, bia, Discipline.Dietitian);

        // Ana follows a published plan, has a draft in progress, logged three meals and has a follow-up due.
        var planId = await Scenario.DraftMealPlanAsync(dietitian, ana.Id, "Reeducação");
        await Scenario.PublishMealPlanAsync(dietitian, planId);
        await Scenario.DraftMealPlanAsync(dietitian, ana.Id, "Próxima fase");
        var entries = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        var adherence = new[] { "AsPlanned", "AsPlanned", "Different" };
        for (var i = 0; i < entries.Length; i++)
        {
            await Scenario.EnsureSuccess(await ana.Http.PutAsJsonAsync($"/api/v1/me/food-diary/{entries[i]}", new
            {
                mealPlanId = planId, eatenAt = DateTimeOffset.UtcNow.AddDays(-i).AddHours(-1), adherence = adherence[i], description = "Refeição",
            }, Ct));
        }

        // A private entry never shows up, not even as a count.
        await Scenario.EnsureSuccess(await ana.Http.PutAsJsonAsync($"/api/v1/me/food-diary/{Guid.CreateVersion7()}", new { eatenAt = DateTimeOffset.UtcNow, waterMl = 300 }, Ct));
        await Scenario.EnsureSuccess(await dietitian.Http.PostAsJsonAsync($"/api/v1/food-diary/{entries[0]}/comments", new { message = "Muito bem!" }, Ct));
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        await Scenario.EnsureSuccess(await dietitian.Http.PostAsJsonAsync("/api/v1/nutrition-assessments", new
        {
            clientId = ana.Id, assessment = new { assessedOn = yesterday.AddDays(-30), goals = "Mais energia", nextFollowUpOn = yesterday },
        }, Ct));

        var dashboard = await Scenario.Read<DietitianDashboardResponse>(await dietitian.Http.GetAsync("/api/v1/dietitian/dashboard", Ct));

        dashboard.Clients.Select(c => c.ClientId).ShouldBe([ana.Id, bia.Id]);
        var first = dashboard.Clients[0];
        (first.RelationshipId, first.ActivePlanId, first.ActivePlanName, first.DraftPlans).ShouldBe((relationshipId, planId, "Reeducação", 1));
        (first.EntriesLast7Days, first.DaysWithEntriesLast7Days, first.AsPlannedLast7Days, first.UncommentedEntries).ShouldBe((3, 3, 2, 2));
        (first.LastAssessedOn, first.NextFollowUpOn).ShouldBe((yesterday.AddDays(-30), yesterday));
        first.LastEntryAt.ShouldNotBeNull();

        var second = dashboard.Clients[1];
        (second.ActivePlanId, second.EntriesLast7Days, second.LastEntryAt, second.NextFollowUpOn).ShouldBe((null, 0, null, null));
        (dashboard.TotalUncommentedEntries, dashboard.TotalFollowUpsDue, dashboard.ClientsWithoutPlan).ShouldBe((2, 1, 1));

        // Trainers have no dietitian dashboard.
        var (trainer, _) = await Scenario.ProfessionalAsync();
        (await trainer.Http.GetAsync("/api/v1/dietitian/dashboard", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_dietitian_in_another_tenant_cannot_reach_the_plan_and_the_attempt_is_audited()
    {
        var (client, dietitian, _) = await LinkedDietitianAsync();
        var planId = await Scenario.DraftMealPlanAsync(dietitian, client.Id);
        await Scenario.PublishMealPlanAsync(dietitian, planId);

        var (outsider, _) = await Scenario.ProfessionalAsync("outsider", Discipline.Dietitian);
        await Scenario.LinkAsync(outsider, client, Discipline.Dietitian);

        (await outsider.Http.GetAsync($"/api/v1/meal-plans/{planId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await outsider.Http.PostAsJsonAsync($"/api/v1/meal-plans/{planId}/end", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await outsider.Http.GetAsync($"/api/v1/meal-plans/{planId}/versions/1", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The owning dietitian's reads are audited, and stop when the client ends the relationship.
        await Scenario.EnsureSuccess(await dietitian.Http.GetAsync($"/api/v1/meal-plans/{planId}/versions/1", Ct));
        var relationships = await Scenario.Read<List<MyHealth.Application.CareRelationships.CareRelationshipResponse>>(
            await client.Http.GetAsync("/api/v1/care-relationships", Ct));
        var own = relationships.Single(r => r.ProfessionalName == "dietitian Pro").Id;
        await Scenario.EnsureSuccess(await client.Http.PostAsJsonAsync($"/api/v1/care-relationships/{own}/end", new { reason = "Mudei de nutricionista" }, Ct));
        (await dietitian.Http.GetAsync($"/api/v1/meal-plans/{planId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await dietitian.Http.GetAsync($"/api/v1/meal-plans/{planId}/versions/1", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.Http.GetAsync($"/api/v1/meal-plans/{planId}/versions/1", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var admin = Scenario.Factory.NewUser("auditor", administrator: true);
        var audit = await Scenario.Read<MyHealth.Application.Common.PagedResponse<MyHealth.Application.Auditing.AuditEntryResponse>>(
            await admin.Http.GetAsync($"/api/v1/admin/audit-entries?actorUserId={outsider.Id}", Ct));
        audit.Items.ShouldContain(e => e.Action == "deny" && e.ResourceId == planId && e.SubjectUserId == null);
        var dietitianAudit = await Scenario.Read<MyHealth.Application.Common.PagedResponse<MyHealth.Application.Auditing.AuditEntryResponse>>(
            await admin.Http.GetAsync($"/api/v1/admin/audit-entries?actorUserId={dietitian.Id}", Ct));
        dietitianAudit.Items.ShouldContain(e => e.Action == "read" && e.ResourceType == "MealPlanVersion" && e.SubjectUserId == client.Id);
    }

    [Fact]
    public async Task Meal_plan_items_need_a_portion_and_meals_need_foods_before_publishing()
    {
        var (client, dietitian, _) = await LinkedDietitianAsync();
        var create = await dietitian.Http.PostAsJsonAsync("/api/v1/meal-plans", new { clientId = client.Id, details = new { name = "Plano", startDate = "2026-10-01" } }, Ct);
        var planId = (await Scenario.Read<Scenario.IdResponse>(create)).Id;

        (await dietitian.Http.PutAsJsonAsync($"/api/v1/meal-plans/{planId}/meals", new[] { new { name = "Lanche", items = new[] { new { food = "Fruta" } } } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await Scenario.EnsureSuccess(await dietitian.Http.PutAsJsonAsync($"/api/v1/meal-plans/{planId}/meals", new[] { new { name = "Lanche", items = Array.Empty<object>() } }, Ct));
        (await dietitian.Http.PostAsJsonAsync($"/api/v1/meal-plans/{planId}/publish", new { changeDescription = "v1" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_client_lists_published_meal_plans_labelled_by_organization_but_never_drafts()
    {
        var (client, dietitian, _) = await LinkedDietitianAsync();
        var published = await Scenario.DraftMealPlanAsync(dietitian, client.Id, "Plano publicado");
        await Scenario.PublishMealPlanAsync(dietitian, published);
        await Scenario.DraftMealPlanAsync(dietitian, client.Id, "Rascunho");

        var plans = await Scenario.Read<List<ClientMealPlanListItem>>(await client.Http.GetAsync("/api/v1/me/meal-plans", Ct));

        var plan = plans.ShouldHaveSingleItem();
        (plan.PlanId, plan.Name, plan.VersionNumber, plan.Status).ShouldBe((published, "Plano publicado", 1, MealPlanStatus.Published));
        (plan.OrganizationName, plan.DietitianName).ShouldBe(("dietitian Pro", "dietitian Pro"));
        (await Scenario.Read<List<ClientMealPlanListItem>>(await (await Scenario.ClientAsync("other")).Http.GetAsync("/api/v1/me/meal-plans", Ct))).ShouldBeEmpty();
    }

    [Fact]
    public async Task One_assessment_is_readable_by_its_client_and_author_only_and_professional_reads_are_audited()
    {
        var (client, dietitian, _) = await LinkedDietitianAsync();
        var created = await dietitian.Http.PostAsJsonAsync("/api/v1/nutrition-assessments", new
        {
            clientId = client.Id, assessment = new { assessedOn = "2026-09-28", goals = "Mais energia" },
        }, Ct);
        var id = (await Scenario.Read<Scenario.IdResponse>(created)).Id;

        (await Scenario.Read<NutritionAssessmentResponse>(await client.Http.GetAsync($"/api/v1/nutrition-assessments/{id}", Ct))).Content.Goals.ShouldBe("Mais energia");
        (await Scenario.Read<NutritionAssessmentResponse>(await dietitian.Http.GetAsync($"/api/v1/nutrition-assessments/{id}", Ct))).DietitianName.ShouldBe("dietitian Pro");

        // Another dietitian of the same client, in another organization, and an unrelated client get nothing.
        var (outsider, _) = await Scenario.ProfessionalAsync("outsider", Discipline.Dietitian);
        await Scenario.LinkAsync(outsider, client, Discipline.Dietitian);
        (await outsider.Http.GetAsync($"/api/v1/nutrition-assessments/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await Scenario.ClientAsync("other")).Http.GetAsync($"/api/v1/nutrition-assessments/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var admin = Scenario.Factory.NewUser("auditor", administrator: true);
        var audit = await Scenario.Read<MyHealth.Application.Common.PagedResponse<MyHealth.Application.Auditing.AuditEntryResponse>>(
            await admin.Http.GetAsync($"/api/v1/admin/audit-entries?subjectUserId={client.Id}&pageSize=200", Ct));
        audit.Items.ShouldContain(e => e.Action == "read" && e.ResourceId == id && e.ActorUserId == dietitian.Id);
        var denied = await Scenario.Read<MyHealth.Application.Common.PagedResponse<MyHealth.Application.Auditing.AuditEntryResponse>>(
            await admin.Http.GetAsync($"/api/v1/admin/audit-entries?actorUserId={outsider.Id}", Ct));
        denied.Items.ShouldContain(e => e.Action == "deny" && e.ResourceId == id && e.SubjectUserId == null);
    }
}
