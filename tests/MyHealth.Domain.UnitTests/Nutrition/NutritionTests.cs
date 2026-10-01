using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.UnitTests.Nutrition;

public sealed class MealPlanTests
{
    private static readonly MealPlanDetails Details = new("Plano", "Energia", new DateOnly(2026, 10, 1), null, null);

    private static MealPlanContent Breakfast(string food = "Aveia") =>
        new([new PlannedMeal(Guid.CreateVersion7(), "Café", new TimeOnly(7, 0), 1, null, [new MealItem(Guid.CreateVersion7(), food, 30m, "g", null, null, [])])]);

    [Fact]
    public void Only_verified_dietitians_prescribe()
    {
        var clientId = Guid.CreateVersion7();
        MealPlan.Create(TestData.TenantId, TestData.Professional(), clientId, Details, TestData.Now).Error.Code.ShouldBe("Professional.MissingProfile");
        MealPlan.Create(TestData.TenantId, TestData.Professional(Discipline.Dietitian, verified: false), clientId, Details, TestData.Now)
            .Error.ShouldBe(ProfessionalErrors.NotVerified);
    }

    [Fact]
    public void Publishing_freezes_numbered_versions_and_notifies_the_client()
    {
        var dietitian = TestData.Professional(Discipline.Dietitian);
        var plan = MealPlan.Create(TestData.TenantId, dietitian, Guid.CreateVersion7(), Details, TestData.Now).Value;
        plan.UpdateDraft(Breakfast(), TestData.Now);

        var v1 = plan.Publish(dietitian, "Inicial", TestData.Now).Value;
        plan.Publish(dietitian, "De novo", TestData.Now).Error.ShouldBe(MealPlanErrors.NothingToPublish);
        plan.UpdateDraft(Breakfast("Tapioca"), TestData.Now);
        var v2 = plan.Publish(dietitian, "Troca", TestData.Now).Value;

        (v1.VersionNumber, v2.VersionNumber, plan.CurrentVersionNumber).ShouldBe((1, 2, 2));
        v1.Content.Meals[0].Items[0].Food.ShouldBe("Aveia");
        v2.Content.Meals[0].Items[0].Food.ShouldBe("Tapioca");
        plan.DomainEvents.OfType<MealPlanPublished>().Count().ShouldBe(2);
    }

    [Fact]
    public void Another_dietitian_or_a_suspended_one_cannot_publish()
    {
        var dietitian = TestData.Professional(Discipline.Dietitian);
        var plan = MealPlan.Create(TestData.TenantId, dietitian, Guid.CreateVersion7(), Details, TestData.Now).Value;
        plan.UpdateDraft(Breakfast(), TestData.Now);

        plan.Publish(TestData.Professional(Discipline.Dietitian), "x", TestData.Now).Error.ShouldBe(MealPlanErrors.NotOwner);
        dietitian.Suspend(Guid.CreateVersion7(), "Registro vencido");
        plan.Publish(dietitian, "x", TestData.Now).Error.ShouldBe(ProfessionalErrors.NotVerified);
    }

    [Fact]
    public void Items_need_a_portion_and_published_meals_need_foods()
    {
        var item = new MealItem(Guid.CreateVersion7(), "Fruta", null, null, null, null, []);
        new MealPlanContent([new PlannedMeal(Guid.CreateVersion7(), "Lanche", null, 1, null, [item])]).ValidateStructure().Error.Code.ShouldBe("MealPlan.ItemWithoutPortion");
        new MealPlanContent([new PlannedMeal(Guid.CreateVersion7(), "Lanche", null, 1, null, [item with { HouseholdMeasure = "1 unidade" }])])
            .ValidateStructure().IsSuccess.ShouldBeTrue();
        new MealPlanContent([new PlannedMeal(Guid.CreateVersion7(), "Lanche", null, 1, null, [])]).ValidateForPublishing().Error.Code.ShouldBe("MealPlan.EmptyMeal");
        MealPlanContent.Empty.ValidateForPublishing().Error.ShouldBe(MealPlanErrors.NoMeals);
    }
}

public sealed class FoodDiaryEntryTests
{
    private static FoodDiaryDetails Details(string? description = "Arroz e feijão", int? water = null) =>
        new("Almoço", TestData.Now.AddHours(-1), TestData.SaoPaulo, description, water, MealAdherence.AsPlanned, null);

    [Fact]
    public void An_entry_needs_something_to_record_and_cannot_be_in_the_future()
    {
        FoodDiaryEntry.Record(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Details(description: null), TestData.Now).Error.ShouldBe(FoodDiaryErrors.Empty);
        FoodDiaryEntry.Record(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Details(water: 0), TestData.Now).Error.ShouldBe(FoodDiaryErrors.InvalidWater);
        FoodDiaryEntry.Record(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Details() with { EatenAt = TestData.Now.AddHours(1) }, TestData.Now)
            .Error.ShouldBe(FoodDiaryErrors.FutureTimestamp);
        FoodDiaryEntry.Record(Guid.Empty, Guid.CreateVersion7(), null, Details(), TestData.Now).Error.ShouldBe(FoodDiaryErrors.InvalidId);
    }

    [Fact]
    public void The_local_date_follows_the_clients_time_zone()
    {
        var lateEvening = new DateTimeOffset(2026, 9, 30, 1, 30, 0, TimeSpan.Zero); // 22:30 on 29 Sep in São Paulo

        var entry = FoodDiaryEntry.Record(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Details() with { EatenAt = lateEvening }, TestData.Now).Value;

        entry.LocalDate.ShouldBe(new DateOnly(2026, 9, 29));
    }

    [Fact]
    public void Only_the_dietitian_it_was_shared_with_can_comment()
    {
        var dietitian = TestData.Professional(Discipline.Dietitian);
        var addressee = new DiaryAddressee(TestData.TenantId, dietitian.Id, null, null, null);
        var shared = FoodDiaryEntry.Record(Guid.CreateVersion7(), Guid.CreateVersion7(), addressee, Details(), TestData.Now).Value;
        var privateEntry = FoodDiaryEntry.Record(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Details(), TestData.Now).Value;

        privateEntry.Comment(dietitian, "Oi", TestData.Now).Error.ShouldBe(FoodDiaryErrors.NotAddressee);
        shared.Comment(TestData.Professional(Discipline.Dietitian), "Oi", TestData.Now).Error.ShouldBe(FoodDiaryErrors.NotAddressee);
        shared.Comment(dietitian, "Muito bem", TestData.Now).IsSuccess.ShouldBeTrue();

        shared.Comments.ShouldHaveSingleItem().Message.ShouldBe("Muito bem");
        shared.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<FoodDiaryCommented>();
    }

    [Fact]
    public void Revising_keeps_the_comments_and_another_client_cannot_revise()
    {
        var dietitian = TestData.Professional(Discipline.Dietitian);
        var clientId = Guid.CreateVersion7();
        var entry = FoodDiaryEntry.Record(
            Guid.CreateVersion7(), clientId, new DiaryAddressee(TestData.TenantId, dietitian.Id, null, null, null), Details(), TestData.Now).Value;
        entry.Comment(dietitian, "Ok", TestData.Now);

        entry.Revise(Guid.CreateVersion7(), Details("Outro"), TestData.Now).Error.ShouldBe(FoodDiaryErrors.NotFound);
        entry.Revise(clientId, Details("Arroz, feijão e salada"), TestData.Now).IsSuccess.ShouldBeTrue();

        entry.Description.ShouldBe("Arroz, feijão e salada");
        entry.Comments.Count.ShouldBe(1);
    }
}

public sealed class NutritionAssessmentTests
{
    private static NutritionAssessmentContent Content(DateOnly? followUp = null) =>
        new(new DateOnly(2026, 9, 29), "Energia", null, "Amendoim", null, null, [], null, followUp, null);

    [Fact]
    public void Only_verified_dietitians_assess_and_follow_up_cannot_precede_the_assessment()
    {
        NutritionAssessment.Record(TestData.TenantId, TestData.Professional(), Guid.CreateVersion7(), Content(), TestData.Now).Error.Code.ShouldBe("Professional.MissingProfile");
        NutritionAssessment.Record(TestData.TenantId, TestData.Professional(Discipline.Dietitian), Guid.CreateVersion7(), Content(new DateOnly(2026, 9, 1)), TestData.Now)
            .Error.ShouldBe(NutritionErrors.FollowUpBeforeAssessment);
    }

    [Fact]
    public void Corrections_keep_the_previous_content()
    {
        var dietitian = TestData.Professional(Discipline.Dietitian);
        var assessment = NutritionAssessment.Record(TestData.TenantId, dietitian, Guid.CreateVersion7(), Content(), TestData.Now).Value;

        assessment.Correct(dietitian, Content() with { ReportedAllergies = "Amendoim, camarão" }, "Alergia esquecida", TestData.Now).IsSuccess.ShouldBeTrue();

        assessment.Version.ShouldBe(2);
        assessment.Corrections.ShouldHaveSingleItem().PreviousContent.ReportedAllergies.ShouldBe("Amendoim");
    }
}
