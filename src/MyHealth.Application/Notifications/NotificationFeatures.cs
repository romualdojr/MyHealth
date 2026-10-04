using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Assessments;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Nutrition;
using MyHealth.Domain.Privacy;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Training.Exercises;
using MyHealth.Domain.Training.MotionCapture;
using MyHealth.Domain.Training.Plans;
using MyHealth.Domain.Training.Workouts;

namespace MyHealth.Application.Notifications;

public sealed record NotificationResponse(
    Guid Id, NotificationType Type, NotificationAudience Audience, Guid? TenantId, string ResourceType, Guid ResourceId, DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

/// <summary>
/// Without a selected tenant: the caller's client and account notifications, across tenants (MT-10). With a selected
/// tenant: the caller's professional notifications for that tenant only (MT-07), plus account notifications.
/// </summary>
public sealed record ListMyNotificationsQuery(bool UnreadOnly) : IQuery<IReadOnlyList<NotificationResponse>>;

internal sealed class ListMyNotificationsHandler(IUserContext user, ITenantContext tenant, IReadDbContext db)
    : IQueryHandler<ListMyNotificationsQuery, IReadOnlyList<NotificationResponse>>
{
    public async Task<Result<IReadOnlyList<NotificationResponse>>> Handle(ListMyNotificationsQuery query, CancellationToken cancellationToken)
    {
        var membership = await tenant.FindAsync(cancellationToken);
        if (membership.IsFailure)
        {
            return membership.Error;
        }

        var notifications = NotificationScope.Visible(db.Notifications, user.UserId, membership.Value?.TenantId);
        if (query.UnreadOnly)
        {
            notifications = notifications.Where(n => n.ReadAt == null);
        }

        var items = await notifications
            .OrderByDescending(n => n.CreatedAt)
            .Take(100)
            .Select(n => new NotificationResponse(n.Id, n.Type, n.Audience, n.TenantId, n.ResourceType, n.ResourceId, n.CreatedAt, n.ReadAt))
            .ToListAsync(cancellationToken);

        return items;
    }
}

internal static class NotificationScope
{
    public static IQueryable<Notification> Visible(IQueryable<Notification> notifications, Guid userId, Guid? activeTenantId) =>
        activeTenantId is { } tenantId
            ? notifications.Where(n => n.RecipientUserId == userId
                                       && (n.Audience == NotificationAudience.Account
                                           || (n.Audience == NotificationAudience.Professional && n.TenantId == tenantId)))
            : notifications.Where(n => n.RecipientUserId == userId && n.Audience != NotificationAudience.Professional);
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
/// Turns domain events into in-app notifications in the same transaction (RF-07). Each one carries the tenant of the
/// resource it points to. Each row is also an outbox entry: the notification dispatcher delivers it by push and e-mail
/// after the transaction commits, so a rolled-back change never notifies anyone.
/// </summary>
internal sealed class NotificationEventHandlers(INotificationRepository notifications, IReadDbContext db) :
    IDomainEventHandler<TrainingPlanPublished>,
    IDomainEventHandler<FeedbackReplied>,
    IDomainEventHandler<DiscomfortReported>,
    IDomainEventHandler<CareRelationshipActivated>,
    IDomainEventHandler<CareRelationshipEnded>,
    IDomainEventHandler<MotionCaptureFinished>,
    IDomainEventHandler<ProfessionalVerified>,
    IDomainEventHandler<TrainingPlanReviewRequested>,
    IDomainEventHandler<PhysicalAssessmentRecorded>,
    IDomainEventHandler<NutritionAssessmentRecorded>,
    IDomainEventHandler<MealPlanPublished>,
    IDomainEventHandler<FoodDiaryCommented>,
    IDomainEventHandler<PrivacyRequestUpdated>
{
    public Task Handle(TrainingPlanReviewRequested e, CancellationToken cancellationToken) =>
        Add(Notification.ForProfessional(
            e.TrainerUserId, e.TenantId, NotificationType.TrainingPlanReviewRequested, nameof(TrainingPlan), e.PlanId, e.OccurredAt));

    public Task Handle(PhysicalAssessmentRecorded e, CancellationToken cancellationToken) =>
        Add(Notification.ForClient(e.ClientId, e.TenantId, NotificationType.PhysicalAssessmentRecorded, nameof(PhysicalAssessment), e.AssessmentId, e.OccurredAt));

    public Task Handle(NutritionAssessmentRecorded e, CancellationToken cancellationToken) =>
        Add(Notification.ForClient(e.ClientId, e.TenantId, NotificationType.NutritionAssessmentRecorded, nameof(NutritionAssessment), e.AssessmentId, e.OccurredAt));

    public Task Handle(MealPlanPublished e, CancellationToken cancellationToken) =>
        Add(Notification.ForClient(e.ClientId, e.TenantId, NotificationType.MealPlanPublished, nameof(MealPlan), e.PlanId, e.OccurredAt));

    public Task Handle(FoodDiaryCommented e, CancellationToken cancellationToken) =>
        Add(Notification.ForClient(e.ClientId, e.TenantId, NotificationType.FoodDiaryCommented, nameof(FoodDiaryEntry), e.EntryId, e.OccurredAt));

    public Task Handle(PrivacyRequestUpdated e, CancellationToken cancellationToken) =>
        Add(Notification.ForAccount(e.UserId, NotificationType.PrivacyRequestUpdated, nameof(PrivacyRequest), e.RequestId, e.OccurredAt));

    public Task Handle(TrainingPlanPublished e, CancellationToken cancellationToken) =>
        Add(Notification.ForClient(e.ClientId, e.TenantId, NotificationType.TrainingPlanPublished, nameof(TrainingPlan), e.PlanId, e.OccurredAt));

    public Task Handle(FeedbackReplied e, CancellationToken cancellationToken) =>
        Add(Notification.ForClient(e.ClientId, e.TenantId, NotificationType.FeedbackReplied, nameof(WorkoutSession), e.WorkoutSessionId, e.OccurredAt));

    public Task Handle(DiscomfortReported e, CancellationToken cancellationToken) =>
        Add(Notification.ForProfessional(
            e.TrainerUserId, e.TenantId, NotificationType.DiscomfortReported, nameof(WorkoutSession), e.WorkoutSessionId, e.OccurredAt));

    public Task Handle(CareRelationshipActivated e, CancellationToken cancellationToken) =>
        Add(Notification.ForProfessional(
            e.ProfessionalUserId, e.TenantId, NotificationType.CareRelationshipActivated, nameof(CareRelationship), e.RelationshipId, e.OccurredAt));

    /// <summary>Only when the client ended it: the professional ended it themselves and already knows.</summary>
    public async Task Handle(CareRelationshipEnded e, CancellationToken cancellationToken)
    {
        if (e.EndedByUserId != e.ClientId)
        {
            return;
        }

        var professionalUserId = await db.ProfessionalProfiles.Where(p => p.Id == e.ProfessionalProfileId).Select(p => p.UserId).FirstAsync(cancellationToken);
        await Add(Notification.ForProfessional(
            professionalUserId, e.TenantId, NotificationType.CareRelationshipEndedByClient, nameof(CareRelationship), e.RelationshipId, e.OccurredAt));
    }

    public Task Handle(MotionCaptureFinished e, CancellationToken cancellationToken) =>
        Add(Notification.ForProfessional(
            e.RequestedByUserId, e.TenantId, e.Succeeded ? NotificationType.MotionCaptureReady : NotificationType.MotionCaptureFailed, nameof(Exercise),
            e.ExerciseId, e.OccurredAt));

    public Task Handle(ProfessionalVerified e, CancellationToken cancellationToken) =>
        Add(Notification.ForAccount(e.UserId, NotificationType.ProfessionalVerified, nameof(ProfessionalProfile), e.ProfessionalProfileId, e.OccurredAt));

    private Task Add(Notification notification)
    {
        notifications.Add(notification);
        return Task.CompletedTask;
    }
}
