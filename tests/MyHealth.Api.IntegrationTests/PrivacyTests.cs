using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Auditing;
using MyHealth.Application.Common;
using MyHealth.Application.Notifications;
using MyHealth.Application.Privacy;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Privacy;
using MyHealth.Domain.Professionals;

namespace MyHealth.Api.IntegrationTests;

/// <summary>Data export and LGPD requests (RNF-04, RF-50): acceptance test 9.</summary>
public sealed class PrivacyTests(ApiFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Acceptance_9_export_contains_own_records_with_provenance_no_third_party_data_and_request_and_delivery_are_audited()
    {
        var client = await Scenario.ClientAsync("ana");
        var otherClient = await Scenario.ClientAsync("bruno");
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        await Scenario.LinkAsync(trainer, otherClient);

        var plan = await Scenario.DraftPlanAsync(trainer, client.Id, "Plano da Ana");
        await Scenario.PublishAsync(trainer, plan);
        var (session, _) = await Scenario.StartAsync(client, plan);
        await Scenario.CompleteAsync(client, session);
        var otherPlan = await Scenario.DraftPlanAsync(trainer, otherClient.Id, "Plano do Bruno");
        await Scenario.PublishAsync(trainer, otherPlan);
        await Scenario.EnsureSuccess(await trainer.Http.PostAsJsonAsync("/api/v1/physical-assessments", new
        {
            clientId = client.Id, assessedOn = "2026-09-20",
            measurements = new[] { new { metric = "body_mass", method = "digital_scale", unit = "kg", value = 61.3m } },
        }, Ct));
        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync("/api/v1/me/health-history", new { goals = "Ganhar força", acknowledged = true }, Ct));

        var export = await Scenario.Read<DataExportResponse>(await client.Http.PostAsync("/api/v1/me/data-exports", null, Ct));
        var download = await client.Http.GetAsync(export.DownloadPath, Ct);
        await Scenario.EnsureSuccess(download);
        download.Content.Headers.ContentDisposition!.FileName.ShouldNotBeNull().ShouldContain("myhealth-export");
        var raw = await download.Content.ReadAsStringAsync(Ct);
        var package = System.Text.Json.JsonSerializer.Deserialize<DataExportPackage>(raw, Scenario.Json)!;

        package.Subject.UserId.ShouldBe(client.Id);
        package.TrainingPlans.ShouldHaveSingleItem().Details.Name.ShouldBe("Plano da Ana");
        package.TrainingPlans[0].TrainerName.ShouldBe("trainer Pro");
        package.TrainingPlans[0].OrganizationName.ShouldBe("trainer Pro");
        var workout = package.WorkoutSessions.ShouldHaveSingleItem();
        workout.Id.ShouldBe(session);
        workout.Feedback.ShouldNotBeNull().PerceivedExertion.ShouldBe(7);
        var assessment = package.PhysicalAssessments.ShouldHaveSingleItem();
        (assessment.AssessorName, assessment.Measurements[0].Method).ShouldBe(("trainer Pro", "digital_scale"));
        package.HealthHistory.ShouldNotBeNull().Revisions.ShouldHaveSingleItem().Content.Goals.ShouldBe("Ganhar força");
        package.CareRelationships.ShouldHaveSingleItem().ProfessionalName.ShouldBe("trainer Pro");
        package.AccessLog.ShouldContain(e => e.Action == "request" && e.Actor == "you");

        // No third-party data: nothing about the other client of the same trainer, and no professional's account id.
        raw.ShouldNotContain(otherClient.Id.ToString());
        raw.ShouldNotContain("Plano do Bruno");
        raw.ShouldNotContain(otherClient.Email);
        raw.ShouldNotContain(trainer.Id.ToString());

        // The audit trail records the request and the delivery.
        var admin = Scenario.Factory.NewUser("auditor", administrator: true);
        var audit = await Scenario.Read<PagedResponse<AuditEntryResponse>>(await admin.Http.GetAsync($"/api/v1/admin/audit-entries?subjectUserId={client.Id}", Ct));
        audit.Items.ShouldContain(e => e.Action == "request" && e.ResourceType == "DataExport" && e.ResourceId == export.Id);
        audit.Items.ShouldContain(e => e.Action == "deliver" && e.ResourceType == "DataExport" && e.ResourceId == export.Id);

        // Operators see that it was requested and delivered, never its contents; nobody else can download it.
        var queue = await Scenario.Read<List<AdminPrivacyRequestResponse>>(await admin.Http.GetAsync("/api/v1/admin/privacy-requests?type=DataExport", Ct));
        queue.ShouldContain(r => r.Id == export.Id && r.DeliveryCount == 1);
        (await otherClient.Http.GetAsync(export.DownloadPath, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await trainer.Http.GetAsync(export.DownloadPath, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_professionals_own_export_does_not_include_their_clients_records()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, profileId) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        var plan = await Scenario.DraftPlanAsync(trainer, client.Id, "Plano do cliente");
        await Scenario.PublishAsync(trainer, plan);

        var export = await Scenario.Read<DataExportResponse>(await trainer.Http.PostAsync("/api/v1/me/data-exports", null, Ct));
        var raw = await (await trainer.Http.GetAsync(export.DownloadPath, Ct)).Content.ReadAsStringAsync(Ct);
        var package = System.Text.Json.JsonSerializer.Deserialize<DataExportPackage>(raw, Scenario.Json)!;

        package.ProfessionalProfiles.ShouldHaveSingleItem().Id.ShouldBe(profileId);
        package.Memberships.ShouldHaveSingleItem().IsAdministrator.ShouldBeTrue();
        package.CareRelationships.ShouldBeEmpty();
        package.TrainingPlans.ShouldBeEmpty();
        raw.ShouldNotContain(client.Id.ToString());
        raw.ShouldNotContain("Plano do cliente");
    }

    [Fact]
    public async Task Correction_and_deletion_requests_are_handled_by_operators_and_the_user_is_notified()
    {
        var client = await Scenario.ClientAsync();
        var admin = Scenario.Factory.NewUser("operator", administrator: true);

        (await client.Http.PostAsJsonAsync("/api/v1/me/privacy-requests", new { type = "Deletion", details = "" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var deletion = (await Scenario.Read<Scenario.IdResponse>(await client.Http.PostAsJsonAsync(
            "/api/v1/me/privacy-requests", new { type = "Deletion", details = "Apaguem meu diário alimentar" }, Ct))).Id;

        await Scenario.EnsureSuccess(await admin.Http.PostAsync($"/api/v1/admin/privacy-requests/{deletion}/start", null, Ct));
        (await admin.Http.PostAsJsonAsync($"/api/v1/admin/privacy-requests/{deletion}/reject", new { notes = "" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync(
            $"/api/v1/admin/privacy-requests/{deletion}/reject", new { notes = "Registros sob prazo legal de guarda" }, Ct));
        (await admin.Http.PostAsync($"/api/v1/admin/privacy-requests/{deletion}/complete", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var mine = await Scenario.Read<List<PrivacyRequestResponse>>(await client.Http.GetAsync("/api/v1/me/privacy-requests", Ct));
        var request = mine.ShouldHaveSingleItem();
        (request.Status, request.ResolutionNotes).ShouldBe((PrivacyRequestStatus.Rejected, "Registros sob prazo legal de guarda"));

        var notifications = await Scenario.Read<List<NotificationResponse>>(await client.Http.GetAsync("/api/v1/me/notifications", Ct));
        notifications.Count(n => n.Type == NotificationType.PrivacyRequestUpdated && n.ResourceId == deletion).ShouldBe(2);

        // Clients cannot reach the operators' queue.
        (await client.Http.GetAsync("/api/v1/admin/privacy-requests", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Retention_policies_are_configured_per_record_type()
    {
        var admin = Scenario.Factory.NewUser("operator", administrator: true);

        (await admin.Http.PutAsJsonAsync("/api/v1/admin/retention-policies/Unknown", new { retentionMonths = 12, legalBasis = "x" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Scenario.EnsureSuccess(await admin.Http.PutAsJsonAsync("/api/v1/admin/retention-policies/PhysicalAssessment", new
        {
            retentionMonths = 60, legalBasis = "Tutela da saúde (LGPD art. 11, II, f)", notes = "Prazo provisório até parecer jurídico",
        }, Ct));
        await Scenario.EnsureSuccess(await admin.Http.PutAsJsonAsync("/api/v1/admin/retention-policies/PhysicalAssessment", new
        {
            retentionMonths = 120, legalBasis = "Tutela da saúde (LGPD art. 11, II, f)",
        }, Ct));

        var policies = await Scenario.Read<List<RetentionPolicyResponse>>(await admin.Http.GetAsync("/api/v1/admin/retention-policies", Ct));
        policies.Count.ShouldBe(RetentionPolicy.RecordTypes.Count);
        policies.Single(p => p.RecordType == "PhysicalAssessment").RetentionMonths.ShouldBe(120);
    }

    [Fact]
    public async Task Exports_are_available_for_a_week_and_unknown_exports_are_not_found()
    {
        var (dietitian, _) = await Scenario.ProfessionalAsync("dietitian", Discipline.Dietitian);
        var stranger = await Scenario.ClientAsync("stranger");

        (await stranger.Http.GetAsync($"/api/v1/me/data-exports/{Guid.CreateVersion7()}/download", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var export = await Scenario.Read<DataExportResponse>(await dietitian.Http.PostAsync("/api/v1/me/data-exports", null, Ct));
        export.ExpiresAt.ShouldBe(export.RequestedAt + PrivacyRequest.ExportAvailability);
    }
}
