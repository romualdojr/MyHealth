using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Plans;

namespace MyHealth.Domain.UnitTests.Training;

public sealed class TrainingPlanTests
{
    [Fact]
    public void Only_verified_trainers_create_plans()
    {
        var result = TrainingPlan.Create(TestData.TenantId, TestData.Professional(verified: false), Guid.CreateVersion7(), TestData.Details(), TestData.Now);

        result.Error.ShouldBe(ProfessionalErrors.NotVerified);
    }

    [Fact]
    public void Dietitians_cannot_create_training_plans()
    {
        var result = TrainingPlan.Create(TestData.TenantId, TestData.Professional(Discipline.Dietitian), Guid.CreateVersion7(), TestData.Details(), TestData.Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Professional.MissingProfile");
    }

    [Fact]
    public void End_date_before_start_is_rejected()
    {
        var details = TestData.Details() with { EndDate = new DateOnly(2026, 9, 1) };

        TrainingPlan.Create(TestData.TenantId, TestData.Professional(), Guid.CreateVersion7(), details, TestData.Now).Error.ShouldBe(PlanErrors.InvalidDates);
    }

    [Fact]
    public void A_review_request_is_raised_once_and_cleared_by_acknowledging_or_publishing()
    {
        var trainer = TestData.Professional();
        var (plan, _) = TestData.PublishedPlan(trainer, TestData.Client());

        plan.RequestReview("Nova lesão", TestData.Now);
        plan.RequestReview("Outra mudança", TestData.Now);

        plan.DomainEvents.OfType<TrainingPlanReviewRequested>().Count().ShouldBe(1);
        (plan.NeedsReview, plan.ReviewReason).ShouldBe((true, "Nova lesão"));
        plan.AcknowledgeReview(TestData.Now).IsSuccess.ShouldBeTrue();
        plan.AcknowledgeReview(TestData.Now).Error.ShouldBe(PlanErrors.NoReviewPending);

        plan.RequestReview("Nova lesão", TestData.Now);
        plan.UpdateDraft(new PlanContent([TestData.StrengthSession("Treino B")]), TestData.Now);
        plan.Publish(trainer, "Ajuste pela lesão", TestData.Now).IsSuccess.ShouldBeTrue();
        plan.NeedsReview.ShouldBeFalse();
    }

    [Fact]
    public void Ended_plans_are_not_flagged_for_review()
    {
        var (plan, _) = TestData.PublishedPlan(TestData.Professional(), TestData.Client());
        plan.End(TestData.Now);

        plan.RequestReview("Nova lesão", TestData.Now);

        plan.NeedsReview.ShouldBeFalse();
        plan.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Publishing_an_empty_plan_fails()
    {
        var trainer = TestData.Professional();
        var plan = TrainingPlan.Create(TestData.TenantId, trainer, Guid.CreateVersion7(), TestData.Details(), TestData.Now).Value;

        plan.Publish(trainer, "v1", TestData.Now).Error.ShouldBe(PlanErrors.NoSessions);
    }

    [Fact]
    public void Strength_session_without_exercises_cannot_be_published()
    {
        var trainer = TestData.Professional();
        var plan = TrainingPlan.Create(TestData.TenantId, trainer, Guid.CreateVersion7(), TestData.Details(), TestData.Now).Value;
        plan.UpdateDraft(new PlanContent([new PlannedSession(Guid.CreateVersion7(), "A", SessionKind.Strength, 1, null, [], null)]), TestData.Now);

        plan.Publish(trainer, "v1", TestData.Now).Error.Code.ShouldBe("TrainingPlan.IncompleteSession");
    }

    [Fact]
    public void Publishing_creates_numbered_immutable_versions()
    {
        var trainer = TestData.Professional();
        var client = TestData.Client();
        var (plan, v1) = TestData.PublishedPlan(trainer, client);
        var sessionB = TestData.StrengthSession("Treino B");

        plan.UpdateDraft(new PlanContent([.. v1.Content.Sessions, sessionB]), TestData.Now.AddDays(7));
        var v2 = plan.Publish(trainer, "Adicionado treino B", TestData.Now.AddDays(7)).Value;

        v1.VersionNumber.ShouldBe(1);
        v1.Content.Sessions.Count.ShouldBe(1);
        v2.VersionNumber.ShouldBe(2);
        v2.Content.Sessions.Count.ShouldBe(2);
        plan.CurrentVersionId.ShouldBe(v2.Id);
        plan.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<TrainingPlanPublished>().VersionNumber.ShouldBe(2);
    }

    [Fact]
    public void Republishing_without_changes_is_rejected()
    {
        var trainer = TestData.Professional();
        var (plan, _) = TestData.PublishedPlan(trainer, TestData.Client());

        plan.Publish(trainer, "Nada mudou", TestData.Now).Error.ShouldBe(PlanErrors.NothingToPublish);
    }

    [Fact]
    public void Another_trainer_cannot_publish()
    {
        var (plan, _) = TestData.PublishedPlan(TestData.Professional(), TestData.Client());
        plan.UpdateDetails(TestData.Details("Novo nome"), TestData.Now);

        plan.Publish(TestData.Professional(), "x", TestData.Now).Error.ShouldBe(PlanErrors.NotOwner);
    }

    [Fact]
    public void Ended_plan_cannot_change()
    {
        var trainer = TestData.Professional();
        var (plan, _) = TestData.PublishedPlan(trainer, TestData.Client());
        plan.End(TestData.Now);

        plan.UpdateDraft(PlanContent.Empty, TestData.Now).Error.ShouldBe(PlanErrors.Ended);
    }

    [Fact]
    public void Copy_starts_a_new_draft_and_leaves_source_untouched()
    {
        var trainer = TestData.Professional();
        var (plan, v1) = TestData.PublishedPlan(trainer, TestData.Client());
        var otherClient = Guid.CreateVersion7();

        var copy = plan.CopyAsDraft(trainer, otherClient, "Cópia", TestData.Now).Value;

        copy.Id.ShouldNotBe(plan.Id);
        copy.ClientId.ShouldBe(otherClient);
        copy.Status.ShouldBe(PlanStatus.Draft);
        copy.CurrentVersionNumber.ShouldBe(0);
        copy.CopiedFromPlanId.ShouldBe(plan.Id);
        copy.Draft.ShouldBe(plan.Draft);
        copy.Details.Name.ShouldBe("Cópia");
        plan.CurrentVersionId.ShouldBe(v1.Id);
    }

    [Fact]
    public void Duplicate_session_ids_are_rejected()
    {
        var trainer = TestData.Professional();
        var plan = TrainingPlan.Create(TestData.TenantId, trainer, Guid.CreateVersion7(), TestData.Details(), TestData.Now).Value;
        var session = TestData.StrengthSession();

        plan.UpdateDraft(new PlanContent([session, session]), TestData.Now).Error.Code.ShouldBe("TrainingPlan.DuplicateIds");
    }
}
