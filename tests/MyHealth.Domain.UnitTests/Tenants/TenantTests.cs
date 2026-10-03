using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.HealthData;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Domain.UnitTests.Tenants;

public sealed class TenantTests
{
    [Fact]
    public void Founder_becomes_an_active_administrator_with_the_chosen_disciplines()
    {
        var founder = TestData.Client("founder@example.com");
        var tenant = Tenant.Create(" Studio Forte ", TenantKind.Organization, founder.Id, TestData.Now);

        var membership = TenantMembership.Founder(tenant, founder, [Discipline.PersonalTrainer, Discipline.PersonalTrainer], TestData.Now);

        tenant.Name.ShouldBe("Studio Forte");
        membership.IsActive.ShouldBeTrue();
        membership.IsAdministrator.ShouldBeTrue();
        membership.UserId.ShouldBe(founder.Id);
        membership.Disciplines.ShouldBe([Discipline.PersonalTrainer]);
        membership.Practices(Discipline.PersonalTrainer).ShouldBeTrue();
        membership.Practices(Discipline.Dietitian).ShouldBeFalse();
    }

    [Fact]
    public void Only_the_invitee_can_accept_a_membership_invitation()
    {
        var invitation = TenantMembership.Invite(TestData.TenantId, " Pro@Example.com ", [Discipline.Dietitian], false, Guid.CreateVersion7(), TestData.Now);

        invitation.Accept(TestData.Client("intruder@example.com"), TestData.Now).Error.ShouldBe(MembershipErrors.NotInvitee);

        var invitee = TestData.Client("pro@example.com");
        invitation.Accept(invitee, TestData.Now).IsSuccess.ShouldBeTrue();
        invitation.UserId.ShouldBe(invitee.Id);
        invitation.Practices(Discipline.Dietitian).ShouldBeTrue();
    }

    [Fact]
    public void Revoking_an_active_membership_stops_its_disciplines_and_raises_an_event()
    {
        var founder = TestData.Client();
        var membership = TenantMembership.Founder(Tenant.Create("Solo", TenantKind.SoloPractice, founder.Id, TestData.Now), founder, [Discipline.PersonalTrainer], TestData.Now);
        var admin = Guid.CreateVersion7();

        membership.Revoke(admin, "Left the studio", TestData.Now).IsSuccess.ShouldBeTrue();

        membership.Practices(Discipline.PersonalTrainer).ShouldBeFalse();
        membership.Revoke(admin, null, TestData.Now).Error.ShouldBe(MembershipErrors.AlreadyClosed);
        var revoked = membership.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<TenantMembershipRevoked>();
        revoked.UserId.ShouldBe(founder.Id);
        revoked.RevokedBy.ShouldBe(admin);
    }

    [Fact]
    public void Revoking_a_pending_invitation_raises_no_event()
    {
        var invitation = TenantMembership.Invite(TestData.TenantId, "pro@example.com", [], false, Guid.CreateVersion7(), TestData.Now);

        invitation.Revoke(Guid.CreateVersion7(), null, TestData.Now).IsSuccess.ShouldBeTrue();

        invitation.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Relationship_ended_by_membership_revocation_revokes_grants_and_records_the_reason()
    {
        var client = TestData.Client();
        var relationship = TestData.ActiveRelationship(TestData.Professional(), client);
        relationship.Grant(client.Id, DataCategory.HeartRate, TestData.Now);
        var admin = Guid.CreateVersion7();

        relationship.EndBecauseMembershipRevoked(admin, TestData.Now).IsSuccess.ShouldBeTrue();

        relationship.Status.ShouldBe(RelationshipStatus.Ended);
        relationship.EndedBy.ShouldBe(admin);
        relationship.EndReason.ShouldBe(CareRelationship.ProfessionalLeftReason);
        relationship.HasActiveGrant(DataCategory.HeartRate).ShouldBeFalse();
    }

    [Fact]
    public void Records_inherit_the_tenant_of_the_plan_they_derive_from()
    {
        var trainer = TestData.Professional();
        var client = TestData.Client();
        var (plan, version) = TestData.PublishedPlan(trainer, client);

        var session = WorkoutSession.Start(
            Guid.CreateVersion7(), client.Id, version, version.Content.Sessions[0].Id, TestData.Now, TestData.SaoPaulo, TestData.Now).Value;
        var copy = plan.CopyAsDraft(trainer, Guid.CreateVersion7(), null, TestData.Now).Value;

        plan.TenantId.ShouldBe(TestData.TenantId);
        version.TenantId.ShouldBe(TestData.TenantId);
        session.TenantId.ShouldBe(TestData.TenantId);
        copy.TenantId.ShouldBe(TestData.TenantId);
        WorkoutHeartRate.Create(session.Id, session.TenantId, client.Id).TenantId.ShouldBe(TestData.TenantId);
    }

    [Fact]
    public void Custom_exercises_are_only_available_in_their_tenant()
    {
        var details = new ExerciseDetails("Afundo", "Passada à frente.", ["quadriceps"], [], null, null, null);
        var custom = Exercise.CreateCustom(TestData.TenantId, Guid.CreateVersion7(), details, TestData.Now);
        var system = Exercise.CreateSystem(details, TestData.Now);

        custom.IsAvailableIn(TestData.TenantId).ShouldBeTrue();
        custom.IsAvailableIn(Guid.CreateVersion7()).ShouldBeFalse();
        custom.IsAvailableIn(null).ShouldBeFalse();
        system.IsAvailableIn(Guid.CreateVersion7()).ShouldBeTrue();
        system.IsAvailableIn(null).ShouldBeTrue();
    }
}
