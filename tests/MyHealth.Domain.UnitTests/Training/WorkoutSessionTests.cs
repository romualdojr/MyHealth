using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Domain.UnitTests.Training;

public sealed class WorkoutSessionTests
{
    private static readonly WorkoutFeedback Feedback = new(7, false, null, null);

    private static (WorkoutSession Session, PlannedExercise Exercise) Started(DateTimeOffset? startedAt = null)
    {
        var client = TestData.Client();
        var (_, version) = TestData.PublishedPlan(TestData.Professional(), client);
        var planned = version.Content.Sessions[0];
        var start = startedAt ?? TestData.Now;
        var session = WorkoutSession.Start(Guid.CreateVersion7(), client.Id, version, planned.Id, start, TestData.SaoPaulo, start).Value;
        return (session, planned.Exercises[0]);
    }

    private static SetLogEntry Set(PlannedExercise exercise, Guid? id = null, int reps = 10, decimal load = 40m) =>
        new(id ?? Guid.CreateVersion7(), exercise.ExerciseId, exercise.Id, 1, reps, load, null, null, null, null, TestData.Now);

    [Fact]
    public void Session_must_belong_to_the_version()
    {
        var client = TestData.Client();
        var (_, version) = TestData.PublishedPlan(TestData.Professional(), client);

        var result = WorkoutSession.Start(Guid.CreateVersion7(), client.Id, version, Guid.CreateVersion7(), TestData.Now, TestData.SaoPaulo, TestData.Now);

        result.Error.ShouldBe(WorkoutErrors.PlannedSessionNotFound);
    }

    [Fact]
    public void Another_client_cannot_start_a_session_of_this_plan()
    {
        var (_, version) = TestData.PublishedPlan(TestData.Professional(), TestData.Client());

        var result = WorkoutSession.Start(
            Guid.CreateVersion7(), Guid.CreateVersion7(), version, version.Content.Sessions[0].Id, TestData.Now, TestData.SaoPaulo, TestData.Now);

        result.Error.ShouldBe(PlanErrors.NotFound);
    }

    [Fact]
    public void Start_in_the_future_is_rejected()
    {
        var client = TestData.Client();
        var (_, version) = TestData.PublishedPlan(TestData.Professional(), client);

        var result = WorkoutSession.Start(
            Guid.CreateVersion7(), client.Id, version, version.Content.Sessions[0].Id, TestData.Now.AddHours(1), TestData.SaoPaulo, TestData.Now);

        result.Error.ShouldBe(WorkoutErrors.FutureTimestamp);
    }

    [Fact]
    public void Pause_resume_and_complete_compute_active_duration()
    {
        var (session, _) = Started();

        session.Pause(TestData.Now.AddMinutes(10));
        session.Resume(TestData.Now.AddMinutes(15));
        session.Complete(Feedback, TestData.Now.AddMinutes(45), TestData.Now.AddMinutes(45));

        session.Status.ShouldBe(WorkoutStatus.Completed);
        session.ActiveDuration().ShouldBe(TimeSpan.FromMinutes(40));
    }

    [Fact]
    public void Completing_counts_on_the_client_local_date()
    {
        // 01:30 UTC on 1 Oct is still 30 Sep in São Paulo (UTC-3).
        var started = new DateTimeOffset(2026, 10, 1, 0, 30, 0, TimeSpan.Zero);
        var (session, _) = Started(started);

        session.Complete(Feedback, started.AddHours(1), started.AddHours(1));

        session.LocalDate.ShouldBe(new DateOnly(2026, 9, 30));
        session.DomainEvents.OfType<WorkoutCompleted>().ShouldHaveSingleItem().LocalDate.ShouldBe(new DateOnly(2026, 9, 30));
    }

    [Fact]
    public void Retried_completion_is_idempotent()
    {
        var (session, _) = Started();
        session.Complete(Feedback, TestData.Now.AddMinutes(30), TestData.Now.AddMinutes(30));
        session.ClearDomainEvents();

        var retry = session.Complete(new WorkoutFeedback(2, true, "x", null), TestData.Now.AddMinutes(40), TestData.Now.AddMinutes(40));

        retry.IsSuccess.ShouldBeTrue();
        session.CompletedAt.ShouldBe(TestData.Now.AddMinutes(30));
        session.Feedback!.PerceivedExertion.ShouldBe(7);
        session.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Abandoned_session_cannot_be_completed()
    {
        var (session, _) = Started();
        session.Abandon(TestData.Now.AddMinutes(5));

        session.Complete(Feedback, TestData.Now.AddMinutes(10), TestData.Now.AddMinutes(10)).Error.ShouldBe(WorkoutErrors.AlreadyFinished);
        session.LocalDate.ShouldBeNull();
    }

    [Fact]
    public void Discomfort_raises_event_for_the_trainer()
    {
        var (session, _) = Started();

        session.Complete(new WorkoutFeedback(8, true, "Dor no ombro", null), TestData.Now.AddMinutes(30), TestData.Now.AddMinutes(30));

        session.DomainEvents.OfType<DiscomfortReported>().ShouldHaveSingleItem().TrainerUserId.ShouldBe(session.TrainerUserId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Perceived_exertion_must_be_between_1_and_10(int value)
    {
        var (session, _) = Started();

        session.Complete(new WorkoutFeedback(value, false, null, null), TestData.Now.AddMinutes(30), TestData.Now.AddMinutes(30))
            .Error.ShouldBe(WorkoutErrors.InvalidPerceivedExertion);
    }

    [Fact]
    public void Logging_the_same_set_id_twice_corrects_instead_of_duplicating()
    {
        var (session, exercise) = Started();
        var setId = Guid.CreateVersion7();

        session.LogSet(Set(exercise, setId, reps: 10), TestData.Now);
        session.LogSet(Set(exercise, setId, reps: 12), TestData.Now);

        session.Sets.ShouldHaveSingleItem().Repetitions.ShouldBe(12);
    }

    [Fact]
    public void Exercise_outside_the_session_is_rejected()
    {
        var (session, exercise) = Started();

        session.LogSet(Set(exercise) with { ExerciseId = Guid.CreateVersion7() }, TestData.Now).Error.ShouldBe(WorkoutErrors.ExerciseNotInSession);
    }

    [Fact]
    public void Empty_set_is_rejected()
    {
        var (session, exercise) = Started();

        session.LogSet(Set(exercise) with { Repetitions = null }, TestData.Now).Error.ShouldBe(WorkoutErrors.EmptySet);
    }

    [Fact]
    public void Sets_cannot_be_logged_after_completion()
    {
        var (session, exercise) = Started();
        session.Complete(Feedback, TestData.Now.AddMinutes(30), TestData.Now.AddMinutes(30));

        session.LogSet(Set(exercise), TestData.Now).Error.ShouldBe(WorkoutErrors.AlreadyFinished);
    }

    [Fact]
    public void Only_the_responsible_trainer_replies_to_feedback()
    {
        var (session, _) = Started();
        session.Complete(Feedback, TestData.Now.AddMinutes(30), TestData.Now.AddMinutes(30));

        session.ReplyToFeedback(Guid.CreateVersion7(), "Ótimo!", TestData.Now).Error.ShouldBe(WorkoutErrors.NotResponsibleTrainer);
        session.ReplyToFeedback(session.TrainerProfileId, "Ótimo!", TestData.Now).IsSuccess.ShouldBeTrue();
        session.Reply!.Message.ShouldBe("Ótimo!");
    }

    [Fact]
    public void Corrections_after_the_workout_keep_previous_values_reason_and_person()
    {
        var (session, exercise) = Started();
        var setId = Guid.CreateVersion7();
        session.LogSet(Set(exercise, setId, reps: 10, load: 40m), TestData.Now);
        var clientId = session.ClientId;
        var corrected = new SetValues(10, 45m, null, null, null, "Anotei a carga errada");

        session.CorrectSetAfterWorkout(setId, corrected, "Carga errada", clientId, TestData.Now).Error.ShouldBe(WorkoutErrors.StillOpen);
        session.Complete(Feedback, TestData.Now.AddMinutes(40), TestData.Now.AddMinutes(41));

        session.CorrectSetAfterWorkout(Guid.CreateVersion7(), corrected, "x", clientId, TestData.Now).Error.ShouldBe(WorkoutErrors.SetNotFound);
        session.CorrectSetAfterWorkout(setId, corrected, " ", clientId, TestData.Now).Error.ShouldBe(WorkoutErrors.CorrectionReasonRequired);
        session.CorrectSetAfterWorkout(setId, new SetValues(null, 45m, null, null, null, null), "x", clientId, TestData.Now).Error.ShouldBe(WorkoutErrors.EmptySet);
        session.CorrectSetAfterWorkout(setId, corrected, "Carga errada", clientId, TestData.Now.AddHours(2)).IsSuccess.ShouldBeTrue();
        session.CorrectSetAfterWorkout(setId, corrected, "Carga errada", clientId, TestData.Now.AddHours(2)).IsSuccess.ShouldBeTrue();

        var set = session.Sets.ShouldHaveSingleItem();
        set.LoadKg.ShouldBe(45m);
        var correction = set.Corrections.ShouldHaveSingleItem();
        (correction.Previous.LoadKg, correction.Reason, correction.CorrectedBy).ShouldBe((40m, "Carga errada", clientId));
    }
}
