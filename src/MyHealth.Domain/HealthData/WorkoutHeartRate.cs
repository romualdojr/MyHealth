using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.HealthData;

public enum HeartRateSyncKind
{
    /// <summary>Snapshot sent while the workout runs. Additive only.</summary>
    InProgress = 1,

    /// <summary>First snapshot sent after the workout finished. Authoritative.</summary>
    Final = 2,

    /// <summary>A later authoritative snapshot (late samples, source deletions).</summary>
    Reconciliation = 3,

    /// <summary>An in-progress snapshot that arrived after finalization; ignored so it cannot revert the final state.</summary>
    IgnoredStale = 4,
}

/// <summary>A time interval; an open <see cref="To"/> extends to "now".</summary>
public sealed record TimeInterval(DateTimeOffset From, DateTimeOffset? To)
{
    public bool Contains(DateTimeOffset instant) => instant >= From && (To is null || instant < To);
}

/// <summary>
/// Where a sample must fall to be imported: inside the workout interval and outside every period in which the client
/// had paused or revoked the Apple Health connection (the authorized period, HK-08 / RF-38).
/// </summary>
public sealed record HeartRateWindow(DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<TimeInterval>? Excluded = null)
{
    public bool Contains(DateTimeOffset instant) =>
        instant >= Start && instant <= End && !(Excluded ?? []).Any(interval => interval.Contains(instant));
}

public sealed record HeartRateReport(bool IsFinal, IReadOnlyCollection<HeartRateReading> Readings);

/// <summary>
/// Descriptive statistics over accepted samples, never a clinical measure. Two means are kept because the iOS module
/// computes a plain average while a physiological session average must weight by time (README "Mean" note):
/// <list type="bullet">
/// <item><see cref="AverageBeatsPerMinute"/>: arithmetic mean of samples (same as the phone).</item>
/// <item><see cref="TimeWeightedAverageBeatsPerMinute"/>: trapezoidal integral over time, excluding gaps longer than
/// <see cref="WorkoutHeartRate.GapThreshold"/>; null when no two samples are close enough to interpolate.</item>
/// </list>
/// </summary>
public sealed record HeartRateSummary(
    int SampleCount,
    int SelfReportedSampleCount,
    double AverageBeatsPerMinute,
    double? TimeWeightedAverageBeatsPerMinute,
    double MaximumBeatsPerMinute,
    double MinimumBeatsPerMinute,
    DateTimeOffset FirstSampleAt,
    DateTimeOffset LastSampleAt,
    string Method,
    string TimeWeightedMethod,
    DateTimeOffset CalculatedAt)
{
    public const string ArithmeticMeanMethod = "arithmetic-mean-of-samples";
    public const string TrapezoidalMeanMethod = "time-weighted-trapezoidal-excluding-gaps-over-60s";
}

public sealed record HeartRateSyncOutcome(
    HeartRateSyncKind Kind,
    int Accepted,
    int Duplicates,
    int Corrected,
    int Rejected,
    int Removed,
    int ClaimedByOtherSession,
    int Revision);

/// <summary>
/// Heart-rate data for one workout session (same id as the session). Implements the HealthKit contract:
/// dedup by sample UUID per client, idempotent retries, a final snapshot that cannot be reverted by a late in-progress
/// one, and later reconciliation that preserves provenance and history.
/// </summary>
public sealed class WorkoutHeartRate : AggregateRoot<Guid>
{
    public const double MinimumPlausibleBpm = 20;
    public const double MaximumPlausibleBpm = 300;

    /// <summary>Consecutive samples further apart than this are a gap: never interpolated nor drawn as continuous.</summary>
    public static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(60);

    private readonly List<HeartRateSample> _samples = [];
    private readonly List<HeartRateSync> _syncs = [];

    private WorkoutHeartRate(Guid workoutSessionId, Guid tenantId, Guid clientId) : base(workoutSessionId)
    {
        TenantId = tenantId;
        ClientId = clientId;
    }

    private WorkoutHeartRate() { }

    /// <summary>The workout's tenant (MT-06): only that tenant's professionals can ever be shown these samples.</summary>
    public Guid TenantId { get; private set; }

    public Guid ClientId { get; private set; }

    public bool IsFinalized { get; private set; }

    public DateTimeOffset? FinalizedAt { get; private set; }

    public int Revision { get; private set; }

    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>When the summary last changed after finalization (HK-08).</summary>
    public DateTimeOffset? LastRevisedAt { get; private set; }

    public HeartRateSummary? Summary { get; private set; }

    public IReadOnlyCollection<HeartRateSample> Samples => _samples.AsReadOnly();

    public IReadOnlyCollection<HeartRateSync> Syncs => _syncs.AsReadOnly();

    public static WorkoutHeartRate Create(Guid workoutSessionId, Guid tenantId, Guid clientId) => new(workoutSessionId, tenantId, clientId);

    /// <param name="report">The phone's current snapshot.</param>
    /// <param name="window">Workout interval and authorized period.</param>
    /// <param name="claimedByOtherSessions">HealthKit ids this client already stored in another workout.</param>
    /// <param name="now">Server time.</param>
    public HeartRateSyncOutcome Apply(
        HeartRateReport report, HeartRateWindow window, IReadOnlySet<Guid> claimedByOtherSessions, DateTimeOffset now)
    {
        if (!report.IsFinal && IsFinalized)
        {
            return Record(HeartRateSyncKind.IgnoredStale, new Counts(), now);
        }

        var kind = report.IsFinal
            ? IsFinalized ? HeartRateSyncKind.Reconciliation : HeartRateSyncKind.Final
            : HeartRateSyncKind.InProgress;

        var counts = new Counts();
        var seen = new HashSet<Guid>();

        // If the same sample appears twice in one report, the last occurrence wins.
        foreach (var reading in report.Readings.Reverse().DistinctBy(r => r.ExternalId))
        {
            if (claimedByOtherSessions.Contains(reading.ExternalId))
            {
                counts.Claimed++;
                continue;
            }

            if (!IsPlausible(reading) || !window.Contains(reading.Timestamp))
            {
                counts.Rejected++;
                continue;
            }

            seen.Add(reading.ExternalId);
            var existing = _samples.Find(s => s.ExternalId == reading.ExternalId);
            if (existing is null)
            {
                _samples.Add(new HeartRateSample(ClientId, reading, now));
                counts.Accepted++;
            }
            else if (existing.IsActive && existing.Matches(reading))
            {
                counts.Duplicates++;
            }
            else
            {
                existing.Correct(reading, now);
                counts.Corrected++;
            }
        }

        if (report.IsFinal)
        {
            // An authoritative snapshot no longer containing a sample means the source deleted it.
            foreach (var sample in _samples.Where(s => s.IsActive && !seen.Contains(s.ExternalId)))
            {
                sample.MarkRemovedBySource(now);
                counts.Removed++;
            }
        }

        var changed = counts.Accepted + counts.Corrected + counts.Removed > 0;
        if (changed || (report.IsFinal && !IsFinalized))
        {
            Summary = CalculateSummary(now);
            Revision++;
            if (IsFinalized)
            {
                LastRevisedAt = now;
            }
        }

        if (report.IsFinal && !IsFinalized)
        {
            IsFinalized = true;
            FinalizedAt = now;
        }

        return Record(kind, counts, now);
    }

    /// <summary>Intervals longer than <paramref name="threshold"/> without samples, so charts never imply continuity.</summary>
    public IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> FindGaps(TimeSpan threshold)
    {
        var ordered = ActiveOrdered().Select(s => s.Timestamp).ToList();
        return ordered.Zip(ordered.Skip(1))
            .Where(pair => pair.Second - pair.First > threshold)
            .Select(pair => (pair.First, pair.Second))
            .ToList();
    }

    private static bool IsPlausible(HeartRateReading reading) =>
        double.IsFinite(reading.BeatsPerMinute)
        && reading.BeatsPerMinute is >= MinimumPlausibleBpm and <= MaximumPlausibleBpm;

    private List<HeartRateSample> ActiveOrdered() => _samples.Where(s => s.IsActive).OrderBy(s => s.Timestamp).ToList();

    private HeartRateSummary? CalculateSummary(DateTimeOffset now)
    {
        var active = ActiveOrdered();
        if (active.Count == 0)
        {
            return null;
        }

        return new HeartRateSummary(
            active.Count,
            active.Count(s => s.Kind == HeartRateDataKind.SelfReported),
            Math.Round(active.Average(s => s.BeatsPerMinute), 1),
            TimeWeightedMean(active),
            active.Max(s => s.BeatsPerMinute),
            active.Min(s => s.BeatsPerMinute),
            active[0].Timestamp,
            active[^1].Timestamp,
            HeartRateSummary.ArithmeticMeanMethod,
            HeartRateSummary.TrapezoidalMeanMethod,
            now);
    }

    private static double? TimeWeightedMean(List<HeartRateSample> ordered)
    {
        double area = 0, seconds = 0;
        foreach (var (left, right) in ordered.Zip(ordered.Skip(1)))
        {
            var span = (right.Timestamp - left.Timestamp).TotalSeconds;
            if (span <= 0 || span > GapThreshold.TotalSeconds)
            {
                continue;
            }

            area += (left.BeatsPerMinute + right.BeatsPerMinute) / 2 * span;
            seconds += span;
        }

        return seconds == 0 ? null : Math.Round(area / seconds, 1);
    }

    private HeartRateSyncOutcome Record(HeartRateSyncKind kind, Counts counts, DateTimeOffset now)
    {
        LastSyncedAt = now;
        _syncs.Add(new HeartRateSync(
            Guid.CreateVersion7(now), kind, counts.Accepted, counts.Duplicates, counts.Corrected, counts.Rejected, counts.Removed,
            counts.Claimed, Revision, now));
        return new HeartRateSyncOutcome(
            kind, counts.Accepted, counts.Duplicates, counts.Corrected, counts.Rejected, counts.Removed, counts.Claimed, Revision);
    }

    private sealed class Counts
    {
        public int Accepted { get; set; }

        public int Duplicates { get; set; }

        public int Corrected { get; set; }

        public int Rejected { get; set; }

        public int Removed { get; set; }

        public int Claimed { get; set; }
    }
}

/// <summary>Audit trail of each upload: what was accepted, rejected or removed, and the resulting revision.</summary>
public sealed class HeartRateSync : Entity<Guid>
{
    internal HeartRateSync(
        Guid id, HeartRateSyncKind kind, int accepted, int duplicates, int corrected, int rejected, int removed,
        int claimedByOtherSession, int revision, DateTimeOffset receivedAt) : base(id)
    {
        Kind = kind;
        Accepted = accepted;
        Duplicates = duplicates;
        Corrected = corrected;
        Rejected = rejected;
        Removed = removed;
        ClaimedByOtherSession = claimedByOtherSession;
        Revision = revision;
        ReceivedAt = receivedAt;
    }

    private HeartRateSync() { }

    public HeartRateSyncKind Kind { get; private set; }

    public int Accepted { get; private set; }

    public int Duplicates { get; private set; }

    public int Corrected { get; private set; }

    public int Rejected { get; private set; }

    public int Removed { get; private set; }

    public int ClaimedByOtherSession { get; private set; }

    public int Revision { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }
}

public interface IWorkoutHeartRateRepository : IRepository<WorkoutHeartRate, Guid>
{
    /// <summary>Of <paramref name="externalIds"/>, those the client already stored under a different workout.</summary>
    Task<IReadOnlySet<Guid>> GetClaimedByOtherSessionsAsync(
        Guid clientId, Guid workoutSessionId, IReadOnlyCollection<Guid> externalIds, CancellationToken cancellationToken = default);
}

public static class HealthDataErrors
{
    public static readonly Error ConnectionRevoked = Error.Conflict("HealthData.ConnectionRevoked", "The connection was revoked; connect again to resume.");
    public static readonly Error ImportNotAllowed = Error.Forbidden("HealthData.ImportNotAllowed", "Apple Health import is not connected or is paused.");
    public static readonly Error NotFound = Error.NotFound("HealthData.NotFound", "No heart-rate data was received for this workout.");
    public static readonly Error SharingStopped = Error.Forbidden("HealthData.SharingStopped", "The client revoked the Apple Health connection; heart rate is no longer shared.");
    public static readonly Error ReconciliationWindowClosed = Error.Conflict(
        "HealthData.ReconciliationWindowClosed", "Heart rate can only be reconciled up to 30 days after the workout ended.");
    public static readonly Error WorkoutExportNotEnabled = Error.Forbidden(
        "HealthData.WorkoutExportNotEnabled", "Saving workouts to Apple Health is not enabled for this client.");
}
