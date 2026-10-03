namespace MyHealth.Application.Abstractions.WhatsApp;

/// <summary>
/// A pre-approved WhatsApp template (business-initiated messages must use one). Parameters fill the template's
/// placeholders in order. MyHealth templates carry generic texts only — never clinical content.
/// </summary>
public sealed record WhatsAppTemplateMessage(string PhoneNumber, string Template, string Language, IReadOnlyList<string> Parameters);

public enum WhatsAppSendStatus
{
    Sent = 1,

    /// <summary>Temporary problem (provider down, rate limit): try again later.</summary>
    Failed = 2,

    /// <summary>The number does not use WhatsApp or the provider refuses it permanently.</summary>
    InvalidRecipient = 3,
}

public sealed record WhatsAppSendResult(WhatsAppSendStatus Status, string? ProviderMessageId = null, string? Error = null)
{
    public bool IsSent => Status == WhatsAppSendStatus.Sent;
}

/// <summary>A message someone sent to MyHealth's WhatsApp number, read from a verified webhook.</summary>
public sealed record WhatsAppInboundMessage(string ProviderMessageId, string FromPhoneNumber, string Text, DateTimeOffset ReceivedAt);

/// <summary>
/// The WhatsApp Business provider (Meta Cloud API directly, or a BSP such as Twilio, Zenvia, Gupshup or Take Blip). A
/// real provider is a new implementation of this interface plus its configuration; opt-in, opt-out, templates and the
/// notification outbox do not change. Until one is chosen a fake implementation logs what would be sent.
/// </summary>
public interface IWhatsAppProvider
{
    string Name { get; }

    /// <summary>Sends a template message. Never throws for provider errors: the status says what happened.</summary>
    Task<WhatsAppSendResult> SendTemplateAsync(WhatsAppTemplateMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies the webhook's signature and reads the messages it carries (status callbacks are ignored). Returns
    /// <c>null</c> when the signature is missing or wrong — such a request changes nothing.
    /// </summary>
    IReadOnlyList<WhatsAppInboundMessage>? ReadWebhook(string body, IReadOnlyDictionary<string, string> headers);

    /// <summary>
    /// Answers the provider's subscription check (e.g. Meta's <c>hub.challenge</c>) with the expected response, or
    /// <c>null</c> when the request is not a valid check.
    /// </summary>
    string? AnswerVerification(IReadOnlyDictionary<string, string> query);
}

/// <summary>The templates MyHealth uses. Each must be registered and approved at the provider with these names and placeholders.</summary>
public static class WhatsAppTemplates
{
    public const string Language = "pt_BR";

    /// <summary>"Seu código de confirmação do MyHealth é {{1}}. Ele vale por 10 minutos." (authentication category)</summary>
    public const string VerificationCode = "myhealth_codigo";

    /// <summary>"*{{1}}* {{2}} Abra o app MyHealth para ver. Para não receber mais, responda SAIR." (utility category, button to the app)</summary>
    public const string Notification = "myhealth_aviso";

    /// <summary>"Você não vai mais receber mensagens do MyHealth no WhatsApp. Para voltar, ative no app." (utility category)</summary>
    public const string OptOutConfirmation = "myhealth_saida";
}
