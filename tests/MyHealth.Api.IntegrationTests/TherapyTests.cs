using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Auditing;
using MyHealth.Application.Common;
using MyHealth.Application.Notifications;
using MyHealth.Application.Privacy;
using MyHealth.Application.Therapies;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Therapies;

namespace MyHealth.Api.IntegrationTests;

/// <summary>Integrative therapies (RF-33 → RF-36): acceptance tests 7 (therapy part) and 8.</summary>
public sealed class TherapyTests(ApiFactory factory) : IntegrationTest(factory)
{
    private const string Council = "COFFITO";

    private static readonly object CompleteRecord = new
    {
        reportedConcern = "Dor lombar há 3 semanas",
        professionalAssessment = "Tensão paravertebral; sem sinais de alerta",
        contraindicationsAndPrecautions = "Sem anticoagulantes; sem gestação",
        technique = "Acupuntura sistêmica, agulhas descartáveis",
        pointsOrRegions = "BL23, BL25, GB30",
        durationMinutes = 30,
        adverseEvents = "Nenhum",
        postSessionGuidance = "Hidratar-se e evitar esforço intenso por 24 h",
    };

    private TestUser Admin() => Scenario.Factory.NewUser("operator", administrator: true);

    /// <summary>A modality configured like acupuncture, with consent text published and enabled after "legal review".</summary>
    private async Task<string> EnabledModalityAsync(params string[] councils)
    {
        var admin = Admin();
        var code = "acupuntura_" + new string(Enumerable.Range(0, 10).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync("/api/v1/admin/therapy-modalities", new
        {
            code,
            modality = new
            {
                name = "Acupuntura (teste)", acceptedCouncils = councils.Length == 0 ? new[] { Council } : councils,
                requiredRecordFields = TherapyModality.AcupunctureDefaultRequiredFields.Select(f => f.ToString()).ToArray(), requiresConsent = true,
            },
        }, Ct));
        await PublishConsentAsync(code, "Riscos: dor local, pequeno sangramento, hematoma");
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync($"/api/v1/admin/therapy-modalities/{code}/enable", new { legalReviewReference = "Parecer 12/2026" }, Ct));
        return code;
    }

    private async Task PublishConsentAsync(string code, string risk) =>
        await Scenario.EnsureSuccess(await Admin().Http.PostAsJsonAsync($"/api/v1/admin/therapy-modalities/{code}/consent-templates", new
        {
            title = "Termo de consentimento — acupuntura", body = "Declaro que fui informado(a) sobre o procedimento.", disclosedRisks = new[] { risk, "Desmaio (raro)" },
        }, Ct));

    private async Task<(TestUser User, Guid ProfileId, Guid QualificationId)> PractitionerAsync(
        string modality, string name = "practitioner", bool verified = true, bool qualified = true, string council = Council)
    {
        var (user, profileId) = await Scenario.ProfessionalAsync(name, Discipline.TherapyPractitioner, verified, council: council);
        var submitted = await user.Http.PostAsJsonAsync("/api/v1/therapy/qualifications", new
        {
            modalityCode = modality, title = "Especialização em Acupuntura", issuer = "Instituto X", certificateNumber = "123",
        }, Ct);
        var qualificationId = (await Scenario.Read<Scenario.IdResponse>(submitted)).Id;
        if (qualified)
        {
            await Scenario.EnsureSuccess(await Admin().Http.PostAsJsonAsync($"/api/v1/admin/practitioner-qualifications/{qualificationId}/verify", new { notes = "ok" }, Ct));
        }

        return (user, profileId, qualificationId);
    }

    private async Task<Guid> ServiceAsync(TestUser practitioner, string modality) =>
        (await Scenario.Read<Scenario.IdResponse>(await practitioner.Http.PostAsJsonAsync("/api/v1/therapy/services", new
        {
            modalityCode = modality, service = new { name = "Sessão de acupuntura", durationMinutes = 50, delivery = "InPerson" },
        }, Ct))).Id;

    private async Task<Guid> ScheduleAsync(TestUser practitioner, Guid clientId, Guid serviceId, DateTimeOffset? startsAt = null) =>
        (await Scenario.Read<Scenario.IdResponse>(await practitioner.Http.PostAsJsonAsync("/api/v1/therapy/sessions", new
        {
            clientId, serviceId, startsAt = startsAt ?? DateTimeOffset.UtcNow.AddMinutes(-10),
        }, Ct))).Id;

    private async Task<Guid> ConsentAsync(TestUser client, Guid relationshipId, string modality, string decision = "Granted")
    {
        var template = await Scenario.Read<ConsentTemplateResponse>(await client.Http.GetAsync($"/api/v1/therapy/modalities/{modality}/consent", Ct));
        return (await Scenario.Read<Scenario.IdResponse>(await client.Http.PostAsJsonAsync("/api/v1/me/therapy-consents", new
        {
            relationshipId, modalityCode = modality, templateVersion = template.Version, decision,
        }, Ct))).Id;
    }

    private async Task<(TestUser Client, TestUser Practitioner, Guid ProfileId, Guid RelationshipId, string Modality, Guid ServiceId)> ReadyAsync()
    {
        var modality = await EnabledModalityAsync();
        var (practitioner, profileId, _) = await PractitionerAsync(modality);
        var client = await Scenario.ClientAsync();
        var relationshipId = await Scenario.LinkAsync(practitioner, client, Discipline.TherapyPractitioner);
        return (client, practitioner, profileId, relationshipId, modality, await ServiceAsync(practitioner, modality));
    }

    [Fact]
    public async Task Acupuncture_is_seeded_disabled_and_only_a_legal_review_reference_and_permitted_councils_enable_a_modality()
    {
        var admin = Admin();
        var modalities = await Scenario.Read<List<ModalityResponse>>(await admin.Http.GetAsync("/api/v1/admin/therapy-modalities", Ct));
        var acupuncture = modalities.Single(m => m.Code == TherapyModality.Acupuncture);
        (acupuncture.Status, acupuncture.AcceptedCouncils.Count, acupuncture.RequiresConsent).ShouldBe((ModalityStatus.Disabled, 0, true));
        acupuncture.RequiredRecordFields.ShouldNotContain(TherapyRecordField.PointsOrRegions);

        // Practitioners may already submit their training, but cannot offer a disabled modality.
        var (practitioner, _, _) = await PractitionerAsync(TherapyModality.Acupuncture, qualified: false);
        (await practitioner.Http.PostAsJsonAsync("/api/v1/therapy/services", new
        {
            modalityCode = TherapyModality.Acupuncture, service = new { name = "Sessão", durationMinutes = 50, delivery = "InPerson" },
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Scenario.Read<List<ModalityResponse>>(await practitioner.Http.GetAsync("/api/v1/therapy/modalities", Ct)))
            .ShouldNotContain(m => m.Code == TherapyModality.Acupuncture);

        // Enabling needs accepted councils and a legal review reference.
        var code = "gate_" + new string(Enumerable.Range(0, 10).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync("/api/v1/admin/therapy-modalities", new
        {
            code, modality = new { name = "Gate", acceptedCouncils = Array.Empty<string>(), requiredRecordFields = Array.Empty<string>(), requiresConsent = false },
        }, Ct));
        (await admin.Http.PostAsJsonAsync($"/api/v1/admin/therapy-modalities/{code}/enable", new { legalReviewReference = "Parecer 1" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Scenario.EnsureSuccess(await admin.Http.PutAsJsonAsync($"/api/v1/admin/therapy-modalities/{code}", new
        {
            name = "Gate", acceptedCouncils = new[] { "cfm" }, requiredRecordFields = Array.Empty<string>(), requiresConsent = false,
        }, Ct));
        (await admin.Http.PostAsJsonAsync($"/api/v1/admin/therapy-modalities/{code}/enable", new { legalReviewReference = " " }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync($"/api/v1/admin/therapy-modalities/{code}/enable", new { legalReviewReference = "Parecer 1" }, Ct));

        // Changing who may practise sends it back for review.
        await Scenario.EnsureSuccess(await admin.Http.PutAsJsonAsync($"/api/v1/admin/therapy-modalities/{code}", new
        {
            name = "Gate", acceptedCouncils = new[] { "CFM", "COFFITO" }, requiredRecordFields = Array.Empty<string>(), requiresConsent = false,
        }, Ct));
        var gate = (await Scenario.Read<List<ModalityResponse>>(await admin.Http.GetAsync("/api/v1/admin/therapy-modalities", Ct))).Single(m => m.Code == code);
        gate.Status.ShouldBe(ModalityStatus.Disabled);
        gate.AcceptedCouncils.ShouldBe(["CFM", "COFFITO"]);
    }

    [Fact]
    public async Task Acceptance_7_unverified_or_unqualified_practitioners_cannot_document_procedures()
    {
        var modality = await EnabledModalityAsync();
        var client = await Scenario.ClientAsync();

        // An unverified profile can neither invite clients nor offer services.
        var (unverified, _, _) = await PractitionerAsync(modality, "unverified", verified: false);
        (await unverified.Http.PostAsJsonAsync("/api/v1/therapy/services", new
        {
            modalityCode = modality, service = new { name = "Sessão", durationMinutes = 50, delivery = "InPerson" },
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A council outside the modality's scope cannot be verified for it.
        var (outOfScope, _, outOfScopeQualification) = await PractitionerAsync(modality, "out-of-scope", qualified: false, council: "CRN");
        var verify = await Admin().Http.PostAsJsonAsync($"/api/v1/admin/practitioner-qualifications/{outOfScopeQualification}/verify", new { }, Ct);
        verify.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await verify.Content.ReadAsStringAsync(Ct)).ShouldContain("Therapy.CouncilNotAccepted");
        (await outOfScope.Http.PostAsJsonAsync("/api/v1/therapy/services", new
        {
            modalityCode = modality, service = new { name = "Sessão", durationMinutes = 50, delivery = "InPerson" },
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A qualified practitioner whose qualification is revoked stops documenting at once.
        var (practitioner, _, qualificationId) = await PractitionerAsync(modality);
        await Scenario.LinkAsync(practitioner, client, Discipline.TherapyPractitioner);
        var session = await ScheduleAsync(practitioner, client.Id, await ServiceAsync(practitioner, modality));
        await Scenario.EnsureSuccess(await Admin().Http.PostAsJsonAsync(
            $"/api/v1/admin/practitioner-qualifications/{qualificationId}/revoke", new { notes = "Certificado cancelado" }, Ct));

        var document = await practitioner.Http.PutAsJsonAsync($"/api/v1/therapy/sessions/{session}/record", CompleteRecord, Ct);
        document.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await document.Content.ReadAsStringAsync(Ct)).ShouldContain("Therapy.NotQualified");
        (await practitioner.Http.PostAsync($"/api/v1/therapy/sessions/{session}/complete", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await practitioner.Http.PostAsJsonAsync($"/api/v1/therapy/sessions/{session}/reschedule", new { startsAt = DateTimeOffset.UtcNow.AddDays(1) }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Acceptance_8_a_session_requires_consent_and_mandatory_fields_before_completion()
    {
        var (client, practitioner, _, relationshipId, modality, serviceId) = await ReadyAsync();
        var session = await ScheduleAsync(practitioner, client.Id, serviceId);

        // Nothing documented, no consent.
        var noConsent = await practitioner.Http.PostAsync($"/api/v1/therapy/sessions/{session}/complete", null, Ct);
        noConsent.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await noConsent.Content.ReadAsStringAsync(Ct)).ShouldContain("TherapySession.MissingDocumentation");

        // Partially documented: the response names what is missing.
        await Scenario.EnsureSuccess(await practitioner.Http.PutAsJsonAsync($"/api/v1/therapy/sessions/{session}/record", new
        {
            reportedConcern = "Dor lombar", technique = "Acupuntura sistêmica", durationMinutes = 30,
        }, Ct));
        var partial = await Scenario.Read<TherapySessionResponse>(await practitioner.Http.GetAsync($"/api/v1/therapy/sessions/{session}", Ct));
        partial.MissingRequiredFields.ShouldBe(
            [TherapyRecordField.ProfessionalAssessment, TherapyRecordField.ContraindicationsAndPrecautions, TherapyRecordField.AdverseEvents, TherapyRecordField.PostSessionGuidance],
            ignoreOrder: true);
        // An unfinished record is the practitioner's work in progress: the client does not see it yet.
        (await Scenario.Read<List<TherapySessionResponse>>(await client.Http.GetAsync("/api/v1/me/therapy-sessions", Ct))).ShouldHaveSingleItem().Record.ShouldBeNull();
        var missing = await practitioner.Http.PostAsync($"/api/v1/therapy/sessions/{session}/complete", null, Ct);
        (await missing.Content.ReadAsStringAsync(Ct)).ShouldContain("AdverseEvents");

        // Fully documented but the client refused consent: still blocked.
        await Scenario.EnsureSuccess(await practitioner.Http.PutAsJsonAsync($"/api/v1/therapy/sessions/{session}/record", CompleteRecord, Ct));
        await ConsentAsync(client, relationshipId, modality, "Refused");
        var refused = await practitioner.Http.PostAsync($"/api/v1/therapy/sessions/{session}/complete", null, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("TherapySession.ConsentRequired");

        // Granted on the current version: completes, and the client sees the record and guidance.
        var consentId = await ConsentAsync(client, relationshipId, modality);
        await Scenario.EnsureSuccess(await practitioner.Http.PostAsync($"/api/v1/therapy/sessions/{session}/complete", null, Ct));
        var mine = (await Scenario.Read<List<TherapySessionResponse>>(await client.Http.GetAsync("/api/v1/me/therapy-sessions", Ct))).ShouldHaveSingleItem();
        mine.Status.ShouldBe(TherapySessionStatus.Completed);
        mine.Record!.PostSessionGuidance.ShouldBe("Hidratar-se e evitar esforço intenso por 24 h");
        var notifications = await Scenario.Read<List<NotificationResponse>>(await client.Http.GetAsync("/api/v1/me/notifications", Ct));
        notifications.ShouldContain(n => n.Type == NotificationType.TherapySessionCompleted && n.ResourceId == session);

        // The consent record shows version, practitioner, disclosed risks and both decisions.
        var consents = await Scenario.Read<List<ConsentResponse>>(await client.Http.GetAsync("/api/v1/me/therapy-consents", Ct));
        consents.Count.ShouldBe(2);
        var granted = consents.Single(c => c.Id == consentId);
        (granted.TemplateVersion, granted.PractitionerName, granted.InForce).ShouldBe((1, "practitioner Pro", true));
        granted.DisclosedRisks.ShouldContain("Riscos: dor local, pequeno sangramento, hematoma");
        consents.ShouldContain(c => c.Decision == ConsentDecision.Refused);

        // A new consent version requires consenting again before the next session completes.
        await PublishConsentAsync(modality, "Riscos atualizados: dor local, hematoma, infecção (rara)");
        var next = await ScheduleAsync(practitioner, client.Id, serviceId, DateTimeOffset.UtcNow.AddMinutes(-5).AddHours(-2));
        await Scenario.EnsureSuccess(await practitioner.Http.PutAsJsonAsync($"/api/v1/therapy/sessions/{next}/record", CompleteRecord, Ct));
        (await practitioner.Http.PostAsync($"/api/v1/therapy/sessions/{next}/complete", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.Http.PostAsJsonAsync("/api/v1/me/therapy-consents", new { relationshipId, modalityCode = modality, templateVersion = 1, decision = "Granted" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var renewed = await ConsentAsync(client, relationshipId, modality);
        await Scenario.EnsureSuccess(await practitioner.Http.PostAsync($"/api/v1/therapy/sessions/{next}/complete", null, Ct));

        // Withdrawal stops further completions.
        await Scenario.EnsureSuccess(await client.Http.PostAsJsonAsync($"/api/v1/me/therapy-consents/{renewed}/withdraw", new { reason = "Prefiro parar" }, Ct));
        var third = await ScheduleAsync(practitioner, client.Id, serviceId, DateTimeOffset.UtcNow.AddHours(-5));
        await Scenario.EnsureSuccess(await practitioner.Http.PutAsJsonAsync($"/api/v1/therapy/sessions/{third}/record", CompleteRecord, Ct));
        (await practitioner.Http.PostAsync($"/api/v1/therapy/sessions/{third}/complete", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var practitionerNotifications = await Scenario.Read<List<NotificationResponse>>(await practitioner.Http.GetAsync("/api/v1/me/notifications", Ct));
        practitionerNotifications.ShouldContain(n => n.Type == NotificationType.TherapyConsentChanged && n.ResourceId == renewed);
    }

    [Fact]
    public async Task Completed_records_change_only_by_correction_and_clients_report_progress_and_receive_referrals()
    {
        var (client, practitioner, _, relationshipId, modality, serviceId) = await ReadyAsync();
        var session = await ScheduleAsync(practitioner, client.Id, serviceId);
        await ConsentAsync(client, relationshipId, modality);
        await Scenario.EnsureSuccess(await practitioner.Http.PutAsJsonAsync($"/api/v1/therapy/sessions/{session}/record", CompleteRecord, Ct));
        await Scenario.EnsureSuccess(await practitioner.Http.PostAsync($"/api/v1/therapy/sessions/{session}/complete", null, Ct));

        (await practitioner.Http.PutAsJsonAsync($"/api/v1/therapy/sessions/{session}/record", CompleteRecord, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await Scenario.EnsureSuccess(await practitioner.Http.PostAsJsonAsync($"/api/v1/therapy/sessions/{session}/record/corrections", new
        {
            reason = "Pontos registrados incorretamente",
            record = new
            {
                reportedConcern = "Dor lombar há 3 semanas", professionalAssessment = "Tensão paravertebral; sem sinais de alerta",
                contraindicationsAndPrecautions = "Sem anticoagulantes; sem gestação", technique = "Acupuntura sistêmica, agulhas descartáveis",
                pointsOrRegions = "BL23, BL40, GB30", durationMinutes = 30, adverseEvents = "Nenhum", postSessionGuidance = "Hidratar-se e evitar esforço intenso por 24 h",
            },
        }, Ct));

        await Scenario.EnsureSuccess(await client.Http.PostAsJsonAsync($"/api/v1/me/therapy-sessions/{session}/progress", new { wellbeing = 4, comments = "Menos dor" }, Ct));
        await Scenario.EnsureSuccess(await practitioner.Http.PostAsJsonAsync($"/api/v1/therapy/sessions/{session}/medical-referral", new
        {
            reason = "Relato de dormência na perna; avaliação médica recomendada", urgency = "Urgent",
        }, Ct));

        await Scenario.EnsureSuccess(await practitioner.Http.PostAsJsonAsync($"/api/v1/therapy/sessions/{session}/medical-referral", new
        {
            reason = "Retorno: manter acompanhamento médico", urgency = "Routine",
        }, Ct));

        var detail = await Scenario.Read<TherapySessionResponse>(await client.Http.GetAsync($"/api/v1/therapy/sessions/{session}", Ct));
        detail.Record!.PointsOrRegions.ShouldBe("BL23, BL40, GB30");
        var correction = detail.Corrections.ShouldHaveSingleItem();
        (correction.Previous.PointsOrRegions, correction.Reason).ShouldBe(("BL23, BL25, GB30", "Pontos registrados incorretamente"));
        detail.ClientReport!.Wellbeing.ShouldBe(4);
        detail.Referrals.Select(r => r.Urgency).ShouldBe([ReferralUrgency.Urgent, ReferralUrgency.Routine]);

        var clientNotifications = await Scenario.Read<List<NotificationResponse>>(await client.Http.GetAsync("/api/v1/me/notifications", Ct));
        clientNotifications.ShouldContain(n => n.Type == NotificationType.MedicalReferralRecommended && n.ResourceId == session);
        var practitionerNotifications = await Scenario.Read<List<NotificationResponse>>(await practitioner.Http.GetAsync("/api/v1/me/notifications", Ct));
        practitionerNotifications.ShouldContain(n => n.Type == NotificationType.TherapyProgressReported && n.ResourceId == session);

        // The client's export carries the record, its correction history and the consent.
        var export = await Scenario.Read<DataExportResponse>(await client.Http.PostAsync("/api/v1/me/data-exports", null, Ct));
        var raw = await (await client.Http.GetAsync(export.DownloadPath, Ct)).Content.ReadAsStringAsync(Ct);
        var package = System.Text.Json.JsonSerializer.Deserialize<DataExportPackage>(raw, Scenario.Json)!;
        package.TherapySessions.ShouldHaveSingleItem().Corrections.Count.ShouldBe(1);
        package.TherapyConsents.ShouldHaveSingleItem().PractitionerName.ShouldBe("practitioner Pro");
    }

    [Fact]
    public async Task Sessions_are_isolated_by_tenant_and_other_professionals_see_only_a_summary_with_a_grant()
    {
        var (client, practitioner, _, _, _, serviceId) = await ReadyAsync();
        var session = await ScheduleAsync(practitioner, client.Id, serviceId, DateTimeOffset.UtcNow.AddDays(2));

        var (trainer, _) = await Scenario.ProfessionalAsync();
        var trainerRelationship = await Scenario.LinkAsync(trainer, client);
        (await trainer.Http.GetAsync($"/api/v1/therapy/sessions/{session}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await trainer.Http.GetAsync($"/api/v1/care-relationships/{trainerRelationship}/therapy-summary", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The trainer is in another organization, so even with the grant the summary is empty: no cross-tenant sharing.
        await Scenario.ShareAsync(client, trainerRelationship, DataCategory.TherapySummary);
        var summary = await Scenario.Read<TherapySummaryResponse>(await trainer.Http.GetAsync($"/api/v1/care-relationships/{trainerRelationship}/therapy-summary", Ct));
        (summary.ScheduledUpcoming, summary.NextSessionAt).ShouldBe((0, null));

        // Another practitioner who knows the id gets 404, audited.
        var modality2 = await EnabledModalityAsync();
        var (outsider, _, _) = await PractitionerAsync(modality2, "outsider");
        await Scenario.LinkAsync(outsider, client, Discipline.TherapyPractitioner);
        (await outsider.Http.GetAsync($"/api/v1/therapy/sessions/{session}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await outsider.Http.PutAsJsonAsync($"/api/v1/therapy/sessions/{session}/record", CompleteRecord, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var audit = await Scenario.Read<PagedResponse<AuditEntryResponse>>(await Admin().Http.GetAsync($"/api/v1/admin/audit-entries?actorUserId={outsider.Id}", Ct));
        audit.Items.ShouldContain(e => e.Action == "deny" && e.ResourceId == session && e.SubjectUserId == null);

        // The practitioner's own reads are audited with the client as subject.
        await Scenario.EnsureSuccess(await practitioner.Http.GetAsync($"/api/v1/therapy/sessions/{session}", Ct));
        var own = await Scenario.Read<PagedResponse<AuditEntryResponse>>(await Admin().Http.GetAsync($"/api/v1/admin/audit-entries?actorUserId={practitioner.Id}", Ct));
        own.Items.ShouldContain(e => e.Action == "read" && e.ResourceId == session && e.SubjectUserId == client.Id);
    }

    [Fact]
    public async Task Summary_inside_the_same_organization_shows_counts_but_no_record_content()
    {
        var modality = await EnabledModalityAsync();
        var client = await Scenario.ClientAsync();
        var (practitioner, _, _) = await PractitionerAsync(modality);
        var (trainer, _) = await Scenario.ProfessionalAsync(ownTenant: false);
        Scenario.UseTenant(trainer, null);
        await Scenario.AddMemberAsync(practitioner, trainer, Discipline.PersonalTrainer);
        Scenario.UseTenant(trainer, practitioner.TenantId);
        await Scenario.LinkAsync(practitioner, client, Discipline.TherapyPractitioner);
        var trainerRelationship = await Scenario.LinkAsync(trainer, client);
        var serviceId = await ServiceAsync(practitioner, modality);
        await ScheduleAsync(practitioner, client.Id, serviceId, DateTimeOffset.UtcNow.AddDays(3));
        await Scenario.EnsureSuccess(await practitioner.Http.PostAsJsonAsync("/api/v1/therapy/care-plans", new
        {
            clientId = client.Id, modalityCode = modality, goals = "Reduzir dor lombar crônica", plannedSessions = 8, frequency = "Semanal",
        }, Ct));
        await Scenario.ShareAsync(client, trainerRelationship, DataCategory.TherapySummary);

        var response = await trainer.Http.GetAsync($"/api/v1/care-relationships/{trainerRelationship}/therapy-summary", Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var summary = await Scenario.Read<TherapySummaryResponse>(response);

        summary.ScheduledUpcoming.ShouldBe(1);
        summary.ActiveModalities.ShouldBe(["Acupuntura (teste)"]);
        raw.ShouldNotContain("lombar");
    }

    [Fact]
    public async Task Scheduling_rejects_overlaps_and_either_side_can_cancel()
    {
        var (client, practitioner, _, _, _, serviceId) = await ReadyAsync();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var first = await ScheduleAsync(practitioner, client.Id, serviceId, start);

        var overlap = await practitioner.Http.PostAsJsonAsync("/api/v1/therapy/sessions", new { clientId = client.Id, serviceId, startsAt = start.AddMinutes(30) }, Ct);
        overlap.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var later = await ScheduleAsync(practitioner, client.Id, serviceId, start.AddHours(2));

        await Scenario.EnsureSuccess(await client.Http.PostAsJsonAsync($"/api/v1/me/therapy-sessions/{first}/cancel", new { reason = "Viagem" }, Ct));
        await Scenario.EnsureSuccess(await practitioner.Http.PostAsJsonAsync($"/api/v1/therapy/sessions/{later}/cancel", new { reason = "Imprevisto" }, Ct));
        (await practitioner.Http.PostAsync($"/api/v1/therapy/sessions/{first}/complete", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // The freed slot can be booked again.
        await ScheduleAsync(practitioner, client.Id, serviceId, start.AddMinutes(30));
        var practitionerNotifications = await Scenario.Read<List<NotificationResponse>>(await practitioner.Http.GetAsync("/api/v1/me/notifications", Ct));
        practitionerNotifications.ShouldContain(n => n.Type == NotificationType.TherapySessionCancelled && n.ResourceId == first);
        var clientNotifications = await Scenario.Read<List<NotificationResponse>>(await client.Http.GetAsync("/api/v1/me/notifications", Ct));
        clientNotifications.ShouldContain(n => n.Type == NotificationType.TherapySessionCancelled && n.ResourceId == later);
    }

    [Fact]
    public async Task Care_plans_are_reviewed_with_history_and_close_on_discharge_or_referral()
    {
        var (client, practitioner, _, _, modality, _) = await ReadyAsync();
        var planId = (await Scenario.Read<Scenario.IdResponse>(await practitioner.Http.PostAsJsonAsync("/api/v1/therapy/care-plans", new
        {
            clientId = client.Id, modalityCode = modality, goals = "Reduzir dor", plannedSessions = 6, frequency = "Semanal", nextReviewOn = "2026-11-01",
        }, Ct))).Id;

        await Scenario.EnsureSuccess(await practitioner.Http.PostAsJsonAsync($"/api/v1/therapy/care-plans/{planId}/reviews", new
        {
            outcome = "Adjust", notes = "Boa resposta; espaçar sessões", frequency = "Quinzenal", nextReviewOn = "2026-12-01",
        }, Ct));
        await Scenario.EnsureSuccess(await practitioner.Http.PostAsJsonAsync($"/api/v1/therapy/care-plans/{planId}/reviews", new
        {
            outcome = "ReferForMedicalCare", notes = "Sinais de alerta; encaminhado para avaliação médica",
        }, Ct));
        (await practitioner.Http.PostAsJsonAsync($"/api/v1/therapy/care-plans/{planId}/reviews", new { outcome = "Continue", notes = "x" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var plan = (await Scenario.Read<List<CarePlanResponse>>(await client.Http.GetAsync("/api/v1/me/therapy-care-plans", Ct))).ShouldHaveSingleItem();
        (plan.Status, plan.Frequency, plan.NextReviewOn).ShouldBe((CarePlanStatus.Closed, "Quinzenal", null));
        plan.Reviews.Select(r => r.Outcome).ShouldBe([CarePlanReviewOutcome.Adjust, CarePlanReviewOutcome.ReferForMedicalCare]);
    }

    [Fact]
    public async Task Ending_the_relationship_cancels_scheduled_sessions_and_frees_the_slot()
    {
        var (client, practitioner, _, relationshipId, _, serviceId) = await ReadyAsync();
        var start = DateTimeOffset.UtcNow.AddDays(4);
        var session = await ScheduleAsync(practitioner, client.Id, serviceId, start);

        await Scenario.EnsureSuccess(await client.Http.PostAsJsonAsync($"/api/v1/care-relationships/{relationshipId}/end", new { reason = "Mudança de cidade" }, Ct));

        var mine = (await Scenario.Read<List<TherapySessionResponse>>(await client.Http.GetAsync("/api/v1/me/therapy-sessions", Ct))).ShouldHaveSingleItem();
        (mine.Status, mine.CancellationReason, mine.CancelledByClient).ShouldBe((TherapySessionStatus.Cancelled, TherapySession.RelationshipEndedReason, false));
        var notifications = await Scenario.Read<List<NotificationResponse>>(await client.Http.GetAsync("/api/v1/me/notifications", Ct));
        notifications.ShouldContain(n => n.Type == NotificationType.TherapySessionCancelled && n.ResourceId == session);

        // The practitioner can book the same time for another client.
        var other = await Scenario.ClientAsync("other");
        await Scenario.LinkAsync(practitioner, other, Discipline.TherapyPractitioner);
        await ScheduleAsync(practitioner, other.Id, serviceId, start);
    }

    [Fact]
    public async Task Racing_bookings_for_the_same_slot_never_double_book_the_practitioner()
    {
        var (client, practitioner, _, _, _, serviceId) = await ReadyAsync();
        var start = DateTimeOffset.UtcNow.AddDays(6);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => practitioner.Http.PostAsJsonAsync(
            "/api/v1/therapy/sessions", new { clientId = client.Id, serviceId, startsAt = start.AddMinutes(i) }, Ct)));

        responses.Count(r => r.IsSuccessStatusCode).ShouldBe(1);
        responses.Where(r => !r.IsSuccessStatusCode).ShouldAllBe(r => r.StatusCode == HttpStatusCode.Conflict);
    }
}
