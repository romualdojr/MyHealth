using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyHealth.Domain.Notifications;
using MyHealth.Infrastructure.Persistence;

namespace MyHealth.Infrastructure.Notifications;

/// <summary>
/// Delivers committed notifications by push and e-mail (transactional outbox). Rows are claimed with
/// <c>FOR UPDATE SKIP LOCKED</c> and a lease, so several API instances can run it side by side, and a crashed
/// instance's claims become due again when the lease ends. Each notification's outcome is saved as soon as it is sent,
/// and no send starts once the lease is close to expiring, so another instance never re-claims a row this one is still
/// working on. Delivery is at-least-once: only a crash between a send and its save can repeat it.
/// Channel rule: push to every registered device; e-mail only when the person has no device, to avoid duplicate alerts.
/// </summary>
public sealed class NotificationDispatcher(
    ApplicationDbContext db, IPushSender push, INotificationEmailSender email, TimeProvider clock, ILogger<NotificationDispatcher> logger)
{
    public const int BatchSize = 50;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LeaseSafetyMargin = TimeSpan.FromMinutes(1);

    /// <summary>Delivers one batch of due notifications and returns how many were settled.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var leaseUntil = now + Lease;
        var pending = nameof(NotificationDeliveryStatus.Pending);

        var claimed = await db.Database
            .SqlQuery<Guid>($"""
                UPDATE myhealth.notifications SET next_delivery_attempt_at = {leaseUntil}
                WHERE id IN (
                    SELECT id FROM myhealth.notifications
                    WHERE delivery_status = {pending} AND next_delivery_attempt_at <= {now}
                    ORDER BY next_delivery_attempt_at
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED)
                RETURNING id AS "Value"
                """)
            .ToListAsync(cancellationToken);

        if (claimed.Count == 0)
        {
            return 0;
        }

        var notifications = await db.Notifications.Where(n => claimed.Contains(n.Id)).ToListAsync(cancellationToken);
        var recipientIds = notifications.Select(n => n.RecipientUserId).Distinct().ToList();
        var devices = (await db.PushDevices.Where(d => recipientIds.Contains(d.UserId)).ToListAsync(cancellationToken)).ToLookup(d => d.UserId);
        var emails = await db.UserProfiles.Where(u => recipientIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email, cancellationToken);

        var settled = 0;
        foreach (var notification in notifications)
        {
            // Leave the rest for the next round rather than racing an instance that re-claims them after the lease.
            if (clock.GetUtcNow() > leaseUntil - LeaseSafetyMargin)
            {
                break;
            }

            await DeliverAsync(notification, devices[notification.RecipientUserId].ToList(), emails.GetValueOrDefault(notification.RecipientUserId), cancellationToken);
            await db.SaveChangesAsync(CancellationToken.None);
            settled++;
        }

        return settled;
    }

    private async Task DeliverAsync(Notification notification, List<PushDevice> devices, string? emailAddress, CancellationToken cancellationToken)
    {
        if (devices.Count == 0 && string.IsNullOrWhiteSpace(emailAddress))
        {
            notification.MarkSkipped();
            return;
        }

        var message = NotificationTexts.Compose(notification);
        var outcomes = devices.Count > 0
            ? await Task.WhenAll(devices.Select(d => SendAsync(notification, () => push.SendAsync(d, message, cancellationToken))))
            : [await SendAsync(notification, () => email.SendAsync(emailAddress!, message, cancellationToken))];

        // One device that received it is enough; retrying would duplicate the alert on the devices that did.
        if (outcomes.Any(o => o.Delivered))
        {
            notification.MarkDelivered(clock.GetUtcNow());
        }
        else
        {
            notification.RecordDeliveryFailure(outcomes.Select(o => o.Error).FirstOrDefault(e => e is not null) ?? "provider.rejected", clock.GetUtcNow());
        }
    }

    private async Task<(bool Delivered, string? Error)> SendAsync(Notification notification, Func<Task<bool>> send)
    {
        try
        {
            return (await send(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception type only: provider messages can echo addresses or tokens.
            logger.LogWarning("Delivery of notification {NotificationId} failed with {ExceptionType}", notification.Id, ex.GetType().Name);
            return (false, ex.GetType().Name);
        }
    }
}


/// <summary>Runs the dispatcher on an interval (<c>Notifications:DispatchIntervalSeconds</c>, default 15).</summary>
internal sealed class NotificationDispatcherService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<NotificationDispatcherService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("Notifications:DispatchIntervalSeconds", 15));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                int processed;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    processed = await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchDueAsync(stoppingToken);
                }
                while (processed == NotificationDispatcher.BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notification dispatch round failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
