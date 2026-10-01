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
    TrainingPlanReviewRequested = 7,
    PhysicalAssessmentRecorded = 8,
    NutritionAssessmentRecorded = 9,
    MealPlanPublished = 10,
    FoodDiaryCommented = 11,
    PrivacyRequestUpdated = 12,
}

/// <summary>Push/e-mail delivery state (the notification row is its own outbox entry).</summary>
public enum NotificationDeliveryStatus
{
    Pending = 1,
    Delivered = 2,

    /// <summary>Gave up after <see cref="Notification.MaxDeliveryAttempts"/>; still visible in the app.</summary>
    Failed = 3,

    /// <summary>Created before external delivery existed, or no channel was available.</summary>
    Skipped = 4,
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

    public const int MaxDeliveryAttempts = 6;

    public NotificationDeliveryStatus DeliveryStatus { get; private set; }

    public int DeliveryAttempts { get; private set; }

    /// <summary>When the dispatcher may try again; <c>null</c> once delivery is settled.</summary>
    public DateTimeOffset? NextDeliveryAttemptAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    /// <summary>A provider error code only, never message content.</summary>
    public string? LastDeliveryError { get; private set; }

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
            DeliveryStatus = NotificationDeliveryStatus.Pending,
            NextDeliveryAttemptAt = now,
        };

    public void MarkDelivered(DateTimeOffset now)
    {
        DeliveryAttempts++;
        DeliveryStatus = NotificationDeliveryStatus.Delivered;
        DeliveredAt = now;
        NextDeliveryAttemptAt = null;
        LastDeliveryError = null;
    }

    /// <summary>No push device and no usable e-mail: the notification stays in the app only.</summary>
    public void MarkSkipped()
    {
        DeliveryStatus = NotificationDeliveryStatus.Skipped;
        NextDeliveryAttemptAt = null;
    }

    /// <summary>Schedules a retry with exponential back-off (1, 4, 16, 64… minutes) until the attempts run out.</summary>
    public void RecordDeliveryFailure(string errorCode, DateTimeOffset now)
    {
        DeliveryAttempts++;
        LastDeliveryError = errorCode.Length > 100 ? errorCode[..100] : errorCode;
        if (DeliveryAttempts >= MaxDeliveryAttempts)
        {
            DeliveryStatus = NotificationDeliveryStatus.Failed;
            NextDeliveryAttemptAt = null;
            return;
        }

        NextDeliveryAttemptAt = now + TimeSpan.FromMinutes(Math.Pow(4, DeliveryAttempts - 1));
    }

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
