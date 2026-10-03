using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Auditing;
using MyHealth.Application.CareRelationships;
using MyHealth.Application.Common;
using MyHealth.Application.Exercises;
using MyHealth.Application.HealthData;
using MyHealth.Application.Notifications;
using MyHealth.Application.Tenants;
using MyHealth.Application.TrainingPlans;
using MyHealth.Application.Users;
using MyHealth.Application.Workouts;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;

namespace MyHealth.Api.IntegrationTests;

/// <summary>
/// Tenant isolation (§4.1.1, MT-01 → MT-10) and acceptance tests 14 and 15. Every cross-tenant attempt uses ids that
/// are known to the caller, and must fail without revealing the resource.
/// </summary>
public sealed class MultiTenancyTests(ApiFactory factory) : IntegrationTest(factory)
{
    private sealed record PlanInTenant(TestUser Client, Guid RelationshipId, Guid PlanId, Guid SessionId);

    /// <summary>Links a new client to <paramref name="trainer"/> in their selected tenant and runs one completed workout.</summary>
    private async Task<PlanInTenant> ClientWithCompletedWorkoutAsync(TestUser trainer, string clientName, TestUser? client = null)
    {
        client ??= await Scenario.ClientAsync(clientName);
        var relationship = await Scenario.LinkAsync(trainer, client);
        var planId = await Scenario.DraftPlanAsync(trainer, client.Id, $"Plano {clientName}");
        await Scenario.PublishAsync(trainer, planId);
        var (sessionId, _) = await Scenario.StartAsync(client, planId);
        await Scenario.CompleteAsync(client, sessionId);
        return new PlanInTenant(client, relationship, planId, sessionId);
    }

    /// <summary>An organization owned by an administrator who practises no discipline, plus that owner.</summary>
    private async Task<(TestUser Owner, Guid TenantId)> OrganizationAsync(string name)
    {
        var owner = await Scenario.ClientAsync($"{name}-owner");
        var tenantId = await Scenario.CreateTenantAsync(owner, name, TenantKind.Organization);
        Scenario.UseTenant(owner, tenantId);
        return (owner, tenantId);
    }

    [Fact]
    public async Task Professional_in_two_tenants_sees_only_the_selected_tenants_records()
    {
        // Acceptance test 14. The trainer has a solo practice (A) and also works for a studio (B).
        var (trainer, _) = await Scenario.ProfessionalAsync("multi");
        var tenantA = trainer.TenantId!.Value;
        var inA = await ClientWithCompletedWorkoutAsync(trainer, "client-a");

        var (studioOwner, tenantB) = await OrganizationAsync("studio");
        await Scenario.AddMemberAsync(studioOwner, trainer, Discipline.PersonalTrainer);
        Scenario.UseTenant(trainer, tenantB);
        var inB = await ClientWithCompletedWorkoutAsync(trainer, "client-b");

        // Both tenants are offered to the professional.
        var me = await Scenario.Read<MeResponse>(await trainer.Http.GetAsync("/api/v1/me", Ct));
        me.Memberships.Select(m => m.TenantId).ShouldBe([tenantA, tenantB], ignoreOrder: true);

        // Acting for B: lists only contain B.
        (await Scenario.Read<List<TrainerPlanListItem>>(await trainer.Http.GetAsync("/api/v1/training-plans", Ct)))
            .ShouldHaveSingleItem().Id.ShouldBe(inB.PlanId);
        (await Scenario.Read<TrainerDashboardResponse>(await trainer.Http.GetAsync("/api/v1/trainer/dashboard", Ct)))
            .Clients.ShouldHaveSingleItem().ClientId.ShouldBe(inB.Client.Id);
        var relationships = await Scenario.Read<List<CareRelationshipResponse>>(await trainer.Http.GetAsync("/api/v1/care-relationships", Ct));
        relationships.Where(r => r.Perspective == RelationshipPerspective.Professional).ShouldHaveSingleItem().Id.ShouldBe(inB.RelationshipId);
        var notifications = await Scenario.Read<List<NotificationResponse>>(await trainer.Http.GetAsync("/api/v1/me/notifications", Ct));
        notifications.ShouldNotBeEmpty();
        notifications.Where(n => n.Audience == NotificationAudience.Professional).ShouldAllBe(n => n.TenantId == tenantB);

        // Known ids from A fail while acting for B.
        (await trainer.Http.GetAsync($"/api/v1/training-plans/{inA.PlanId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await trainer.Http.PostAsJsonAsync($"/api/v1/training-plans/{inA.PlanId}/publish", new { changeDescription = "x" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await trainer.Http.GetAsync($"/api/v1/training-plans/{inA.PlanId}/versions/1", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await trainer.Http.GetAsync($"/api/v1/workout-sessions/{inA.SessionId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await trainer.Http.PostAsJsonAsync($"/api/v1/workout-sessions/{inA.SessionId}/feedback-reply", new { message = "Oi" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await trainer.Http.GetAsync($"/api/v1/trainer/clients/{inA.Client.Id}/workout-sessions", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await trainer.Http.PostAsJsonAsync($"/api/v1/training-plans/{inB.PlanId}/copy", new { targetClientId = inA.Client.Id }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Switching back to A shows A again.
        Scenario.UseTenant(trainer, tenantA);
        (await Scenario.Read<List<TrainerPlanListItem>>(await trainer.Http.GetAsync("/api/v1/training-plans", Ct)))
            .ShouldHaveSingleItem().Id.ShouldBe(inA.PlanId);
        (await trainer.Http.GetAsync($"/api/v1/workout-sessions/{inA.SessionId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await trainer.Http.GetAsync($"/api/v1/workout-sessions/{inB.SessionId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // MT-09: the rejected attempts are audited in the tenant that was selected, without revealing the resource.
        var admin = Scenario.Factory.NewUser("auditor", administrator: true);
        var audit = await Scenario.Read<PagedResponse<AuditEntryResponse>>(
            await admin.Http.GetAsync($"/api/v1/admin/audit-entries?actorUserId={trainer.Id}&pageSize=500", Ct));
        audit.Items.ShouldContain(e => e.Action == "deny" && e.ResourceId == inA.PlanId && e.TenantId == tenantB && e.SubjectUserId == null);
        audit.Items.ShouldContain(e => e.Action == "deny" && e.ResourceId == inB.SessionId && e.TenantId == tenantA);
    }

    [Fact]
    public async Task Tenant_header_is_required_for_professional_work_and_honoured_only_for_members()
    {
        var (trainer, _) = await Scenario.ProfessionalAsync();
        var (_, strangersTenant) = await OrganizationAsync("elsewhere");

        Scenario.UseTenant(trainer, null);
        var missing = await trainer.Http.GetAsync("/api/v1/training-plans", Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync(Ct)).ShouldContain("Tenant.Required");

        Scenario.UseTenant(trainer, strangersTenant);
        var notMember = await trainer.Http.GetAsync("/api/v1/training-plans", Ct);
        notMember.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await notMember.Content.ReadAsStringAsync(Ct)).ShouldContain("Tenant.NotFound");

        trainer.Http.DefaultRequestHeaders.Remove("X-Tenant-Id");
        trainer.Http.DefaultRequestHeaders.Add("X-Tenant-Id", "not-a-guid");
        (await trainer.Http.GetAsync("/api/v1/training-plans", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Platform operators have no tenant access either.
        var platformAdmin = Scenario.Factory.NewUser("operator", administrator: true);
        Scenario.UseTenant(platformAdmin, strangersTenant);
        (await platformAdmin.Http.GetAsync("/api/v1/tenant", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Client_of_two_organizations_sees_both_and_neither_organization_sees_the_others_records()
    {
        // Acceptance test 15.
        var client = await Scenario.ClientAsync("shared-client");
        var (trainerA, _) = await Scenario.ProfessionalAsync("trainer-a");
        var (trainerB, _) = await Scenario.ProfessionalAsync("trainer-b");
        var inA = await ClientWithCompletedWorkoutAsync(trainerA, "shared-client", client);
        var inB = await ClientWithCompletedWorkoutAsync(trainerB, "shared-client", client);

        // The client's own view combines both, labelled by organization and professional (MT-10).
        var plans = await Scenario.Read<List<ClientPlanListItem>>(await client.Http.GetAsync("/api/v1/me/training-plans", Ct));
        plans.Select(p => (p.TenantId, p.OrganizationName, p.TrainerName)).ShouldBe(
            [(trainerA.TenantId!.Value, "trainer-a Pro", "trainer-a Pro"), (trainerB.TenantId!.Value, "trainer-b Pro", "trainer-b Pro")], ignoreOrder: true);
        var home = await Scenario.Read<ClientHomeResponse>(await client.Http.GetAsync("/api/v1/me/home", Ct));
        home.Professionals.Select(p => p.TenantId).ShouldBe([trainerA.TenantId!.Value, trainerB.TenantId!.Value], ignoreOrder: true);
        home.Attendance.CompletedSessions.ShouldBe(2);

        // Heart rate recorded for B's workout, shared with both professionals.
        await Scenario.EnsureSuccess(await client.Http.PostAsync("/api/v1/me/health-connections/apple-health/connect", null, Ct));
        await Scenario.EnsureSuccess(await client.Http.PutAsJsonAsync($"/api/v1/workout-sessions/{inB.SessionId}/heart-rate", new
        {
            workoutID = inB.SessionId, startedAt = DateTimeOffset.UtcNow.AddMinutes(-40), endedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            points = new[] { new { healthKitID = Guid.NewGuid(), timestamp = DateTimeOffset.UtcNow.AddMinutes(-20), beatsPerMinute = 130, sourceName = "Apple Watch" } },
        }, Ct));
        await Scenario.EnsureSuccess(await client.Http.PutAsync($"/api/v1/care-relationships/{inA.RelationshipId}/sharing/HeartRate", null, Ct));
        await Scenario.EnsureSuccess(await client.Http.PutAsync($"/api/v1/care-relationships/{inB.RelationshipId}/sharing/HeartRate", null, Ct));

        var recipients = (await Scenario.Read<HealthConnectionResponse>(await client.Http.GetAsync("/api/v1/me/health-connections/apple-health", Ct))).Recipients;
        recipients.Select(r => r.OrganizationName).ShouldBe(["trainer-a Pro", "trainer-b Pro"], ignoreOrder: true);

        // MT-06: A's grant never exposes B's workout.
        (await Scenario.Read<List<SharedHeartRateSessionItem>>(
            await trainerA.Http.GetAsync($"/api/v1/care-relationships/{inA.RelationshipId}/heart-rate/sessions", Ct))).ShouldBeEmpty();
        (await trainerA.Http.GetAsync($"/api/v1/care-relationships/{inA.RelationshipId}/heart-rate/sessions/{inB.SessionId}", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Scenario.Read<List<SharedHeartRateSessionItem>>(
            await trainerB.Http.GetAsync($"/api/v1/care-relationships/{inB.RelationshipId}/heart-rate/sessions", Ct)))
            .ShouldHaveSingleItem().SessionId.ShouldBe(inB.SessionId);

        // Neither professional can use the other's relationship, plan or session.
        (await trainerA.Http.GetAsync($"/api/v1/care-relationships/{inB.RelationshipId}/heart-rate/sessions", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await trainerA.Http.PostAsJsonAsync($"/api/v1/care-relationships/{inB.RelationshipId}/end", new { reason = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await trainerA.Http.GetAsync($"/api/v1/training-plans/{inB.PlanId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await trainerA.Http.GetAsync($"/api/v1/workout-sessions/{inB.SessionId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Scenario.Read<List<WorkoutSessionListItem>>(await trainerA.Http.GetAsync($"/api/v1/trainer/clients/{client.Id}/workout-sessions", Ct)))
            .ShouldHaveSingleItem().Id.ShouldBe(inA.SessionId);
    }

    [Fact]
    public async Task Removing_a_professional_from_a_tenant_stops_access_immediately_and_keeps_the_records()
    {
        // MT-08.
        var (owner, tenantId) = await OrganizationAsync("clinic");
        var (trainer, _) = await Scenario.ProfessionalAsync("leaving", ownTenant: false);
        var membershipId = await Scenario.AddMemberAsync(owner, trainer, Discipline.PersonalTrainer);
        Scenario.UseTenant(trainer, tenantId);
        var record = await ClientWithCompletedWorkoutAsync(trainer, "patient");

        await Scenario.EnsureSuccess(await owner.Http.PostAsJsonAsync($"/api/v1/tenant/memberships/{membershipId}/revoke", new { reason = "Contract ended" }, Ct));

        (await trainer.Http.GetAsync("/api/v1/training-plans", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await trainer.Http.GetAsync($"/api/v1/workout-sessions/{record.SessionId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The client's relationship ended; their records remain, labelled with the clinic.
        var relationship = (await Scenario.Read<List<CareRelationshipResponse>>(await record.Client.Http.GetAsync("/api/v1/care-relationships", Ct)))
            .ShouldHaveSingleItem();
        relationship.Status.ShouldBe(RelationshipStatus.Ended);
        relationship.OrganizationName.ShouldBe("clinic");
        (await Scenario.Read<List<ClientPlanListItem>>(await record.Client.Http.GetAsync("/api/v1/me/training-plans", Ct))).ShouldHaveSingleItem();
        (await record.Client.Http.GetAsync($"/api/v1/workout-sessions/{record.SessionId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var members = await Scenario.Read<List<MembershipResponse>>(await owner.Http.GetAsync("/api/v1/tenant/memberships", Ct));
        members.Single(m => m.Id == membershipId).Status.ShouldBe(MembershipStatus.Revoked);
        var audit = await Scenario.Read<PagedResponse<AuditEntryResponse>>(await owner.Http.GetAsync("/api/v1/tenant/audit-entries", Ct));
        audit.Items.ShouldContain(e => e.Action == "end" && e.ResourceId == record.RelationshipId);
        audit.Items.ShouldAllBe(e => e.TenantId == tenantId);
    }

    [Fact]
    public async Task Tenant_administrators_manage_members_but_get_no_clinical_access()
    {
        // MT-05.
        var (owner, tenantId) = await OrganizationAsync("gym");
        var (trainer, _) = await Scenario.ProfessionalAsync("coach", ownTenant: false);
        var ownerMembership = (await Scenario.Read<CurrentTenantResponse>(await owner.Http.GetAsync("/api/v1/tenant", Ct))).MembershipId;
        await Scenario.AddMemberAsync(owner, trainer, Discipline.PersonalTrainer);
        Scenario.UseTenant(trainer, tenantId);
        var record = await ClientWithCompletedWorkoutAsync(trainer, "member-client");

        (await owner.Http.GetAsync("/api/v1/trainer/dashboard", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Http.GetAsync($"/api/v1/training-plans/{record.PlanId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Http.GetAsync($"/api/v1/workout-sessions/{record.SessionId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A plain member cannot administer, and the last administrator cannot leave.
        (await trainer.Http.GetAsync("/api/v1/tenant/memberships", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await trainer.Http.PostAsJsonAsync("/api/v1/tenant/memberships/invitations", new { email = "x@test.local", disciplines = Array.Empty<string>() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Http.PostAsJsonAsync($"/api/v1/tenant/memberships/{ownerMembership}/revoke", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Inviting someone who already belongs is a conflict.
        (await owner.Http.PostAsJsonAsync("/api/v1/tenant/memberships/invitations", new { email = trainer.Email, disciplines = new[] { "PersonalTrainer" } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Administrator_of_another_tenant_cannot_revoke_this_tenants_members()
    {
        var (ownerA, _) = await OrganizationAsync("org-a");
        var (ownerB, _) = await OrganizationAsync("org-b");
        var member = await Scenario.ClientAsync("member");
        var membershipInA = await Scenario.AddMemberAsync(ownerA, member);

        (await ownerB.Http.PostAsJsonAsync($"/api/v1/tenant/memberships/{membershipInA}/revoke", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invitations_are_listed_for_the_invitee_and_a_member_may_leave()
    {
        var (owner, tenantId) = await OrganizationAsync("team");
        var invitee = await Scenario.ClientAsync("invitee");
        var invite = await owner.Http.PostAsJsonAsync("/api/v1/tenant/memberships/invitations", new { email = invitee.Email, disciplines = new[] { "Dietitian" } }, Ct);
        var membershipId = (await Scenario.Read<Scenario.IdResponse>(invite)).Id;

        var pending = await Scenario.Read<List<TenantInvitationResponse>>(await invitee.Http.GetAsync("/api/v1/me/tenant-invitations", Ct));
        pending.ShouldHaveSingleItem().TenantName.ShouldBe("team");
        (await owner.Http.PostAsync($"/api/v1/tenant-invitations/{membershipId}/accept", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await Scenario.EnsureSuccess(await invitee.Http.PostAsync($"/api/v1/tenant-invitations/{membershipId}/accept", null, Ct));
        Scenario.UseTenant(invitee, tenantId);
        (await Scenario.Read<CurrentTenantResponse>(await invitee.Http.GetAsync("/api/v1/tenant", Ct))).Disciplines.ShouldBe([Discipline.Dietitian]);

        await Scenario.EnsureSuccess(await invitee.Http.PostAsJsonAsync($"/api/v1/tenant/memberships/{membershipId}/revoke", new { reason = "Moving on" }, Ct));
        (await invitee.Http.GetAsync("/api/v1/tenant", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Custom_exercises_stay_inside_their_tenant()
    {
        var (trainerA, _) = await Scenario.ProfessionalAsync("library-a");
        var (trainerB, _) = await Scenario.ProfessionalAsync("library-b");
        var create = await trainerA.Http.PostAsJsonAsync("/api/v1/exercises", new
        {
            name = $"Afundo búlgaro {Guid.NewGuid():N}", description = "Pé de trás apoiado no banco.", muscleGroups = new[] { "quadriceps" },
            equipment = new[] { "banco" },
        }, Ct);
        var exerciseId = (await Scenario.Read<Scenario.IdResponse>(create)).Id;

        (await Scenario.Read<PagedResponse<ExerciseResponse>>(await trainerA.Http.GetAsync("/api/v1/exercises?search=búlgaro", Ct))).Items
            .ShouldContain(e => e.Id == exerciseId);
        (await Scenario.Read<PagedResponse<ExerciseResponse>>(await trainerB.Http.GetAsync("/api/v1/exercises?search=búlgaro", Ct))).Items
            .ShouldNotContain(e => e.Id == exerciseId);
        (await trainerB.Http.GetAsync($"/api/v1/exercises/{exerciseId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Trainer B cannot prescribe it either.
        var client = await Scenario.ClientAsync();
        await Scenario.LinkAsync(trainerB, client);
        var planB = await Scenario.DraftPlanAsync(trainerB, client.Id);
        var useForeign = await trainerB.Http.PutAsJsonAsync($"/api/v1/training-plans/{planB}/sessions", new[]
        {
            new { name = "Treino", kind = "Strength", exercises = new[] { new { exerciseId, restSeconds = 60, sets = new[] { new { minRepetitions = 10 } } } } },
        }, Ct);
        useForeign.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await useForeign.Content.ReadAsStringAsync(Ct)).ShouldContain("Exercise.Unknown");

        // A client of tenant A can read it to perform the workout; the client of B cannot.
        var clientOfA = await Scenario.ClientAsync("client-of-a");
        await Scenario.LinkAsync(trainerA, clientOfA);
        (await clientOfA.Http.GetAsync($"/api/v1/exercises/{exerciseId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.Http.GetAsync($"/api/v1/exercises/{exerciseId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
