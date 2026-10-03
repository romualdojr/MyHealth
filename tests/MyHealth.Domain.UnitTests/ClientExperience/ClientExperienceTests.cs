using MyHealth.Domain.Assistant;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.Workouts;
using MyHealth.Domain.Wellbeing;

namespace MyHealth.Domain.UnitTests.ClientExperience;

public sealed class ExerciseMediaTests
{
    private static readonly DateTimeOffset Now = TestData.Now;

    private static Exercise Library(string? description = "Desça até a coxa ficar paralela ao chão.") =>
        Exercise.CreateSystem(new ExerciseDetails("Agachamento livre", "Barra nas costas.", ["quadriceps"], ["barra"], null, null, description), Now);

    private static ExerciseMedia Loop(CameraAngle angle = CameraAngle.Side) =>
        new(Guid.CreateVersion7(), MediaKind.Loop, angle, "https://media.myhealth.app/squat-side.mp4", null, 12, null, []);

    private static ExerciseMedia Full(params MediaChapter[] chapters) =>
        new(Guid.CreateVersion7(), MediaKind.FullVideo, CameraAngle.Side, "https://media.myhealth.app/squat.mp4", "https://media.myhealth.app/squat.jpg", 84,
            "https://media.myhealth.app/squat.vtt", chapters);

    [Fact]
    public void A_library_exercise_gets_a_loop_per_angle_and_a_full_video_with_chapters()
    {
        var exercise = Library();

        exercise.SetSystemMedia([Loop(), Loop(CameraAngle.Front), Full(new(0, "Posição"), new(21, "Descida"), new(65, "Erros comuns"))], Now).IsSuccess.ShouldBeTrue();

        exercise.Media.Count.ShouldBe(3);
        exercise.Version.ShouldBe(1);
    }

    [Fact]
    public void Media_rules_protect_the_workout_screen_and_accessibility()
    {
        Library().SetSystemMedia([Loop(), Loop()], Now).Error.ShouldBe(ExerciseErrors.DuplicateLoop);
        Library(description: null).SetSystemMedia([Loop()], Now).Error.ShouldBe(ExerciseErrors.MediaDescriptionRequired);
        Library().SetSystemMedia([Loop() with { Url = "http://media.myhealth.app/a.mp4" }], Now).Error.ShouldBe(ExerciseErrors.InvalidMedia);
        Library().SetSystemMedia([Loop() with { DurationSeconds = null }], Now).Error.ShouldBe(ExerciseErrors.InvalidMedia);
        Library().SetSystemMedia([Full(new(30, "Descida"), new(10, "Posição"))], Now).Error.ShouldBe(ExerciseErrors.InvalidChapters);
        Library().SetSystemMedia([Full(new MediaChapter(90, "Depois do fim"))], Now).Error.ShouldBe(ExerciseErrors.InvalidChapters);
        Library().SetSystemMedia([.. Enumerable.Range(0, 9).Select(i => Loop((CameraAngle)(i % 4 + 1)) with { Kind = MediaKind.Image, DurationSeconds = null })], Now)
            .Error.ShouldBe(ExerciseErrors.TooManyMedia);
    }

    [Fact]
    public void Only_the_creator_changes_a_custom_exercise_and_operators_only_the_library()
    {
        var owner = Guid.CreateVersion7();
        var custom = Exercise.CreateCustom(TestData.TenantId, owner, new ExerciseDetails("Afundo", "Passada.", [], [], null, null, "Passo à frente."), Now);

        custom.SetMedia(Guid.CreateVersion7(), [Loop()], Now).Error.ShouldBe(ExerciseErrors.NotOwner);
        custom.SetSystemMedia([Loop()], Now).Error.ShouldBe(ExerciseErrors.NotSystem);
        custom.SetMedia(owner, [Loop()], Now).IsSuccess.ShouldBeTrue();
    }
}

public sealed class WellbeingCheckInTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);

    [Fact]
    public void A_check_in_needs_an_answer_on_the_1_to_5_scale_for_a_recent_day()
    {
        WellbeingCheckIn.Record(Guid.CreateVersion7(), Today, Today, null, null, null, TestData.Now).Error.ShouldBe(WellbeingErrors.Empty);
        WellbeingCheckIn.Record(Guid.CreateVersion7(), Today, Today, 6, null, null, TestData.Now).Error.ShouldBe(WellbeingErrors.OutOfScale);
        WellbeingCheckIn.Record(Guid.CreateVersion7(), Today.AddDays(1), Today, 3, null, null, TestData.Now).Error.ShouldBe(WellbeingErrors.DateOutOfRange);
        WellbeingCheckIn.Record(Guid.CreateVersion7(), Today.AddDays(-8), Today, 3, null, null, TestData.Now).Error.ShouldBe(WellbeingErrors.DateOutOfRange);
    }

    [Fact]
    public void Answering_again_the_same_day_keeps_what_was_not_answered()
    {
        var checkIn = WellbeingCheckIn.Record(Guid.CreateVersion7(), Today, Today, 2, null, null, TestData.Now).Value;

        checkIn.Update(Today, null, 4, Guid.CreateVersion7(), TestData.Now.AddHours(2)).IsSuccess.ShouldBeTrue();

        (checkIn.SleepQuality, checkIn.Energy).ShouldBe((2, 4));
        checkIn.WorkoutSessionId.ShouldNotBeNull();
    }
}

public sealed class AssistantConsentTests
{
    private static readonly DateTimeOffset Now = TestData.Now;

    [Fact]
    public void Consent_is_for_the_current_terms_with_at_least_one_area_and_can_be_withdrawn_and_renewed()
    {
        var user = Guid.CreateVersion7();
        AssistantConsent.Accept(user, 0, 1, [AssistantScope.Training], Now).Error.ShouldBe(AssistantErrors.OutdatedTerms);
        AssistantConsent.Accept(user, 1, 1, [], Now).Error.ShouldBe(AssistantErrors.NoScopes);

        var consent = AssistantConsent.Accept(user, 1, 1, [AssistantScope.Nutrition, AssistantScope.Training, AssistantScope.Training], Now).Value;
        consent.Scopes.ShouldBe([AssistantScope.Training, AssistantScope.Nutrition]);
        consent.Allows(AssistantScope.Therapies, 1).ShouldBeFalse();

        // A new version of the terms needs a new acceptance.
        consent.IsActiveFor(2).ShouldBeFalse();

        consent.Withdraw(Now.AddDays(1));
        consent.IsActiveFor(1).ShouldBeFalse();
        consent.Renew(1, 1, [AssistantScope.Training], Now.AddDays(2)).IsSuccess.ShouldBeTrue();
        consent.IsActiveFor(1).ShouldBeTrue();
        consent.AcceptedAt.ShouldBe(Now.AddDays(2));
        consent.History.Select(h => h.Active).ShouldBe([true, false, true]);
    }

    [Fact]
    public void A_suggestion_is_confirmed_once_and_expires_after_an_hour()
    {
        var proposal = AssistantProposal.Create(Guid.CreateVersion7(), ProposalKind.FoodDiaryEntry, "{}", Now);

        proposal.Confirm(Now.AddMinutes(5)).IsSuccess.ShouldBeTrue();
        proposal.Confirm(Now.AddMinutes(6)).IsSuccess.ShouldBeTrue();
        proposal.Discard(Now.AddMinutes(7)).Error.ShouldBe(AssistantErrors.ProposalClosed);

        var expired = AssistantProposal.Create(Guid.CreateVersion7(), ProposalKind.SetLog, "{}", Now);
        expired.Confirm(Now + AssistantProposal.Lifetime).Error.ShouldBe(AssistantErrors.ProposalClosed);
        expired.Discard(Now + AssistantProposal.Lifetime).IsSuccess.ShouldBeTrue();
    }
}

public sealed class BodyCheckTests
{
    private static readonly DateTimeOffset Now = TestData.Now;

    private static WorkoutSession Started()
    {
        var client = TestData.Client();
        var (_, version) = TestData.PublishedPlan(TestData.Professional(), client);
        return WorkoutSession.Start(Guid.CreateVersion7(), client.Id, version, version.Content.Sessions[0].Id, Now, TestData.SaoPaulo, Now).Value;
    }

    private static BodyCheck Check(BodyCheckAnswer answer = BodyCheckAnswer.Worse, bool report = true, Guid? id = null) =>
        new(id ?? Guid.CreateVersion7(), " joelho direito ", answer, "Dói na descida", report, Now.AddMinutes(20));

    [Fact]
    public void Reporting_a_body_check_notifies_the_trainer_and_a_retry_is_a_no_op()
    {
        var session = Started();
        var check = Check();

        session.RecordBodyCheck(check, Now.AddMinutes(20)).IsSuccess.ShouldBeTrue();
        session.RecordBodyCheck(check, Now.AddMinutes(21)).IsSuccess.ShouldBeTrue();
        session.RecordBodyCheck(check with { Answer = BodyCheckAnswer.Same }, Now.AddMinutes(21))
            .Error.ShouldBe(WorkoutErrors.BodyCheckIdConflict);

        session.BodyChecks.ShouldHaveSingleItem().Subject.ShouldBe("joelho direito");
        session.DomainEvents.OfType<DiscomfortReported>().ShouldHaveSingleItem();
        session.LatestReportedDiscomfort().ShouldBe(new ReportedDiscomfort("joelho direito", "Dói na descida"));
    }

    [Fact]
    public void A_check_without_report_stays_with_the_client_and_closes_a_week_after_the_workout()
    {
        var session = Started();
        session.RecordBodyCheck(Check(BodyCheckAnswer.Better, report: false), Now.AddMinutes(20)).IsSuccess.ShouldBeTrue();
        session.DomainEvents.OfType<DiscomfortReported>().ShouldBeEmpty();
        session.LatestReportedDiscomfort().ShouldBeNull();

        session.Complete(new WorkoutFeedback(7, false, null, null), Now.AddMinutes(50), Now.AddMinutes(50)).IsSuccess.ShouldBeTrue();
        session.RecordBodyCheck(Check(), Now.AddDays(6)).IsSuccess.ShouldBeTrue();
        session.RecordBodyCheck(Check(), Now.AddDays(8)).Error.ShouldBe(WorkoutErrors.BodyCheckClosed);
    }
}
