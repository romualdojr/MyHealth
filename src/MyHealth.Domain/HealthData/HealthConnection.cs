using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.HealthData;

public enum HealthDataProvider
{
    AppleHealth = 1,
}

public enum HealthConnectionStatus
{
    /// <summary>Heart-rate import was never turned on (the client may still save workouts to Apple Health).</summary>
    NotConnected = 0,
    Connected = 1,
    Paused = 2,
    Revoked = 3,
}

/// <summary>
/// The client's choices about a health platform. Connect, pause and revoke the heart-rate <em>import</em> are separate
/// actions; saving workouts <em>to</em> Apple Health is a separate opt-in because iOS grants write permission
/// separately from read permission (HK-06). Workouts keep working in every state. The OS permissions live on the phone.
/// </summary>
public sealed class HealthConnection : AggregateRoot<Guid>
{
    /// <summary>How long after a workout ends late samples may still be reconciled (HK-08).</summary>
    public static readonly TimeSpan ReconciliationWindow = TimeSpan.FromDays(30);

    private readonly List<HealthConnectionChange> _history = [];

    private HealthConnection(Guid id, Guid clientId, HealthDataProvider provider) : base(id)
    {
        ClientId = clientId;
        Provider = provider;
    }

    private HealthConnection() { }

    public Guid ClientId { get; private set; }

    public HealthDataProvider Provider { get; private set; }

    public HealthConnectionStatus Status { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The client opted in to saving completed workouts to Apple Health.</summary>
    public bool SaveWorkouts { get; private set; }

    public DateTimeOffset? SaveWorkoutsChangedAt { get; private set; }

    public IReadOnlyCollection<HealthConnectionChange> History => _history.AsReadOnly();

    public bool AllowsImport => Status == HealthConnectionStatus.Connected;

    /// <summary>Professionals stop receiving heart rate once the client revokes the connection (RF-43).</summary>
    public bool AllowsSharing => Status != HealthConnectionStatus.Revoked;

    /// <summary>A record with no import, e.g. when the client only opts in to saving workouts.</summary>
    public static HealthConnection Create(Guid clientId, HealthDataProvider provider, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now), clientId, provider) { UpdatedAt = now };

    public static HealthConnection Connect(Guid clientId, HealthDataProvider provider, DateTimeOffset now)
    {
        var connection = Create(clientId, provider, now);
        connection.ChangeTo(HealthConnectionStatus.Connected, now);
        return connection;
    }

    public void Reconnect(DateTimeOffset now) => ChangeTo(HealthConnectionStatus.Connected, now);

    public Result Pause(DateTimeOffset now)
    {
        if (Status == HealthConnectionStatus.Revoked)
        {
            return HealthDataErrors.ConnectionRevoked;
        }

        if (Status == HealthConnectionStatus.NotConnected)
        {
            return HealthDataErrors.ImportNotAllowed;
        }

        ChangeTo(HealthConnectionStatus.Paused, now);
        return Result.Success();
    }

    /// <summary>Revoking also turns off workout saving: the client is withdrawing from Apple Health altogether.</summary>
    public void Revoke(DateTimeOffset now)
    {
        ChangeTo(HealthConnectionStatus.Revoked, now);
        SetWorkoutSaving(false, now);
    }

    public void SetWorkoutSaving(bool enabled, DateTimeOffset now)
    {
        if (SaveWorkouts == enabled)
        {
            return;
        }

        SaveWorkouts = enabled;
        SaveWorkoutsChangedAt = now;
    }

    /// <summary>
    /// Periods in which the client had paused or revoked import. Samples recorded then are never imported, even if the
    /// phone uploads them later. Before the first connection nothing is excluded: connecting authorizes the workout in
    /// progress and earlier workouts still inside the reconciliation window.
    /// </summary>
    public IReadOnlyList<TimeInterval> ExcludedIntervals()
    {
        var ordered = _history.OrderBy(h => h.ChangedAt).ToList();
        var excluded = new List<TimeInterval>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Status == HealthConnectionStatus.Connected)
            {
                continue;
            }

            var reconnect = ordered.Skip(i + 1).FirstOrDefault(h => h.Status == HealthConnectionStatus.Connected);
            excluded.Add(new TimeInterval(ordered[i].ChangedAt, reconnect?.ChangedAt));
        }

        return excluded;
    }

    private void ChangeTo(HealthConnectionStatus status, DateTimeOffset now)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        UpdatedAt = now;
        _history.Add(new HealthConnectionChange(Guid.CreateVersion7(now), status, now));
    }
}

public sealed class HealthConnectionChange : Entity<Guid>
{
    internal HealthConnectionChange(Guid id, HealthConnectionStatus status, DateTimeOffset changedAt) : base(id)
    {
        Status = status;
        ChangedAt = changedAt;
    }

    private HealthConnectionChange() { }

    public HealthConnectionStatus Status { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }
}

public interface IHealthConnectionRepository : IRepository<HealthConnection, Guid>
{
    Task<HealthConnection?> GetAsync(Guid clientId, HealthDataProvider provider, CancellationToken cancellationToken = default);
}
