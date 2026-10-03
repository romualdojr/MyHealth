using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.UnitTests.Professionals;

public sealed class ProfessionalProfileTests
{
    [Fact]
    public void New_profile_is_pending_verification()
    {
        TestData.Professional(verified: false).VerificationStatus.ShouldBe(VerificationStatus.Pending);
    }

    [Fact]
    public void Verification_raises_event()
    {
        var profile = TestData.Professional(verified: false);

        profile.Verify(Guid.CreateVersion7(), "CREF conferido", TestData.Now).IsSuccess.ShouldBeTrue();

        profile.IsVerified.ShouldBeTrue();
        profile.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<ProfessionalVerified>();
    }

    [Fact]
    public void Changing_registration_requires_new_verification()
    {
        var profile = TestData.Professional();

        profile.UpdateDetails("Carlos Lima", null, null, ProfessionalRegistration.Create("CREF", "999999-G", "RJ"));

        profile.VerificationStatus.ShouldBe(VerificationStatus.Pending);
    }

    [Fact]
    public void Changing_only_display_details_keeps_verification()
    {
        var profile = TestData.Professional();

        profile.UpdateDetails("Carlos A. Lima", "Bio", null, ProfessionalRegistration.Create("cref ", "012345-g", "sp"));

        profile.IsVerified.ShouldBeTrue();
    }

    [Fact]
    public void Rejection_requires_reason()
    {
        TestData.Professional(verified: false).Reject(Guid.CreateVersion7(), " ").Error.ShouldBe(ProfessionalErrors.ReasonRequired);
    }
}
