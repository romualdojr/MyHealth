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

/// <summary>
/// An in-app notification. It carries a pointer to the content, never clinical details, so it is safe to show
/// on a lock screen or forward to a push provider.
/// </summary>
public sealed class Notification : AggregateRoot<Guid>
{
    private Notification(Guid id) : base(id) { }

    private Notification() { }

    public Guid RecipientUserId { get; private set; }

    public NotificationType Type { get; private set; }

    public string ResourceType { get; private set; } = null!;

    public Guid ResourceId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public static Notification Create(Guid recipientUserId, NotificationType type, string resourceType, Guid resourceId, DateTimeOffset now) =>
        new(Guid.CreateVersion7(now))
        {
            RecipientUserId = recipientUserId,
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
