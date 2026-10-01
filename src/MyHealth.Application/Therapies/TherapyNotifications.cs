using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.CareRelationships;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Therapies;

namespace MyHealth.Application.Therapies;

/// <summary>Ending a care relationship cancels its scheduled therapy sessions in the same transaction (MT-08).</summary>
internal sealed class RelationshipEndedTherapyHandler(ITherapySessionRepository sessions, IAuditLog audit) : IDomainEventHandler<CareRelationshipEnded>
{
    public async Task Handle(CareRelationshipEnded e, CancellationToken cancellationToken)
    {
        foreach (var session in await sessions.ListScheduledAsync(e.TenantId, e.ProfessionalProfileId, e.ClientId, cancellationToken))
        {
            session.CancelBecauseRelationshipEnded(e.OccurredAt);
            audit.Record(AuditActions.Cancel, nameof(TherapySession), session.Id, e.ClientId, e.TenantId);
        }
    }
}

/// <summary>
/// In-app notifications (and outbox entries) for therapies (RF-36, journey 6). Like every notification they carry a
/// pointer only: a medical-care referral says "there is an update", never the warning signs.
/// </summary>
internal sealed class TherapyNotificationHandlers(INotificationRepository notifications) :
    IDomainEventHandler<TherapySessionScheduled>,
    IDomainEventHandler<TherapySessionCancelled>,
    IDomainEventHandler<TherapySessionCompleted>,
    IDomainEventHandler<MedicalReferralRecommended>,
    IDomainEventHandler<TherapyProgressReported>,
    IDomainEventHandler<TherapyConsentChanged>,
    IDomainEventHandler<TherapyCarePlanReviewed>
{
    private const string Session = nameof(TherapySession);

    public Task Handle(TherapySessionScheduled e, CancellationToken cancellationToken) =>
        Add(Notification.ForClient(e.ClientId, e.TenantId, NotificationType.TherapySessionScheduled, Session, e.SessionId, e.OccurredAt));

    public Task Handle(TherapySessionCancelled e, CancellationToken cancellationToken) =>
        Add(e.CancelledByClient
            ? Notification.ForProfessional(e.PractitionerUserId, e.TenantId, NotificationType.TherapySessionCancelled, Session, e.SessionId, e.OccurredAt)
            : Notification.ForClient(e.ClientId, e.TenantId, NotificationType.TherapySessionCancelled, Session, e.SessionId, e.OccurredAt));

    public Task Handle(TherapySessionCompleted e, CancellationToken cancellationToken) =>
        Add(Notification.ForClient(e.ClientId, e.TenantId, NotificationType.TherapySessionCompleted, Session, e.SessionId, e.OccurredAt));

    public Task Handle(MedicalReferralRecommended e, CancellationToken cancellationToken) =>
        Add(Notification.ForClient(e.ClientId, e.TenantId, NotificationType.MedicalReferralRecommended, Session, e.SessionId, e.OccurredAt));

    public Task Handle(TherapyProgressReported e, CancellationToken cancellationToken) =>
        Add(Notification.ForProfessional(e.PractitionerUserId, e.TenantId, NotificationType.TherapyProgressReported, Session, e.SessionId, e.OccurredAt));

    public Task Handle(TherapyConsentChanged e, CancellationToken cancellationToken) =>
        Add(Notification.ForProfessional(e.PractitionerUserId, e.TenantId, NotificationType.TherapyConsentChanged, nameof(TherapyConsent), e.ConsentId, e.OccurredAt));

    public Task Handle(TherapyCarePlanReviewed e, CancellationToken cancellationToken) =>
        Add(Notification.ForClient(e.ClientId, e.TenantId, NotificationType.TherapyCarePlanReviewed, nameof(TherapyCarePlan), e.CarePlanId, e.OccurredAt));

    private Task Add(Notification notification)
    {
        notifications.Add(notification);
        return Task.CompletedTask;
    }
}
