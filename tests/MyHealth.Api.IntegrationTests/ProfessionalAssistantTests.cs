extern alias web;

using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Therapies;
using web::MyHealth.Web.Common.Api;

namespace MyHealth.Api.IntegrationTests;

/// <summary>
/// The dietitian's and the therapy practitioner's chat (rule-based model): patients at a glance, summaries only of those
/// who allow AI, and plan proposals that become a meal plan draft or a care plan through the usual endpoints. Replies are
/// read with the web app's models, as the assistant page reads them.
/// </summary>
public sealed class ProfessionalAssistantTests(ApiFactory factory) : IntegrationTest(factory)
{
    private static async Task<ProfessionalAssistantReply> AskAsync(TestUser professional, string path, string message, Guid? clientId = null) =>
        await Scenario.Read<ProfessionalAssistantReply>(
            await professional.Http.PostAsJsonAsync($"/api/v1/{path}/messages", new { message, history = Array.Empty<object>(), clientId }, Ct));

    [Fact]
    public async Task The_dietitian_gets_summaries_and_a_meal_plan_proposal_that_respects_the_assessment_and_becomes_a_draft()
    {
        var (dietitian, _) = await Scenario.ProfessionalAsync("dietitian", Discipline.Dietitian, council: "CRN");
        var daniela = await Scenario.ClientAsync("daniela");
        var danielaLink = await Scenario.LinkAsync(dietitian, daniela, Discipline.Dietitian);
        await Scenario.ShareAsync(daniela, danielaLink, DataCategory.AiCoaching);
        var otto = await Scenario.ClientAsync("otto");
        await Scenario.LinkAsync(dietitian, otto, Discipline.Dietitian);

        await Scenario.EnsureSuccess(await dietitian.Http.PostAsJsonAsync("/api/v1/nutrition-assessments", new
        {
            clientId = daniela.Id,
            assessment = new
            {
                assessedOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2), goals = "Ganhar massa magra", reportedRestrictions = "Vegetariana (come ovos)",
                reportedAllergies = "Intolerância à lactose", preferences = "Gosta de ovos",
            },
        }, Ct));

        // Both patients on the page; only Daniela allows AI analysis.
        var overview = await Scenario.Read<ProfessionalAssistantOverviewItem>(await dietitian.Http.GetAsync("/api/v1/dietitian/assistant", Ct));
        overview.Discipline.ShouldBe("Dietitian");
        overview.Patients.Select(p => (p.Name, p.AiConsent, p.Attention)).OrderBy(p => p.Name)
            .ShouldBe([("daniela Silva", true, "sem plano"), ("otto Silva", false, "sem plano")]);

        // The summary covers Daniela only, by name, and says one patient is not shared.
        var summary = await AskAsync(dietitian, "dietitian/assistant", "resumo da semana");
        (summary.PatientsIncluded, summary.PatientsWithoutConsent).ShouldBe((1, 1));
        summary.Reply.ShouldContain("daniela Silva");
        summary.Reply.ShouldNotContain("Paciente 1");

        // Asking by name (hidden from the model as an alias) gives a vegetarian, lactose-free proposal that keeps eggs.
        var reply = await AskAsync(dietitian, "dietitian/assistant", "Monte um plano alimentar para a daniela", daniela.Id);
        var proposal = reply.MealPlans.ShouldHaveSingleItem();
        proposal.ClientId.ShouldBe(daniela.Id);
        var foods = proposal.Meals.SelectMany(m => m.Items).Select(i => i.Food).ToList();
        foods.ShouldNotContain(f => f.Contains("Frango", StringComparison.Ordinal) || f.Contains("Café com leite", StringComparison.Ordinal));
        foods.ShouldContain("Ovos mexidos");
        proposal.Details.Goal.ShouldBe("Ganhar massa magra");
        proposal.Reason.ShouldNotBeNull().ShouldContain("vegetariano");

        // The page creates the draft as proposed, through the usual endpoints; nothing was stored before.
        (await Scenario.Read<List<MealPlanItem>>(await dietitian.Http.GetAsync($"/api/v1/meal-plans?clientId={daniela.Id}", Ct))).ShouldBeEmpty();
        var planId = (await Scenario.Read<Scenario.IdResponse>(
            await dietitian.Http.PostAsJsonAsync("/api/v1/meal-plans", new { clientId = proposal.ClientId, details = proposal.Details }, Ct))).Id;
        await Scenario.EnsureSuccess(await dietitian.Http.PutAsJsonAsync($"/api/v1/meal-plans/{planId}/meals", proposal.Meals, Ct));
        await Scenario.EnsureSuccess(await dietitian.Http.PostAsJsonAsync($"/api/v1/meal-plans/{planId}/publish", new { changeDescription = "Proposta do assistente" }, Ct));

        // Otto has not allowed AI analysis: the chat refuses to focus on him.
        var refused = await dietitian.Http.PostAsJsonAsync("/api/v1/dietitian/assistant/messages", new { message = "resumo", clientId = otto.Id }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("Assistant.SharingRequired");

        // Answered messages count toward the organization's daily limit.
        (await Scenario.Read<ProfessionalAssistantOverviewItem>(await dietitian.Http.GetAsync("/api/v1/dietitian/assistant", Ct))).UsedToday.ShouldBe(2);
    }

    [Fact]
    public async Task A_trainer_cannot_use_the_dietitian_assistant_and_a_dietitian_only_sees_their_own_patients()
    {
        var (trainer, _) = await Scenario.ProfessionalAsync();
        (await trainer.Http.GetAsync("/api/v1/dietitian/assistant", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (dietitian, _) = await Scenario.ProfessionalAsync("dietitian", Discipline.Dietitian, council: "CRN");
        var client = await Scenario.ClientAsync();
        await Scenario.LinkAsync(trainer, client);
        var outsider = await dietitian.Http.PostAsJsonAsync("/api/v1/dietitian/assistant/messages", new { message = "resumo", clientId = client.Id }, Ct);
        outsider.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_practitioner_gets_a_care_plan_proposal_in_a_qualified_modality_and_starts_it()
    {
        var admin = Scenario.Factory.NewUser("operator", administrator: true);
        var code = "acupuntura_" + new string(Enumerable.Range(0, 10).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync("/api/v1/admin/therapy-modalities", new
        {
            code,
            modality = new
            {
                name = "Acupuntura (teste)", acceptedCouncils = new[] { "COFFITO" },
                requiredRecordFields = TherapyModality.AcupunctureDefaultRequiredFields.Select(f => f.ToString()).ToArray(), requiresConsent = true,
            },
        }, Ct));
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync($"/api/v1/admin/therapy-modalities/{code}/consent-templates", new
        {
            title = "Termo de consentimento", body = "Declaro que fui informado(a).", disclosedRisks = new[] { "Dor local" },
        }, Ct));
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync($"/api/v1/admin/therapy-modalities/{code}/enable", new { legalReviewReference = "Parecer 1/2026" }, Ct));

        var (practitioner, _) = await Scenario.ProfessionalAsync("practitioner", Discipline.TherapyPractitioner, council: "COFFITO");
        var qualification = (await Scenario.Read<Scenario.IdResponse>(await practitioner.Http.PostAsJsonAsync("/api/v1/therapy/qualifications", new
        {
            modalityCode = code, title = "Especialização em Acupuntura", issuer = "Instituto X", certificateNumber = "123",
        }, Ct))).Id;

        var client = await Scenario.ClientAsync("lia");
        var link = await Scenario.LinkAsync(practitioner, client, Discipline.TherapyPractitioner);
        await Scenario.ShareAsync(client, link, DataCategory.AiCoaching);

        // Without a verified qualification there is no modality to propose a plan in.
        var unqualified = await AskAsync(practitioner, "therapy/assistant", "proponha um plano de cuidado", client.Id);
        unqualified.CarePlans.ShouldBeEmpty();
        unqualified.Reply.ShouldContain("qualificação verificada");

        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync($"/api/v1/admin/practitioner-qualifications/{qualification}/verify", new { notes = "ok" }, Ct));
        var reply = await AskAsync(practitioner, "therapy/assistant", "proponha um plano de cuidado", client.Id);
        var proposal = reply.CarePlans.ShouldHaveSingleItem();
        (proposal.ClientId, proposal.ModalityCode, proposal.PlannedSessions).ShouldBe((client.Id, code, 8));
        proposal.NextReviewOn.ShouldNotBeNull().ShouldBeGreaterThan(DateOnly.FromDateTime(DateTime.UtcNow));

        // The practitioner starts it as proposed; then the summary shows the active plan.
        await Scenario.EnsureSuccess(await practitioner.Http.PostAsJsonAsync("/api/v1/therapy/care-plans", new
        {
            clientId = proposal.ClientId, modalityCode = proposal.ModalityCode, goals = proposal.Goals, plannedSessions = proposal.PlannedSessions,
            frequency = proposal.Frequency, nextReviewOn = proposal.NextReviewOn,
        }, Ct));
        var overview = await Scenario.Read<ProfessionalAssistantOverviewItem>(await practitioner.Http.GetAsync("/api/v1/therapy/assistant", Ct));
        var patient = overview.Patients.ShouldHaveSingleItem();
        (patient.PlanName, patient.Attention).ShouldBe(("Acupuntura (teste)", null));

        var summary = await AskAsync(practitioner, "therapy/assistant", "resumo");
        summary.Reply.ShouldContain("lia Silva");
        summary.Reply.ShouldContain(code);
    }
}
