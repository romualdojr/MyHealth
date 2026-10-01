using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Application.Notifications;

public sealed record NotificationResponse(Guid Id, NotificationType Type, string ResourceType, Guid ResourceId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record ListMyNotificationsQuery(bool UnreadOnly) : IQuery<IReadOnlyList<NotificationResponse>>;

internal sealed class ListMyNotificationsHandler(IUserContext user, IReadDbContext db)
    : IQueryHandler<ListMyNotificationsQuery, IReadOnlyList<NotificationResponse>>
{
    public async Task<Result<IReadOnlyList<NotificationResponse>>> Handle(ListMyNotificationsQuery query, CancellationToken cancellationToken)
    {
        var notifications = db.Notifications.Where(n => n.RecipientUserId == user.UserId);
        if (query.UnreadOnly)
        {
            notifications = notifications.Where(n => n.ReadAt == null);
        }

        var items = await notifications
            .OrderByDescending(n => n.CreatedAt)
            .Take(100)
            .Select(n => new NotificationResponse(n.Id, n.Type, n.ResourceType, n.ResourceId, n.CreatedAt, n.ReadAt))
            .ToListAsync(cancellationToken);

        return items;
    }
}

public sealed record MarkNotificationReadCommand(Guid NotificationId) : ICommand;

internal sealed class MarkNotificationReadHandler(IUserContext user, INotificationRepository notifications, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<MarkNotificationReadCommand>
{
    public async Task<Result> Handle(MarkNotificationReadCommand command, CancellationToken cancellationToken)
    {
        var notification = await notifications.GetByIdAsync(command.NotificationId, cancellationToken);
        if (notification is null)
        {
            return NotificationErrors.NotFound;
        }

        var result = notification.MarkAsRead(user.UserId, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>
/// Turns domain events into in-app notifications in the same transaction (RF-07). Push/e-mail delivery will be
/// driven from these rows by an outbox processor in Phase 2.
/// </summary>
internal sealed class NotificationEventHandlers(INotificationRepository notifications) :
    IDomainEventHandler<TrainingPlanPublished>,
    IDomainEventHandler<FeedbackReplied>,
    IDomainEventHandler<DiscomfortReported>,
    IDomainEventHandler<CareRelationshipActivated>,
    IDomainEventHandler<ProfessionalVerified>
{
    public Task Handle(TrainingPlanPublished domainEvent, CancellationToken cancellationToken) =>
        Notify(domainEvent.ClientId, NotificationType.TrainingPlanPublished, nameof(TrainingPlan), domainEvent.PlanId, domainEvent.OccurredAt);

    public Task Handle(FeedbackReplied domainEvent, CancellationToken cancellationToken) =>
        Notify(domainEvent.ClientId, NotificationType.FeedbackReplied, nameof(WorkoutSession), domainEvent.WorkoutSessionId, domainEvent.OccurredAt);

    public Task Handle(DiscomfortReported domainEvent, CancellationToken cancellationToken) =>
        Notify(domainEvent.TrainerUserId, NotificationType.DiscomfortReported, nameof(WorkoutSession), domainEvent.WorkoutSessionId, domainEvent.OccurredAt);

    public Task Handle(CareRelationshipActivated domainEvent, CancellationToken cancellationToken) =>
        Notify(domainEvent.ProfessionalUserId, NotificationType.CareRelationshipActivated, nameof(CareRelationship), domainEvent.RelationshipId, domainEvent.OccurredAt);

    public Task Handle(ProfessionalVerified domainEvent, CancellationToken cancellationToken) =>
        Notify(domainEvent.UserId, NotificationType.ProfessionalVerified, nameof(ProfessionalProfile), domainEvent.ProfessionalProfileId, domainEvent.OccurredAt);

    private Task Notify(Guid recipient, NotificationType type, string resourceType, Guid resourceId, DateTimeOffset now)
    {
        notifications.Add(Notification.Create(recipient, type, resourceType, resourceId, now));
        return Task.CompletedTask;
    }
}
