using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Notifications;

public enum PushPlatform
{
    Ios = 1,
    Android = 2,
}

/// <summary>
/// A phone that receives push notifications for a user. Keyed by the app installation, so when someone else signs in on
/// the same phone the device moves to them and the previous user stops receiving pushes there.
/// </summary>
public sealed class PushDevice : AggregateRoot<Guid>
{
    private PushDevice(Guid id) : base(id) { }

    private PushDevice() { }

    public Guid InstallationId { get; private set; }

    public Guid UserId { get; private set; }

    public PushPlatform Platform { get; private set; }

    public string Token { get; private set; } = null!;

    public DateTimeOffset RegisteredAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PushDevice Register(Guid installationId, Guid userId, PushPlatform platform, string token, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now))
        {
            InstallationId = installationId,
            UserId = userId,
            Platform = platform,
            Token = token.Trim(),
            RegisteredAt = now,
            UpdatedAt = now,
        };

    public void Update(Guid userId, PushPlatform platform, string token, DateTimeOffset now)
    {
        UserId = userId;
        Platform = platform;
        Token = token.Trim();
        UpdatedAt = now;
    }
}

public interface IPushDeviceRepository : IRepository<PushDevice, Guid>
{
    Task<PushDevice?> GetByInstallationAsync(Guid installationId, CancellationToken cancellationToken = default);

    void Remove(PushDevice device);
}
