using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.HealthData;

/// <summary>
/// A reading as reported by the phone. <see cref="ExternalId"/> is the HealthKit sample UUID.
/// <see cref="WasUserEntered"/> mirrors <c>HKMetadataKeyWasUserEntered</c>: typed into the Health app by a person
/// rather than measured by a device.
/// </summary>
public sealed record HeartRateReading(
    Guid ExternalId,
    DateTimeOffset Timestamp,
    double BeatsPerMinute,
    string SourceName,
    string? DeviceName,
    bool WasUserEntered = false);

/// <summary>How a value was obtained, so dashboards can tell measured from self-reported data (RF-43).</summary>
public enum HeartRateDataKind
{
    Measured = 1,
    SelfReported = 2,
}

/// <summary>
/// A stored heart-rate sample with provenance. Samples are never hard-deleted: a sample the source removed is
/// marked as such so history and audits stay intact. A HealthKit sample is stored once per client (README contract).
/// </summary>
public sealed class HeartRateSample : Entity<Guid>
{
    internal HeartRateSample(Guid clientId, HeartRateReading reading, DateTimeOffset receivedAt) : base(Guid.CreateVersion7(receivedAt))
    {
        ClientId = clientId;
        ExternalId = reading.ExternalId;
        ReceivedAt = receivedAt;
        Apply(reading);
    }

    private HeartRateSample() { }

    public Guid ClientId { get; private set; }

    public Guid ExternalId { get; private set; }

    public DateTimeOffset Timestamp { get; private set; }

    public double BeatsPerMinute { get; private set; }

    public string SourceName { get; private set; } = null!;

    public string? DeviceName { get; private set; }

    public HeartRateDataKind Kind { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? CorrectedAt { get; private set; }

    public DateTimeOffset? RemovedAt { get; private set; }

    public bool IsActive => RemovedAt is null;

    internal bool Matches(HeartRateReading reading) =>
        Timestamp == reading.Timestamp
        && BeatsPerMinute.Equals(reading.BeatsPerMinute)
        && SourceName == reading.SourceName
        && DeviceName == reading.DeviceName
        && Kind == KindOf(reading);

    internal void Correct(HeartRateReading reading, DateTimeOffset now)
    {
        Apply(reading);
        RemovedAt = null;
        CorrectedAt = now;
    }

    internal void MarkRemovedBySource(DateTimeOffset now) => RemovedAt ??= now;

    private static HeartRateDataKind KindOf(HeartRateReading reading) =>
        reading.WasUserEntered ? HeartRateDataKind.SelfReported : HeartRateDataKind.Measured;

    private void Apply(HeartRateReading reading)
    {
        Timestamp = reading.Timestamp;
        BeatsPerMinute = reading.BeatsPerMinute;
        SourceName = reading.SourceName;
        DeviceName = reading.DeviceName;
        Kind = KindOf(reading);
    }
}
