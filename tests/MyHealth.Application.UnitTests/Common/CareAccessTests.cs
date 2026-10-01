using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Application.Common;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;

namespace MyHealth.Application.UnitTests.Common;

public sealed class CareAccessTests
{
    private readonly IProfessionalProfileRepository _professionals = Substitute.For<IProfessionalProfileRepository>();
    private readonly ICareRelationshipRepository _relationships = Substitute.For<ICareRelationshipRepository>();
    private readonly ITenantMembershipRepository _memberships = Substitute.For<ITenantMembershipRepository>();
    private readonly Abstractions.Auditing.IAuditLog _audit = Fixtures.AuditLog();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CareAccess AccessFor(Guid userId, Guid? tenantId = null)
    {
        var user = Fixtures.UserContext(userId, requestedTenantId: tenantId ?? Fixtures.TenantId);
        return new CareAccess(user, new TenantContext(user, _memberships, _audit), _professionals, _relationships, _audit);
    }

    /// <summary>Makes <paramref name="userId"/> an active member of the fixture tenant.</summary>
    private void MemberOf(Guid userId, params Discipline[] disciplines)
    {
        var membership = TenantMembership.Invite(Fixtures.TenantId, "member@example.com", disciplines, false, Guid.CreateVersion7(), Fixtures.Now);
        membership.Accept(Domain.Users.UserProfile.Create(userId, "Member", "member@example.com", "America/Sao_Paulo", Fixtures.Now).Value, Fixtures.Now);
        _memberships.GetActiveAsync(Fixtures.TenantId, userId, Arg.Any<CancellationToken>()).Returns(membership);
    }

    [Fact]
    public async Task Acting_as_a_professional_requires_a_selected_tenant()
    {
        var userId = Guid.CreateVersion7();
        var user = Fixtures.UserContext(userId);
        var access = new CareAccess(user, new TenantContext(user, _memberships, _audit), _professionals, _relationships, _audit);

        var result = await access.GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: false, Ct);

        result.Error.ShouldBe(TenantErrors.Required);
    }

    [Fact]
    public async Task A_tenant_the_caller_does_not_belong_to_is_not_found_and_audited()
    {
        var trainer = Fixtures.Trainer();
        _professionals.GetByUserAsync(trainer.UserId, Discipline.PersonalTrainer, Arg.Any<CancellationToken>()).Returns(trainer);
        var otherTenant = Guid.CreateVersion7();

        var result = await AccessFor(trainer.UserId, otherTenant).GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: false, Ct);

        result.Error.ShouldBe(TenantErrors.NotFound);
        await _audit.Received(1).RecordDeniedAsync(nameof(Tenant), otherTenant, otherTenant, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Membership_must_include_the_discipline()
    {
        var trainer = Fixtures.Trainer();
        MemberOf(trainer.UserId, Discipline.Dietitian);
        _professionals.GetByUserAsync(trainer.UserId, Discipline.PersonalTrainer, Arg.Any<CancellationToken>()).Returns(trainer);

        var result = await AccessFor(trainer.UserId).GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: false, Ct);

        result.Error.ShouldBe(TenantErrors.DisciplineNotInMembership);
    }

    [Fact]
    public async Task User_without_a_profile_for_the_discipline_is_denied()
    {
        var userId = Guid.CreateVersion7();
        MemberOf(userId, Discipline.PersonalTrainer);

        var result = await AccessFor(userId).GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: false, Ct);

        result.Error.Code.ShouldBe("Professional.MissingProfile");
    }

    [Fact]
    public async Task Unverified_professional_is_denied_when_verification_is_required()
    {
        var trainer = Fixtures.Trainer(verified: false);
        MemberOf(trainer.UserId, Discipline.PersonalTrainer);
        _professionals.GetByUserAsync(trainer.UserId, Discipline.PersonalTrainer, Arg.Any<CancellationToken>()).Returns(trainer);

        var result = await AccessFor(trainer.UserId).GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: true, Ct);

        result.Error.ShouldBe(ProfessionalErrors.NotVerified);
    }

    [Fact]
    public async Task Acting_professional_carries_the_selected_tenant()
    {
        var trainer = Fixtures.Trainer();
        MemberOf(trainer.UserId, Discipline.PersonalTrainer);
        _professionals.GetByUserAsync(trainer.UserId, Discipline.PersonalTrainer, Arg.Any<CancellationToken>()).Returns(trainer);

        var result = await AccessFor(trainer.UserId).GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: true, Ct);

        result.Value.TenantId.ShouldBe(Fixtures.TenantId);
        result.Value.Profile.ShouldBe(trainer);
    }

    [Fact]
    public async Task Shared_data_requires_an_active_grant()
    {
        var trainer = Fixtures.Trainer();
        MemberOf(trainer.UserId, Discipline.PersonalTrainer);
        var relationship = Fixtures.ActiveRelationship(trainer, Fixtures.Client());
        _relationships.GetByIdAsync(relationship.Id, Arg.Any<CancellationToken>()).Returns(relationship);

        var result = await AccessFor(trainer.UserId).GetSharedAccessAsync(relationship.Id, DataCategory.HeartRate, Ct);

        result.Error.ShouldBe(CareRelationshipErrors.SharingNotGranted);
    }

    [Fact]
    public async Task Shared_data_is_available_with_an_active_grant()
    {
        var trainer = Fixtures.Trainer();
        MemberOf(trainer.UserId, Discipline.PersonalTrainer);
        var client = Fixtures.Client();
        var relationship = Fixtures.ActiveRelationship(trainer, client);
        relationship.Grant(client.Id, DataCategory.HeartRate, Fixtures.Now);
        _relationships.GetByIdAsync(relationship.Id, Arg.Any<CancellationToken>()).Returns(relationship);

        var result = await AccessFor(trainer.UserId).GetSharedAccessAsync(relationship.Id, DataCategory.HeartRate, Ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Another_professional_cannot_use_someone_elses_relationship()
    {
        var client = Fixtures.Client();
        var relationship = Fixtures.ActiveRelationship(Fixtures.Trainer(), client);
        relationship.Grant(client.Id, DataCategory.HeartRate, Fixtures.Now);
        _relationships.GetByIdAsync(relationship.Id, Arg.Any<CancellationToken>()).Returns(relationship);
        var stranger = Guid.CreateVersion7();
        MemberOf(stranger, Discipline.PersonalTrainer);

        var result = await AccessFor(stranger).GetSharedAccessAsync(relationship.Id, DataCategory.HeartRate, Ct);

        result.Error.ShouldBe(CareRelationshipErrors.NotFound);
        await _audit.Received(1).RecordDeniedAsync(nameof(CareRelationship), relationship.Id, Fixtures.TenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_professionals_own_relationship_is_not_usable_from_another_tenant()
    {
        var trainer = Fixtures.Trainer();
        var client = Fixtures.Client();
        var relationship = Fixtures.ActiveRelationship(trainer, client);
        relationship.Grant(client.Id, DataCategory.HeartRate, Fixtures.Now);
        _relationships.GetByIdAsync(relationship.Id, Arg.Any<CancellationToken>()).Returns(relationship);

        // The same professional, acting for a second tenant they also belong to.
        var secondTenant = Guid.CreateVersion7();
        var membership = TenantMembership.Invite(secondTenant, "member@example.com", [Discipline.PersonalTrainer], false, Guid.CreateVersion7(), Fixtures.Now);
        membership.Accept(Domain.Users.UserProfile.Create(trainer.UserId, "Member", "member@example.com", "America/Sao_Paulo", Fixtures.Now).Value, Fixtures.Now);
        _memberships.GetActiveAsync(secondTenant, trainer.UserId, Arg.Any<CancellationToken>()).Returns(membership);

        var result = await AccessFor(trainer.UserId, secondTenant).GetSharedAccessAsync(relationship.Id, DataCategory.HeartRate, Ct);

        result.Error.ShouldBe(CareRelationshipErrors.NotFound);
    }

    [Fact]
    public async Task Tenant_resolution_happens_once_per_request()
    {
        var userId = Guid.CreateVersion7();
        MemberOf(userId, Discipline.PersonalTrainer);
        var user = Fixtures.UserContext(userId, requestedTenantId: Fixtures.TenantId);
        var tenant = new TenantContext(user, _memberships, _audit);

        (await tenant.RequireAsync(Ct)).IsSuccess.ShouldBeTrue();
        (await tenant.FindAsync(Ct)).Value!.TenantId.ShouldBe(Fixtures.TenantId);

        await _memberships.Received(1).GetActiveAsync(Fixtures.TenantId, userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_selected_tenant_is_not_an_error_for_client_views()
    {
        var user = Fixtures.UserContext(Guid.CreateVersion7());

        Result<ActiveMembership?> found = await new TenantContext(user, _memberships, _audit).FindAsync(Ct);

        found.IsSuccess.ShouldBeTrue();
        found.Value.ShouldBeNull();
    }
}
