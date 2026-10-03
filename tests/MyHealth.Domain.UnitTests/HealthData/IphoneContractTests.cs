using MyHealth.Domain.HealthData;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Domain.UnitTests.HealthData;

/// <summary>Rules added to close the iPhone-integration gaps: authorized period, per-client dedup, means, provenance, HK-06.</summary>
public sealed class IphoneContractTests
{
    private static readonly DateTimeOffset Start = TestData.Now;
    private static readonly IReadOnlySet<Guid> None = new HashSet<Guid>();

    private static HeartRateReading Reading(int second, double bpm = 120, bool userEntered = false) =>
        new(Guid.CreateVersion7(), Start.AddSeconds(second), bpm, "Apple Watch", null, userEntered);

    private static WorkoutHeartRate NewRecord() => WorkoutHeartRate.Create(Guid.CreateVersion7(), TestData.TenantId, Guid.CreateVersion7());

    [Fact]
    public void Samples_recorded_while_import_was_paused_are_never_imported()
    {
        var connection = HealthConnection.Connect(Guid.CreateVersion7(), HealthDataProvider.AppleHealth, Start);
        connection.Pause(Start.AddMinutes(10));
        connection.Reconnect(Start.AddMinutes(20));
        var window = new HeartRateWindow(Start, Start.AddMinutes(30), connection.ExcludedIntervals());
        var record = NewRecord();

        var outcome = record.Apply(
            new HeartRateReport(true, [Reading(5 * 60), Reading(15 * 60), Reading(25 * 60)]), window, None, Start.AddMinutes(31));

        outcome.Accepted.ShouldBe(2);
        outcome.Rejected.ShouldBe(1);
        record.Samples.ShouldNotContain(s => s.Timestamp == Start.AddMinutes(15));
    }

    [Fact]
    public void Open_pause_excludes_everything_after_it()
    {
        var connection = HealthConnection.Connect(Guid.CreateVersion7(), HealthDataProvider.AppleHealth, Start);
        connection.Revoke(Start.AddMinutes(10));

        connection.ExcludedIntervals().ShouldHaveSingleItem().ShouldBe(new TimeInterval(Start.AddMinutes(10), null));
    }

    [Fact]
    public void Sample_already_stored_for_another_session_of_the_client_is_not_stored_again()
    {
        var shared = Reading(10);
        var record = NewRecord();

        var outcome = record.Apply(
            new HeartRateReport(false, [shared, Reading(20)]), new HeartRateWindow(Start, Start.AddHours(1)), new HashSet<Guid> { shared.ExternalId }, Start);

        outcome.ClaimedByOtherSession.ShouldBe(1);
        outcome.Accepted.ShouldBe(1);
        record.Samples.ShouldNotContain(s => s.ExternalId == shared.ExternalId);
    }

    [Fact]
    public void Time_weighted_mean_weights_by_duration_and_skips_gaps()
    {
        var record = NewRecord();
        // 100 bpm for 10 s, rising to 160 over 50 s, then a 5-minute gap and a 60 bpm outlier.
        HeartRateReading[] readings = [Reading(0, 100), Reading(10, 100), Reading(60, 160), Reading(360, 60)];

        record.Apply(new HeartRateReport(true, readings), new HeartRateWindow(Start, Start.AddHours(1)), None, Start.AddHours(1));

        var summary = record.Summary.ShouldNotBeNull();
        summary.AverageBeatsPerMinute.ShouldBe(105); // arithmetic, as computed by the phone
        summary.TimeWeightedAverageBeatsPerMinute.ShouldBe(125); // (100*10 + 130*50) / 60, the gap is excluded
        summary.TimeWeightedMethod.ShouldBe(HeartRateSummary.TrapezoidalMeanMethod);
    }

    [Fact]
    public void Time_weighted_mean_is_null_when_samples_are_too_sparse()
    {
        var record = NewRecord();

        record.Apply(new HeartRateReport(true, [Reading(0), Reading(600)]), new HeartRateWindow(Start, Start.AddHours(1)), None, Start.AddHours(1));

        record.Summary!.TimeWeightedAverageBeatsPerMinute.ShouldBeNull();
    }

    [Fact]
    public void User_entered_samples_are_labelled_self_reported()
    {
        var record = NewRecord();

        record.Apply(
            new HeartRateReport(false, [Reading(1), Reading(2, userEntered: true)]), new HeartRateWindow(Start, Start.AddHours(1)), None, Start);

        record.Samples.Select(s => s.Kind).ShouldBe([HeartRateDataKind.Measured, HeartRateDataKind.SelfReported], ignoreOrder: true);
        record.Summary!.SelfReportedSampleCount.ShouldBe(1);
    }

    [Fact]
    public void Revoking_stops_sharing_and_workout_saving()
    {
        var connection = HealthConnection.Connect(Guid.CreateVersion7(), HealthDataProvider.AppleHealth, Start);
        connection.SetWorkoutSaving(true, Start);

        connection.Revoke(Start.AddDays(1));

        connection.AllowsSharing.ShouldBeFalse();
        connection.SaveWorkouts.ShouldBeFalse();
    }

    [Fact]
    public void Workout_saving_does_not_require_heart_rate_import()
    {
        var connection = HealthConnection.Create(Guid.CreateVersion7(), HealthDataProvider.AppleHealth, Start);

        connection.SetWorkoutSaving(true, Start);

        connection.Status.ShouldBe(HealthConnectionStatus.NotConnected);
        connection.AllowsImport.ShouldBeFalse();
        connection.SaveWorkouts.ShouldBeTrue();
        connection.Pause(Start).Error.ShouldBe(HealthDataErrors.ImportNotAllowed);
    }

    [Fact]
    public void Exactly_one_apple_health_workout_is_recorded_per_session()
    {
        var client = TestData.Client();
        var (_, version) = TestData.PublishedPlan(TestData.Professional(), client);
        var session = WorkoutSession.Start(Guid.CreateVersion7(), client.Id, version, version.Content.Sessions[0].Id, Start, TestData.SaoPaulo, Start).Value;
        var healthKitWorkout = Guid.CreateVersion7();

        session.RecordAppleHealthWorkout(healthKitWorkout, Start, Start).Error.ShouldBe(WorkoutErrors.NotCompleted);
        session.Complete(new WorkoutFeedback(6, false, null, null), Start.AddMinutes(30), Start.AddMinutes(30));

        session.RecordAppleHealthWorkout(healthKitWorkout, Start.AddMinutes(30), Start.AddMinutes(31)).IsSuccess.ShouldBeTrue();
        session.RecordAppleHealthWorkout(healthKitWorkout, Start.AddMinutes(30), Start.AddMinutes(32)).IsSuccess.ShouldBeTrue();
        session.RecordAppleHealthWorkout(Guid.CreateVersion7(), Start.AddMinutes(30), Start.AddMinutes(33))
            .Error.ShouldBe(WorkoutErrors.AlreadySavedToAppleHealth);
        session.AppleHealthWorkout!.ExternalWorkoutId.ShouldBe(healthKitWorkout);
    }
}
