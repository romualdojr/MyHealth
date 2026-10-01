using MyHealth.Domain.Billing;

namespace MyHealth.Application.Abstractions.Payments;

/// <summary>What the provider needs to create a charge: the invoice and who pays it.</summary>
public sealed record ChargeRequest(Guid InvoiceId, long AmountCents, string Currency, string Description, string PayerName, string PayerEmail, DateTimeOffset DueAt);

/// <summary>A payment confirmed (or refused) by the provider, read from a verified webhook.</summary>
public sealed record PaymentNotification(string EventId, string Type, string ChargeId, long AmountCents, string ProviderPaymentId, DateTimeOffset OccurredAt)
{
    public const string Paid = "charge.paid";
}

/// <summary>
/// The payment service provider (Pix and cards, §8 "Payments"). MyHealth never sees card data: the provider collects it
/// on its checkout page. A real provider (Asaas, Pagar.me, Mercado Pago, Iugu, Stripe…) is a new implementation of this
/// interface; the rest of billing does not change.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }

    Task<ProviderCharge> CreateChargeAsync(ChargeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies the webhook's signature and reads it. Returns <c>null</c> when the signature is missing or wrong — such a
    /// request is ignored and never touches an invoice.
    /// </summary>
    PaymentNotification? ReadWebhook(string body, IReadOnlyDictionary<string, string> headers);
}

/// <summary>A provider that can produce a signed webhook for one of its charges, for development and the back office.</summary>
public interface IPaymentSimulator
{
    (string Body, IReadOnlyDictionary<string, string> Headers) SimulatePayment(ProviderCharge charge, long amountCents);
}
