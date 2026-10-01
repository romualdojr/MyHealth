using MyHealth.Domain.Assessments;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.UnitTests.Assessments;

public sealed class MeasurementTests
{
    [Fact]
    public void Codes_are_normalised_and_units_must_be_known()
    {
        Measurement.Create(" Body Mass ", "digital-scale", "kg", 70m).Value.ShouldBe(new Measurement("body_mass", "digital_scale", "kg", 70m));
        Measurement.Create("body_mass", "scale", "lb", 150m).Error.Code.ShouldBe("Measurement.UnknownUnit");
        Measurement.Create("1x", "scale", "kg", 1m).Error.ShouldBe(MeasurementErrors.InvalidCode);
        Measurement.Create("body_mass", "scale", "kg", -1m).Error.ShouldBe(MeasurementErrors.OutOfRange);
    }

    [Fact]
    public void A_metric_appears_once_per_method_but_may_be_measured_with_two_methods()
    {
        Measurement.CreateMany([("body_fat_percentage", "bioimpedance", "%", 20m), ("body_fat_percentage", "bioimpedance", "%", 21m)])
            .Error.ShouldBe(MeasurementErrors.Duplicate);
        Measurement.CreateMany([("body_fat_percentage", "bioimpedance", "%", 20m), ("body_fat_percentage", "skinfold_jackson_pollock_7", "%", 18m)])
            .Value.Count.ShouldBe(2);
    }
}

public sealed class PhysicalAssessmentTests
{
    private static readonly IReadOnlyList<Measurement> Weight = [new("body_mass", "digital_scale", "kg", 80m)];

    [Fact]
    public void Only_verified_trainers_record_assessments()
    {
        var client = TestData.Client();
        PhysicalAssessment.Record(TestData.TenantId, TestData.Professional(Discipline.Dietitian), client.Id, new DateOnly(2026, 9, 1), Weight, null, TestData.Now)
            .Error.Code.ShouldBe("Professional.MissingProfile");
        PhysicalAssessment.Record(TestData.TenantId, TestData.Professional(verified: false), client.Id, new DateOnly(2026, 9, 1), Weight, null, TestData.Now)
            .Error.ShouldBe(ProfessionalErrors.NotVerified);
    }

    [Fact]
    public void An_assessment_needs_measurements_and_cannot_be_dated_in_the_future()
    {
        var trainer = TestData.Professional();
        PhysicalAssessment.Record(TestData.TenantId, trainer, Guid.CreateVersion7(), new DateOnly(2026, 9, 1), [], null, TestData.Now)
            .Error.ShouldBe(AssessmentErrors.NoMeasurements);
        PhysicalAssessment.Record(TestData.TenantId, trainer, Guid.CreateVersion7(), new DateOnly(2026, 10, 5), Weight, null, TestData.Now)
            .Error.ShouldBe(AssessmentErrors.FutureDate);
    }

    [Fact]
    public void Corrections_keep_the_replaced_values_with_reason_and_only_the_assessor_can_correct()
    {
        var trainer = TestData.Professional();
        var assessment = PhysicalAssessment.Record(TestData.TenantId, trainer, Guid.CreateVersion7(), new DateOnly(2026, 9, 1), Weight, "ok", TestData.Now).Value;
        assessment.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<PhysicalAssessmentRecorded>();

        assessment.Correct(TestData.Professional(), new DateOnly(2026, 9, 1), Weight, null, "x", TestData.Now).Error.ShouldBe(AssessmentErrors.NotAssessor);
        assessment.Correct(trainer, new DateOnly(2026, 9, 1), Weight, null, " ", TestData.Now).Error.ShouldBe(AssessmentErrors.ReasonRequired);

        assessment.Correct(trainer, new DateOnly(2026, 9, 2), [new("body_mass", "digital_scale", "kg", 81m)], null, "Data errada", TestData.Now).IsSuccess.ShouldBeTrue();

        assessment.Version.ShouldBe(2);
        assessment.Measurements.ShouldHaveSingleItem().Value.ShouldBe(81m);
        var correction = assessment.Corrections.ShouldHaveSingleItem();
        (correction.ReplacedVersion, correction.PreviousAssessedOn, correction.PreviousNotes, correction.Reason)
            .ShouldBe((1, new DateOnly(2026, 9, 1), "ok", "Data errada"));
        correction.PreviousMeasurements.ShouldHaveSingleItem().Value.ShouldBe(80m);
    }
}

public sealed class HealthHistoryTests
{
    private static HealthHistoryContent Content(string? limitations = null, string? injuries = null, string goals = "Saúde") =>
        new(goals, null, limitations, injuries, "Manhãs", null);

    [Fact]
    public void Every_submission_must_be_acknowledged()
    {
        var history = HealthHistory.Start(Guid.CreateVersion7(), TestData.Now);

        history.Submit(Content(), acknowledged: false, TestData.Now).Error.ShouldBe(HealthHistoryErrors.AcknowledgementRequired);
        history.Revisions.ShouldBeEmpty();
    }

    [Fact]
    public void Identical_content_does_not_create_a_revision_so_retries_are_safe()
    {
        var history = HealthHistory.Start(Guid.CreateVersion7(), TestData.Now);
        history.Submit(Content(), true, TestData.Now);
        history.ClearDomainEvents();

        var again = history.Submit(Content() with { Availability = "  Manhãs " }, true, TestData.Now.AddMinutes(1));

        again.Value.Number.ShouldBe(1);
        history.Revisions.Count.ShouldBe(1);
        history.DomainEvents.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, null, "Maratona", false)]
    [InlineData("Hipertensão controlada", null, "Saúde", true)]
    [InlineData(null, "Entorse no tornozelo", "Saúde", true)]
    public void Only_changes_to_limitations_or_injuries_are_significant(string? limitations, string? injuries, string goals, bool significant)
    {
        var history = HealthHistory.Start(Guid.CreateVersion7(), TestData.Now);
        history.Submit(Content(), true, TestData.Now);
        history.ClearDomainEvents();

        var revision = history.Submit(Content(limitations, injuries, goals), true, TestData.Now.AddDays(1)).Value;

        revision.Number.ShouldBe(2);
        revision.IsSignificantChange.ShouldBe(significant);
        history.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<HealthHistoryRevised>().IsSignificantChange.ShouldBe(significant);
        history.Revisions.Single(r => r.Number == 1).Content.ShouldBe(Content());
    }

    [Fact]
    public void Reviews_are_idempotent_per_professional_and_organization_and_need_a_verified_reviewer()
    {
        var history = HealthHistory.Start(Guid.CreateVersion7(), TestData.Now);
        history.Submit(Content(), true, TestData.Now);
        var trainer = TestData.Professional();

        history.Review(TestData.TenantId, TestData.Professional(verified: false), 1, null, TestData.Now).Error.ShouldBe(ProfessionalErrors.NotVerified);
        history.Review(TestData.TenantId, trainer, 2, null, TestData.Now).Error.ShouldBe(HealthHistoryErrors.RevisionNotFound);
        history.Review(TestData.TenantId, trainer, 1, "ok", TestData.Now).IsSuccess.ShouldBeTrue();
        history.Review(TestData.TenantId, trainer, 1, "ok", TestData.Now).IsSuccess.ShouldBeTrue();
        history.Review(Guid.CreateVersion7(), trainer, 1, null, TestData.Now).IsSuccess.ShouldBeTrue();

        history.Reviews.Count.ShouldBe(2);
    }
}
