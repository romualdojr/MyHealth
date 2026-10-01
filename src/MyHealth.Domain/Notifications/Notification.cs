using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Notifications;

public enum NotificationType
{
    TrainingPlanPublished = 1,
    FeedbackReplied = 2,
    DiscomfortReported = 3,
    WorkoutCompleted = 4,
    CareRelationshipActivated = 5,
    ProfessionalVerified = 6,
}

/// <summary>Which of the recipient's hats the notification is for, so tenant context can filter it (MT-07).</summary>
public enum NotificationAudience
{
    /// <summary>About the recipient's own care; shown in the client view, which spans tenants (MT-10).</summary>
    Client = 1,

    /// <summary>About work the recipient does in <see cref="Notification.TenantId"/>; shown only in that tenant's context.</summary>
    Professional = 2,

    /// <summary>About the account itself (e.g. credential verification); not tied to a tenant.</summary>
    Account = 3,
}

/// <summary>
/// An in-app notification. It carries a pointer to the content, never clinical details, so it is safe to show
/// on a lock screen or forward to a push provider.
/// </summary>
public sealed class Notification : AggregateRoot<Guid>
{
    private Notification(Guid id) : base(id) { }

    private Notification() { }

    public Guid RecipientUserId { get; private set; }

    public NotificationAudience Audience { get; private set; }

    /// <summary>The tenant of the referenced resource; <c>null</c> for <see cref="NotificationAudience.Account"/>.</summary>
    public Guid? TenantId { get; private set; }

    public NotificationType Type { get; private set; }

    public string ResourceType { get; private set; } = null!;

    public Guid ResourceId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public static Notification ForClient(
        Guid recipientUserId, Guid tenantId, NotificationType type, string resourceType, Guid resourceId, DateTimeOffset now) =>
        Create(recipientUserId, NotificationAudience.Client, tenantId, type, resourceType, resourceId, now);

    public static Notification ForProfessional(
        Guid recipientUserId, Guid tenantId, NotificationType type, string resourceType, Guid resourceId, DateTimeOffset now) =>
        Create(recipientUserId, NotificationAudience.Professional, tenantId, type, resourceType, resourceId, now);

    public static Notification ForAccount(Guid recipientUserId, NotificationType type, string resourceType, Guid resourceId, DateTimeOffset now) =>
        Create(recipientUserId, NotificationAudience.Account, null, type, resourceType, resourceId, now);

    private static Notification Create(
        Guid recipientUserId, NotificationAudience audience, Guid? tenantId, NotificationType type, string resourceType, Guid resourceId,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7(now))
        {
            RecipientUserId = recipientUserId,
            Audience = audience,
            TenantId = tenantId,
            Type = type,
            ResourceType = resourceType,
            ResourceId = resourceId,
            CreatedAt = now,
        };

    public Result MarkAsRead(Guid userId, DateTimeOffset now)
    {
        if (userId != RecipientUserId)
        {
            return NotificationErrors.NotFound;
        }

        ReadAt ??= now;
        return Result.Success();
    }
}

public interface INotificationRepository : IRepository<Notification, Guid>;

public static class NotificationErrors
{
    public static readonly Error NotFound = Error.NotFound("Notification.NotFound", "The notification was not found.");
}
