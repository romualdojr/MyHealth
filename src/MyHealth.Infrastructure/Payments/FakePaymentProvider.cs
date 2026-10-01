using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MyHealth.Application.Abstractions.Payments;
using MyHealth.Domain.Billing;

namespace MyHealth.Infrastructure.Payments;

/// <summary>
/// A stand-in payment provider with the same contract as a real one: charges with a Pix copy-and-paste code and a
/// checkout URL, and webhooks signed with HMAC-SHA256 over the raw body (header <c>X-Fake-Signature</c>). Nothing is
/// charged. Replace it with a real adapter (Asaas, Pagar.me, Mercado Pago, Iugu, Stripe) when the provider is chosen.
/// </summary>
internal sealed class FakePaymentProvider(IConfiguration configuration, TimeProvider clock) : IPaymentProvider, IPaymentSimulator
{
    public const string ProviderName = "fake";
    public const string SignatureHeader = "X-Fake-Signature";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => ProviderName;

    private byte[] Secret => Encoding.UTF8.GetBytes(configuration["Payments:Fake:WebhookSecret"] ?? "myhealth-fake-webhook-secret");

    public Task<ProviderCharge> CreateChargeAsync(ChargeRequest request, CancellationToken cancellationToken)
    {
        var chargeId = $"fake_ch_{Guid.CreateVersion7():N}";
        var amount = (request.AmountCents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        var charge = new ProviderCharge(
            ProviderName,
            chargeId,
            $"00020126580014BR.GOV.BCB.PIX0136MYHEALTH-FAKE-{chargeId}5204000053039865406{amount}5802BR5913MYHEALTH FAKE6009SAO PAULO6304FAKE",
            $"https://pagamentos.fake.myhealth.local/checkout/{chargeId}",
            clock.GetUtcNow().AddDays(1));
        return Task.FromResult(charge);
    }

    public PaymentNotification? ReadWebhook(string body, IReadOnlyDictionary<string, string> headers)
    {
        var signature = headers.FirstOrDefault(h => string.Equals(h.Key, SignatureHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrEmpty(signature) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(body)), Encoding.ASCII.GetBytes(signature)))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(body, Json);
            return payload is null
                ? null
                : new PaymentNotification(payload.EventId, payload.Type, payload.ChargeId, payload.AmountCents, payload.PaymentId, payload.OccurredAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public (string Body, IReadOnlyDictionary<string, string> Headers) SimulatePayment(ProviderCharge charge, long amountCents)
    {
        var body = JsonSerializer.Serialize(
            new Payload($"fake_evt_{Guid.CreateVersion7():N}", PaymentNotification.Paid, charge.ChargeId, amountCents, $"fake_pay_{Guid.CreateVersion7():N}", clock.GetUtcNow()),
            Json);
        return (body, new Dictionary<string, string> { [SignatureHeader] = Sign(body) });
    }

    /// <summary>Hex HMAC-SHA256 of the body, as a provider signs its webhooks.</summary>
    public string Sign(string body) => Convert.ToHexStringLower(HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes(body)));

    private sealed record Payload(string EventId, string Type, string ChargeId, long AmountCents, string PaymentId, DateTimeOffset OccurredAt);
}
