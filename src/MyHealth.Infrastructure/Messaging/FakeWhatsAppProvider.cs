using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.WhatsApp;
using MyHealth.Domain.Messaging;

namespace MyHealth.Infrastructure.Messaging;

/// <summary>
/// Stands in for the WhatsApp provider until one is chosen. Sending logs the template and a masked number and keeps the
/// last messages in memory (development and tests read the verification code from <see cref="Sent"/>); numbers ending
/// in 0000 are refused as not on WhatsApp. Webhooks are JSON <c>{"messages":[{"id","from","text","timestamp"}]}</c>
/// signed with HMAC-SHA256 over the raw body (header <c>X-Fake-Signature</c>, secret <c>WhatsApp:Fake:WebhookSecret</c>).
/// </summary>
public sealed partial class FakeWhatsAppProvider(IConfiguration configuration, TimeProvider clock, ILogger<FakeWhatsAppProvider> logger) : IWhatsAppProvider
{
    public const string SignatureHeader = "X-Fake-Signature";
    private const int Kept = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentQueue<WhatsAppTemplateMessage> _sent = new();

    public string Name => "fake";

    /// <summary>The most recent messages "sent", oldest first.</summary>
    public IReadOnlyCollection<WhatsAppTemplateMessage> Sent => _sent;

    private byte[] Secret => Encoding.UTF8.GetBytes(configuration["WhatsApp:Fake:WebhookSecret"] ?? "myhealth-fake-whatsapp-secret");

    public Task<WhatsAppSendResult> SendTemplateAsync(WhatsAppTemplateMessage message, CancellationToken cancellationToken)
    {
        if (message.PhoneNumber.EndsWith("0000", StringComparison.Ordinal))
        {
            return Task.FromResult(new WhatsAppSendResult(WhatsAppSendStatus.InvalidRecipient, Error: "not-on-whatsapp"));
        }

        _sent.Enqueue(message);
        while (_sent.Count > Kept && _sent.TryDequeue(out _))
        {
        }

        LogSent(logger, message.Template, PhoneNumbers.Mask(message.PhoneNumber));
        return Task.FromResult(new WhatsAppSendResult(WhatsAppSendStatus.Sent, $"fake-{Guid.CreateVersion7(clock.GetUtcNow())}"));
    }

    public IReadOnlyList<WhatsAppInboundMessage>? ReadWebhook(string body, IReadOnlyDictionary<string, string> headers)
    {
        var signature = headers.FirstOrDefault(h => string.Equals(h.Key, SignatureHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrEmpty(signature)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(body)), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant())))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(body, Json);
            return
            [
                .. (payload?.Messages ?? [])
                    .Where(m => m.Id is not null && m.From is not null)
                    .Select(m => new WhatsAppInboundMessage(m.Id!, m.From!, m.Text ?? string.Empty, m.Timestamp ?? clock.GetUtcNow())),
            ];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string? AnswerVerification(IReadOnlyDictionary<string, string> query)
    {
        var token = configuration["WhatsApp:VerifyToken"] ?? "myhealth-fake-verify-token";
        return query.GetValueOrDefault("hub.mode") == "subscribe" && query.GetValueOrDefault("hub.verify_token") == token
            ? query.GetValueOrDefault("hub.challenge")
            : null;
    }

    /// <summary>Hex HMAC-SHA256 of the body, as the fake provider signs its webhooks.</summary>
    public string Sign(string body) => Convert.ToHexStringLower(HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes(body)));

    [LoggerMessage(Level = LogLevel.Information, Message = "WhatsApp template {Template} to {Phone} (fake provider)")]
    private static partial void LogSent(ILogger logger, string template, string phone);

    private sealed record Payload(IReadOnlyList<InboundMessage>? Messages);

    private sealed record InboundMessage(string? Id, string? From, string? Text, DateTimeOffset? Timestamp);
}
