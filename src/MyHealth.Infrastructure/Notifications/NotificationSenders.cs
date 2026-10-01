using Microsoft.Extensions.Logging;
using MyHealth.Domain.Notifications;

namespace MyHealth.Infrastructure.Notifications;

/// <summary>
/// What leaves the platform for a notification: a generic title and body plus a pointer for the app to open. Never
/// clinical content, so it is safe on a lock screen, in an e-mail inbox and at the push provider.
/// </summary>
public sealed record OutgoingNotification(Guid NotificationId, NotificationType Type, string Title, string Body, string ResourceType, Guid ResourceId)
{
    public string DeepLink => $"myhealth://notifications/{NotificationId}";
}

/// <summary>Sends a push to one device. Throw or return <c>false</c> on failure; the dispatcher retries with back-off.</summary>
public interface IPushSender
{
    Task<bool> SendAsync(PushDevice device, OutgoingNotification notification, CancellationToken cancellationToken);
}

/// <summary>Sends a notification e-mail. Throw or return <c>false</c> on failure; the dispatcher retries with back-off.</summary>
public interface INotificationEmailSender
{
    Task<bool> SendAsync(string email, OutgoingNotification notification, CancellationToken cancellationToken);
}

/// <summary>
/// Placeholder until the push and e-mail providers are chosen (§8 "verify provider costs and rules"). Logs that a
/// delivery happened — the notification id and type only, never the recipient's address or token.
/// </summary>
internal sealed class LoggingNotificationSender(ILogger<LoggingNotificationSender> logger) : IPushSender, INotificationEmailSender
{
    public Task<bool> SendAsync(PushDevice device, OutgoingNotification notification, CancellationToken cancellationToken)
    {
        logger.LogInformation("Push {NotificationType} {NotificationId} to a {Platform} device", notification.Type, notification.NotificationId, device.Platform);
        return Task.FromResult(true);
    }

    public Task<bool> SendAsync(string email, OutgoingNotification notification, CancellationToken cancellationToken)
    {
        logger.LogInformation("E-mail {NotificationType} {NotificationId}", notification.Type, notification.NotificationId);
        return Task.FromResult(true);
    }
}

/// <summary>Brazilian Portuguese texts (RNF-07). They say what kind of update there is, never what it contains.</summary>
internal static class NotificationTexts
{
    public static OutgoingNotification Compose(Notification n)
    {
        var (title, body) = n.Type switch
        {
            NotificationType.TrainingPlanPublished => ("Treino atualizado", "Seu profissional publicou uma nova versão do seu plano de treino."),
            NotificationType.FeedbackReplied => ("Nova resposta", "Seu treinador respondeu ao seu feedback."),
            NotificationType.DiscomfortReported => ("Relato de desconforto", "Um cliente relatou desconforto em um treino."),
            NotificationType.WorkoutCompleted => ("Treino concluído", "Um cliente concluiu um treino."),
            NotificationType.CareRelationshipActivated => ("Novo vínculo", "Um cliente aceitou seu convite."),
            NotificationType.ProfessionalVerified => ("Cadastro verificado", "Seu registro profissional foi verificado."),
            NotificationType.TrainingPlanReviewRequested => ("Revisão sugerida", "Um plano de treino precisa da sua revisão."),
            NotificationType.PhysicalAssessmentRecorded => ("Nova avaliação", "Uma nova avaliação física foi registrada."),
            NotificationType.NutritionAssessmentRecorded => ("Nova avaliação", "Uma nova avaliação nutricional foi registrada."),
            NotificationType.MealPlanPublished => ("Plano alimentar atualizado", "Sua nutricionista publicou uma nova versão do seu plano alimentar."),
            NotificationType.FoodDiaryCommented => ("Novo comentário", "Sua nutricionista comentou no seu diário alimentar."),
            NotificationType.PrivacyRequestUpdated => ("Solicitação de privacidade", "Há uma atualização na sua solicitação sobre seus dados."),
            NotificationType.TherapySessionScheduled => ("Sessão agendada", "Uma sessão de terapia foi agendada ou remarcada."),
            NotificationType.TherapySessionCancelled => ("Sessão cancelada", "Uma sessão de terapia foi cancelada."),
            NotificationType.TherapySessionCompleted => ("Orientações disponíveis", "As orientações da sua última sessão estão disponíveis."),
            NotificationType.MedicalReferralRecommended => ("Orientação importante", "Seu terapeuta deixou uma orientação importante para você."),
            NotificationType.TherapyProgressReported => ("Retorno do cliente", "Um cliente contou como se sentiu após a sessão."),
            NotificationType.TherapyConsentChanged => ("Consentimento atualizado", "Um cliente atualizou um consentimento."),
            NotificationType.TherapyCarePlanReviewed => ("Plano de cuidado revisado", "Seu plano de cuidado foi revisado."),
            NotificationType.InvoiceIssued => ("Nova fatura", "Há uma nova fatura do MyHealth para a sua organização."),
            NotificationType.SubscriptionStatusChanged => ("Assinatura", "A situação da assinatura da sua organização mudou."),
            _ => ("MyHealth", "Você tem uma nova atualização."),
        };

        return new OutgoingNotification(n.Id, n.Type, title, body, n.ResourceType, n.ResourceId);
    }
}
