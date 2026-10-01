using MyHealth.Domain.HealthData;

namespace MyHealth.Domain.UnitTests.HealthData;

/// <summary>HealthKit contract (HK-04/05/07/08, RF-41/42) and acceptance tests 4, 11 and 13.</summary>
public sealed class WorkoutHeartRateTests
{
    private static readonly DateTimeOffset Start = TestData.Now;
    private static readonly HeartRateWindow Window = new(Start, Start.AddMinutes(60));
    private static readonly IReadOnlySet<Guid> None = new HashSet<Guid>();

    private static HeartRateReading Reading(int minute, double bpm = 120, Guid? id = null, string source = "Apple Watch") =>
        new(id ?? Guid.CreateVersion7(), Start.AddMinutes(minute), bpm, source, "Watch");

    private static WorkoutHeartRate NewRecord() => WorkoutHeartRate.Create(Guid.CreateVersion7(), TestData.TenantId, Guid.CreateVersion7());

    [Fact]
    public void Retried_report_does_not_duplicate_samples()
    {
        var record = NewRecord();
        HeartRateReading[] readings = [Reading(1, 110), Reading(2, 130)];

        var first = record.Apply(new HeartRateReport(false, readings), Window, None, Start);
        var retry = record.Apply(new HeartRateReport(false, readings), Window, None, Start.AddSeconds(5));

        first.Accepted.ShouldBe(2);
        retry.Accepted.ShouldBe(0);
        retry.Duplicates.ShouldBe(2);
        record.Samples.Count.ShouldBe(2);
        retry.Revision.ShouldBe(first.Revision);
    }

    [Fact]
    public void Summary_is_recalculated_from_accepted_points()
    {
        var record = NewRecord();

        record.Apply(new HeartRateReport(true, [Reading(1, 100), Reading(2, 140), Reading(3, 120)]), Window, None, Start);

        record.Summary.ShouldNotBeNull();
        record.Summary.SampleCount.ShouldBe(3);
        record.Summary.AverageBeatsPerMinute.ShouldBe(120);
        record.Summary.MaximumBeatsPerMinute.ShouldBe(140);
        record.Summary.Method.ShouldBe(HeartRateSummary.ArithmeticMeanMethod);
    }

    [Fact]
    public void Implausible_and_out_of_window_samples_are_rejected()
    {
        var record = NewRecord();
        HeartRateReading[] readings = [Reading(1, 10), Reading(2, 400), Reading(-5), Reading(90), Reading(3, 125)];

        var outcome = record.Apply(new HeartRateReport(false, readings), Window, None, Start);

        outcome.Rejected.ShouldBe(4);
        record.Samples.ShouldHaveSingleItem().BeatsPerMinute.ShouldBe(125);
    }

    [Fact]
    public void Late_in_progress_report_cannot_revert_the_final_state()
    {
        var record = NewRecord();
        var kept = Reading(1, 110);
        var deletedBySource = Reading(2, 150);
        record.Apply(new HeartRateReport(false, [kept, deletedBySource]), Window, None, Start);
        record.Apply(new HeartRateReport(true, [kept]), Window, None, Start.AddMinutes(61));

        // An out-of-order retry of an earlier in-progress snapshot.
        var stale = record.Apply(new HeartRateReport(false, [kept, deletedBySource]), Window, None, Start.AddMinutes(62));

        stale.Kind.ShouldBe(HeartRateSyncKind.IgnoredStale);
        record.IsFinalized.ShouldBeTrue();
        record.Samples.Single(s => s.ExternalId == deletedBySource.ExternalId).IsActive.ShouldBeFalse();
        record.Summary!.SampleCount.ShouldBe(1);
    }

    [Fact]
    public void Final_report_marks_missing_samples_as_removed_by_source_without_deleting_them()
    {
        var record = NewRecord();
        var a = Reading(1, 100);
        var b = Reading(2, 200);
        record.Apply(new HeartRateReport(false, [a, b]), Window, None, Start);

        var outcome = record.Apply(new HeartRateReport(true, [a]), Window, None, Start.AddMinutes(61));

        outcome.Removed.ShouldBe(1);
        record.Samples.Count.ShouldBe(2);
        record.Samples.Single(s => s.ExternalId == b.ExternalId).RemovedAt.ShouldNotBeNull();
        record.Summary!.MaximumBeatsPerMinute.ShouldBe(100);
    }

    [Fact]
    public void Reconciliation_adds_late_samples_and_records_revision()
    {
        var record = NewRecord();
        var a = Reading(1, 100);
        record.Apply(new HeartRateReport(true, [a]), Window, None, Start.AddMinutes(61));
        var revisionAfterFinal = record.Revision;

        var outcome = record.Apply(new HeartRateReport(true, [a, Reading(30, 160)]), Window, None, Start.AddHours(3));

        outcome.Kind.ShouldBe(HeartRateSyncKind.Reconciliation);
        outcome.Accepted.ShouldBe(1);
        record.Revision.ShouldBe(revisionAfterFinal + 1);
        record.LastRevisedAt.ShouldBe(Start.AddHours(3));
        record.Summary!.SampleCount.ShouldBe(2);
    }

    [Fact]
    public void Samples_from_different_sources_are_all_kept_with_provenance()
    {
        var record = NewRecord();

        record.Apply(new HeartRateReport(false, [Reading(1, source: "Apple Watch"), Reading(2, source: "Polar H10")]), Window, None, Start);

        record.Samples.Select(s => s.SourceName).ShouldBe(["Apple Watch", "Polar H10"], ignoreOrder: true);
    }

    [Fact]
    public void Source_correction_updates_the_sample()
    {
        var record = NewRecord();
        var id = Guid.CreateVersion7();
        record.Apply(new HeartRateReport(false, [Reading(1, 100, id)]), Window, None, Start);

        var outcome = record.Apply(new HeartRateReport(false, [Reading(1, 104, id)]), Window, None, Start.AddMinutes(2));

        outcome.Corrected.ShouldBe(1);
        record.Samples.ShouldHaveSingleItem().BeatsPerMinute.ShouldBe(104);
        record.Samples.Single().CorrectedAt.ShouldNotBeNull();
    }

    [Fact]
    public void Gaps_are_reported_instead_of_implying_continuity()
    {
        var record = NewRecord();
        record.Apply(new HeartRateReport(false, [Reading(1), Reading(2), Reading(10)]), Window, None, Start);

        var gap = record.FindGaps(TimeSpan.FromMinutes(1)).ShouldHaveSingleItem();

        gap.From.ShouldBe(Start.AddMinutes(2));
        gap.To.ShouldBe(Start.AddMinutes(10));
    }

    [Fact]
    public void No_samples_means_no_summary_rather_than_invented_values()
    {
        var record = NewRecord();

        record.Apply(new HeartRateReport(true, []), Window, None, Start);

        record.IsFinalized.ShouldBeTrue();
        record.Summary.ShouldBeNull();
    }

    [Fact]
    public void Every_upload_is_recorded_in_the_sync_history()
    {
        var record = NewRecord();
        record.Apply(new HeartRateReport(false, [Reading(1)]), Window, None, Start);
        record.Apply(new HeartRateReport(true, [Reading(2)]), Window, None, Start.AddMinutes(61));

        record.Syncs.Select(s => s.Kind).ShouldBe([HeartRateSyncKind.InProgress, HeartRateSyncKind.Final]);
        record.LastSyncedAt.ShouldBe(Start.AddMinutes(61));
    }
}
