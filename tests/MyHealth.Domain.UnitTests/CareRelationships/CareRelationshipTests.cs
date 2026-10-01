using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.UnitTests.CareRelationships;

public sealed class CareRelationshipTests
{
    [Fact]
    public void Unverified_professional_cannot_invite()
    {
        var result = CareRelationship.Invite(TestData.Professional(verified: false), "client@example.com", TestData.Now);

        result.Error.ShouldBe(ProfessionalErrors.NotVerified);
    }

    [Fact]
    public void Invitee_accepts_and_relationship_becomes_active()
    {
        var client = TestData.Client();
        var relationship = CareRelationship.Invite(TestData.Professional(), " Client@Example.com ", TestData.Now).Value;

        var result = relationship.Accept(client, TestData.Now);

        result.IsSuccess.ShouldBeTrue();
        relationship.Status.ShouldBe(RelationshipStatus.Active);
        relationship.ClientId.ShouldBe(client.Id);
        relationship.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<CareRelationshipActivated>();
    }

    [Fact]
    public void Someone_else_cannot_accept_the_invitation()
    {
        var relationship = CareRelationship.Invite(TestData.Professional(), "client@example.com", TestData.Now).Value;

        var result = relationship.Accept(TestData.Client("intruder@example.com"), TestData.Now);

        result.Error.ShouldBe(CareRelationshipErrors.NotInvitee);
        relationship.Status.ShouldBe(RelationshipStatus.Pending);
    }

    [Fact]
    public void Only_the_client_manages_sharing()
    {
        var professional = TestData.Professional();
        var relationship = TestData.ActiveRelationship(professional, TestData.Client());

        var result = relationship.Grant(professional.UserId, DataCategory.HeartRate, TestData.Now);

        result.Error.ShouldBe(CareRelationshipErrors.OnlyClientManagesSharing);
        relationship.HasActiveGrant(DataCategory.HeartRate).ShouldBeFalse();
    }

    [Fact]
    public void Revoking_and_granting_again_keeps_history()
    {
        var client = TestData.Client();
        var relationship = TestData.ActiveRelationship(TestData.Professional(), client);

        relationship.Grant(client.Id, DataCategory.HeartRate, TestData.Now);
        relationship.Revoke(client.Id, DataCategory.HeartRate, TestData.Now.AddDays(1));
        relationship.HasActiveGrant(DataCategory.HeartRate).ShouldBeFalse();
        relationship.Grant(client.Id, DataCategory.HeartRate, TestData.Now.AddDays(2));

        relationship.HasActiveGrant(DataCategory.HeartRate).ShouldBeTrue();
        relationship.Grants.Count.ShouldBe(2);
        relationship.Grants.Count(g => g.RevokedAt is not null).ShouldBe(1);
    }

    [Fact]
    public void Granting_twice_is_idempotent()
    {
        var client = TestData.Client();
        var relationship = TestData.ActiveRelationship(TestData.Professional(), client);

        relationship.Grant(client.Id, DataCategory.HeartRate, TestData.Now);
        relationship.Grant(client.Id, DataCategory.HeartRate, TestData.Now);

        relationship.Grants.ShouldHaveSingleItem();
    }

    [Fact]
    public void Ending_revokes_all_grants_and_stops_access()
    {
        var client = TestData.Client();
        var relationship = TestData.ActiveRelationship(TestData.Professional(), client);
        relationship.Grant(client.Id, DataCategory.HeartRate, TestData.Now);

        var result = relationship.End(client.Id, "Mudei de cidade", TestData.Now.AddDays(3));

        result.IsSuccess.ShouldBeTrue();
        relationship.Status.ShouldBe(RelationshipStatus.Ended);
        relationship.HasActiveGrant(DataCategory.HeartRate).ShouldBeFalse();
        relationship.Grants.ShouldAllBe(g => g.RevokedAt != null);
    }

    [Fact]
    public void Outsider_cannot_end_the_relationship()
    {
        var relationship = TestData.ActiveRelationship(TestData.Professional(), TestData.Client());

        relationship.End(Guid.CreateVersion7(), null, TestData.Now).Error.ShouldBe(CareRelationshipErrors.NotParticipant);
    }
}
