using MyHealth.Application.Common;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Professionals;

namespace MyHealth.Application.UnitTests.Common;

public sealed class CareAccessTests
{
    private readonly IProfessionalProfileRepository _professionals = Substitute.For<IProfessionalProfileRepository>();
    private readonly ICareRelationshipRepository _relationships = Substitute.For<ICareRelationshipRepository>();

    private CareAccess AccessFor(Guid userId) => new(Fixtures.UserContext(userId), _professionals, _relationships);

    [Fact]
    public async Task User_without_a_profile_for_the_discipline_is_denied()
    {
        var result = await AccessFor(Guid.CreateVersion7())
            .GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: false, TestContext.Current.CancellationToken);

        result.Error.Code.ShouldBe("Professional.MissingProfile");
    }

    [Fact]
    public async Task Unverified_professional_is_denied_when_verification_is_required()
    {
        var trainer = Fixtures.Trainer(verified: false);
        _professionals.GetByUserAsync(trainer.UserId, Discipline.PersonalTrainer, Arg.Any<CancellationToken>()).Returns(trainer);

        var result = await AccessFor(trainer.UserId)
            .GetCurrentProfessionalAsync(Discipline.PersonalTrainer, requireVerified: true, TestContext.Current.CancellationToken);

        result.Error.ShouldBe(ProfessionalErrors.NotVerified);
    }

    [Fact]
    public async Task Shared_data_requires_an_active_grant()
    {
        var trainer = Fixtures.Trainer();
        var relationship = Fixtures.ActiveRelationship(trainer, Fixtures.Client());
        _relationships.GetByIdAsync(relationship.Id, Arg.Any<CancellationToken>()).Returns(relationship);

        var result = await AccessFor(trainer.UserId).GetSharedAccessAsync(relationship.Id, DataCategory.HeartRate, TestContext.Current.CancellationToken);

        result.Error.ShouldBe(CareRelationshipErrors.SharingNotGranted);
    }

    [Fact]
    public async Task Shared_data_is_available_with_an_active_grant()
    {
        var trainer = Fixtures.Trainer();
        var client = Fixtures.Client();
        var relationship = Fixtures.ActiveRelationship(trainer, client);
        relationship.Grant(client.Id, DataCategory.HeartRate, Fixtures.Now);
        _relationships.GetByIdAsync(relationship.Id, Arg.Any<CancellationToken>()).Returns(relationship);

        var result = await AccessFor(trainer.UserId).GetSharedAccessAsync(relationship.Id, DataCategory.HeartRate, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Another_professional_cannot_use_someone_elses_relationship()
    {
        var client = Fixtures.Client();
        var relationship = Fixtures.ActiveRelationship(Fixtures.Trainer(), client);
        relationship.Grant(client.Id, DataCategory.HeartRate, Fixtures.Now);
        _relationships.GetByIdAsync(relationship.Id, Arg.Any<CancellationToken>()).Returns(relationship);

        var result = await AccessFor(Guid.CreateVersion7()).GetSharedAccessAsync(relationship.Id, DataCategory.HeartRate, TestContext.Current.CancellationToken);

        result.Error.ShouldBe(CareRelationshipErrors.NotFound);
    }
}
