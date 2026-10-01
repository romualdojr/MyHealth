using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.CareRelationships;
using MyHealth.Application.Tenants;
using MyHealth.Application.TrainingPlans;
using MyHealth.Application.Users;
using MyHealth.Application.Workouts;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Api.IntegrationTests;

/// <summary>State transitions over HTTP: ending plans, pausing workouts, declining invitations, changing credentials.</summary>
public sealed class LifecycleTests(ApiFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task An_ended_plan_cannot_change_but_stays_visible_to_the_client_and_only_its_trainer_can_end_it()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var (stranger, _) = await Scenario.ProfessionalAsync("stranger");
        await Scenario.LinkAsync(trainer, client);
        var plan = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, plan);

        (await stranger.Http.PostAsync($"/api/v1/training-plans/{plan}/end", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await Scenario.EnsureSuccess(await trainer.Http.PostAsync($"/api/v1/training-plans/{plan}/end", null, Ct));

        (await trainer.Http.PostAsync($"/api/v1/training-plans/{plan}/end", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await Scenario.SetSessionsAsync(trainer, plan, ["Treino B"]).ShouldThrowAsync<Exception>();
        (await trainer.Http.PostAsJsonAsync($"/api/v1/training-plans/{plan}/publish", new { changeDescription = "v2" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Scenario.Read<TrainerPlanResponse>(await trainer.Http.GetAsync($"/api/v1/training-plans/{plan}", Ct))).Status.ShouldBe(PlanStatus.Ended);
        (await Scenario.Read<List<ClientPlanListItem>>(await client.Http.GetAsync("/api/v1/me/training-plans", Ct)))
            .ShouldHaveSingleItem().Status.ShouldBe(PlanStatus.Ended);
    }

    [Fact]
    public async Task Pausing_and_resuming_are_idempotent_and_paused_time_is_not_counted()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        await Scenario.LinkAsync(trainer, client);
        var plan = await Scenario.DraftPlanAsync(trainer, client.Id);
        await Scenario.PublishAsync(trainer, plan);
        var start = DateTimeOffset.UtcNow.AddMinutes(-40);
        var (session, _) = await Scenario.StartAsync(client, plan, start);

        (await client.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{session}/resume", new { at = start.AddMinutes(5) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await Scenario.EnsureSuccess(await client.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{session}/pause", new { at = start.AddMinutes(10) }, Ct));
        await Scenario.EnsureSuccess(await client.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{session}/pause", new { at = start.AddMinutes(10) }, Ct));
        (await Scenario.Read<WorkoutSessionResponse>(await client.Http.GetAsync($"/api/v1/workout-sessions/{session}", Ct))).Status.ShouldBe(WorkoutStatus.Paused);

        // Resuming before the pause started is rejected; someone else's session is not found.
        (await client.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{session}/resume", new { at = start.AddMinutes(9) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var other = await Scenario.ClientAsync("other");
        (await other.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{session}/resume", new { at = start.AddMinutes(20) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await Scenario.EnsureSuccess(await client.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{session}/resume", new { at = start.AddMinutes(20) }, Ct));
        await Scenario.CompleteAsync(client, session, start.AddMinutes(40));

        var completed = await Scenario.Read<WorkoutSessionResponse>(await client.Http.GetAsync($"/api/v1/workout-sessions/{session}", Ct));
        completed.Status.ShouldBe(WorkoutStatus.Completed);
        completed.ActiveDurationSeconds.ShouldBe(30 * 60);
        (await client.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{session}/pause", new { at = start.AddMinutes(41) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_declined_care_invitation_cannot_be_accepted_later_and_the_professional_sees_it_declined()
    {
        var client = await Scenario.ClientAsync();
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var invite = await trainer.Http.PostAsJsonAsync("/api/v1/care-relationships/invitations", new { discipline = "PersonalTrainer", clientEmail = client.Email }, Ct);
        var relationshipId = (await Scenario.Read<Scenario.IdResponse>(invite)).Id;

        var stranger = await Scenario.ClientAsync("stranger");
        // Someone else's invitation does not exist for them.
        (await stranger.Http.PostAsync($"/api/v1/care-relationships/{relationshipId}/decline", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await Scenario.EnsureSuccess(await client.Http.PostAsync($"/api/v1/care-relationships/{relationshipId}/decline", null, Ct));

        (await client.Http.PostAsync($"/api/v1/care-relationships/{relationshipId}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var seen = await Scenario.Read<List<CareRelationshipResponse>>(await trainer.Http.GetAsync("/api/v1/care-relationships", Ct));
        seen.Single(r => r.Id == relationshipId).Status.ShouldBe(RelationshipStatus.Declined);

        // Declining ends nothing else: the professional can invite again.
        await Scenario.LinkAsync(trainer, client);
    }

    [Fact]
    public async Task A_declined_organization_invitation_cannot_be_accepted_and_the_administrator_sees_it()
    {
        var (owner, _) = await Scenario.ProfessionalAsync("owner", ownTenant: false);
        var tenantId = await Scenario.CreateTenantAsync(owner, "Clínica", TenantKind.Organization, Discipline.PersonalTrainer);
        Scenario.UseTenant(owner, tenantId);
        var invitee = await Scenario.ClientAsync("colleague");
        var invite = await owner.Http.PostAsJsonAsync("/api/v1/tenant/memberships/invitations", new
        {
            email = invitee.Email, disciplines = new[] { "PersonalTrainer" }, isAdministrator = false,
        }, Ct);
        var membershipId = (await Scenario.Read<Scenario.IdResponse>(invite)).Id;

        var stranger = await Scenario.ClientAsync("stranger");
        (await stranger.Http.PostAsync($"/api/v1/tenant-invitations/{membershipId}/decline", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await Scenario.EnsureSuccess(await invitee.Http.PostAsync($"/api/v1/tenant-invitations/{membershipId}/decline", null, Ct));

        (await invitee.Http.PostAsync($"/api/v1/tenant-invitations/{membershipId}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Scenario.Read<List<TenantInvitationResponse>>(await invitee.Http.GetAsync("/api/v1/me/tenant-invitations", Ct))).ShouldBeEmpty();
        var members = await Scenario.Read<List<MembershipResponse>>(await owner.Http.GetAsync("/api/v1/tenant/memberships", Ct));
        members.Single(m => m.Id == membershipId).Status.ShouldBe(MembershipStatus.Declined);
    }

    [Fact]
    public async Task Changing_the_council_registration_resets_verification_but_editing_details_does_not()
    {
        var (trainer, profileId) = await Scenario.ProfessionalAsync();
        var me = await Scenario.Read<MeResponse>(await trainer.Http.GetAsync("/api/v1/me", Ct));
        var registration = me.ProfessionalProfiles.ShouldHaveSingleItem();
        registration.VerificationStatus.ShouldBe(VerificationStatus.Verified);

        var current = await Scenario.Read<List<MyHealth.Application.Professionals.ProfessionalProfileResponse>>(
            await Scenario.Factory.NewUser("admin", administrator: true).Http.GetAsync("/api/v1/admin/professional-profiles?status=Verified", Ct));
        var profile = current.Single(p => p.Id == profileId);
        var sameRegistration = new { council = profile.Council, number = profile.RegistrationNumber, region = profile.Region };

        await Scenario.EnsureSuccess(await trainer.Http.PutAsJsonAsync($"/api/v1/professional-profiles/{profileId}", new
        {
            displayName = "Treinador Novo Nome", bio = "Especialista em força", registration = sameRegistration,
        }, Ct));
        (await Scenario.Read<MeResponse>(await trainer.Http.GetAsync("/api/v1/me", Ct))).ProfessionalProfiles[0]
            .ShouldSatisfyAllConditions(p => p.DisplayName.ShouldBe("Treinador Novo Nome"), p => p.VerificationStatus.ShouldBe(VerificationStatus.Verified));

        var other = await Scenario.ClientAsync("other");
        (await other.Http.PutAsJsonAsync($"/api/v1/professional-profiles/{profileId}", new { displayName = "x", registration = sameRegistration }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await Scenario.EnsureSuccess(await trainer.Http.PutAsJsonAsync($"/api/v1/professional-profiles/{profileId}", new
        {
            displayName = "Treinador Novo Nome", registration = new { council = "CREF", number = "999999-G", region = "RJ" },
        }, Ct));
        (await Scenario.Read<MeResponse>(await trainer.Http.GetAsync("/api/v1/me", Ct))).ProfessionalProfiles[0].VerificationStatus.ShouldBe(VerificationStatus.Pending);

        // A new registration must be checked again before inviting clients.
        (await trainer.Http.PostAsJsonAsync("/api/v1/care-relationships/invitations", new { discipline = "PersonalTrainer", clientEmail = "x@test.local" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
