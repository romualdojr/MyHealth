using MyHealth.Domain.Abstractions;

namespace MyHealth.Domain.Billing;

public enum InvoiceStatus
{
    Open = 1,
    Paid = 2,
    Void = 3,
}

/// <summary>One module for the period, at the price in force when the invoice was issued (cents of BRL).</summary>
public sealed record InvoiceLine(BillingModule Module, long AmountCents);

/// <summary>A payment request at the provider: what the payer uses to pay (Pix copy-and-paste code, checkout page).</summary>
public sealed record ProviderCharge(string Provider, string ChargeId, string? PixCopyPaste, string? CheckoutUrl, DateTimeOffset ExpiresAt);

/// <summary>
/// A monthly invoice from MyHealth to a tenant (RF-48). Lines are snapshotted, so later price changes never alter it. It
/// is paid through a provider charge confirmed by webhook, or recorded as paid by an operator (e.g. bank transfer).
/// Card data never reaches MyHealth: the provider collects it.
/// </summary>
public sealed class Invoice : AggregateRoot<Guid>
{
    private Invoice(Guid id) : base(id) { }

    private Invoice() { }

    public Guid TenantId { get; private set; }

    public Guid SubscriptionId { get; private set; }

    public DateTimeOffset PeriodStart { get; private set; }

    public DateTimeOffset PeriodEnd { get; private set; }

    public List<InvoiceLine> Lines { get; private set; } = [];

    public long TotalCents { get; private set; }

    public string Currency { get; private set; } = ModulePrice.Currency;

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset DueAt { get; private set; }

    public InvoiceStatus Status { get; private set; }

    public ProviderCharge? Charge { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>The provider's payment id, or the operator's note for a manual payment.</summary>
    public string? PaymentReference { get; private set; }

    public Guid? RecordedBy { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    public string? VoidReason { get; private set; }

    public bool IsOverdue(DateTimeOffset now) => Status == InvoiceStatus.Open && now > DueAt;

    internal static Invoice Issue(TenantSubscription subscription, DateTimeOffset periodStart, DateTimeOffset periodEnd, List<InvoiceLine> lines, DateTimeOffset now)
    {
        var invoice = new Invoice(Guid.CreateVersion7(now))
        {
            TenantId = subscription.TenantId,
            SubscriptionId = subscription.Id,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            Lines = lines,
            TotalCents = lines.Sum(l => l.AmountCents),
            IssuedAt = now,
            DueAt = now.AddDays(subscription.DueDays),
            Status = InvoiceStatus.Open,
        };

        // A free period (every module priced at zero) needs no payment.
        if (invoice.TotalCents == 0)
        {
            invoice.Status = InvoiceStatus.Paid;
            invoice.PaidAt = now;
            invoice.PaymentReference = "zero-amount";
        }

        invoice.Raise(new InvoiceIssued(invoice.Id, invoice.TenantId, invoice.TotalCents, now));
        return invoice;
    }

    /// <summary>Keeps the current charge while it is valid, so retries do not create duplicate charges at the provider.</summary>
    public bool NeedsNewCharge(DateTimeOffset now) => Status == InvoiceStatus.Open && (Charge is null || Charge.ExpiresAt <= now);

    public Result AttachCharge(ProviderCharge charge)
    {
        if (Status != InvoiceStatus.Open)
        {
            return BillingErrors.InvoiceNotOpen;
        }

        Charge = charge;
        return Result.Success();
    }

    /// <summary>The provider confirmed the payment. Idempotent; a different amount is refused for manual review.</summary>
    public Result ConfirmPayment(long amountCents, string providerPaymentId, DateTimeOffset paidAt)
    {
        if (Status == InvoiceStatus.Paid)
        {
            return Result.Success();
        }

        if (Status != InvoiceStatus.Open)
        {
            return BillingErrors.InvoiceNotOpen;
        }

        if (amountCents != TotalCents)
        {
            return BillingErrors.AmountMismatch;
        }

        MarkPaid(paidAt, providerPaymentId, null);
        return Result.Success();
    }

    /// <summary>An operator records a payment received outside the provider (bank transfer, agreement).</summary>
    public Result RecordManualPayment(Guid operatorUserId, string note, DateTimeOffset now)
    {
        if (Status != InvoiceStatus.Open)
        {
            return BillingErrors.InvoiceNotOpen;
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            return BillingErrors.ReasonRequired;
        }

        MarkPaid(now, "manual: " + note.Trim(), operatorUserId);
        return Result.Success();
    }

    public Result Void(Guid operatorUserId, string reason, DateTimeOffset now)
    {
        if (Status != InvoiceStatus.Open)
        {
            return BillingErrors.InvoiceNotOpen;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return BillingErrors.ReasonRequired;
        }

        Status = InvoiceStatus.Void;
        VoidedAt = now;
        VoidReason = reason.Trim();
        RecordedBy = operatorUserId;
        Raise(new InvoiceSettled(Id, TenantId, SubscriptionId, now));
        return Result.Success();
    }

    private void MarkPaid(DateTimeOffset paidAt, string reference, Guid? by)
    {
        Status = InvoiceStatus.Paid;
        PaidAt = paidAt;
        PaymentReference = reference;
        RecordedBy = by;
        Raise(new InvoiceSettled(Id, TenantId, SubscriptionId, paidAt));
    }
}

public sealed record InvoiceIssued(Guid InvoiceId, Guid TenantId, long TotalCents, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

/// <summary>Paid or voided: the subscription's status is re-evaluated in the same transaction.</summary>
public sealed record InvoiceSettled(Guid InvoiceId, Guid TenantId, Guid SubscriptionId, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public interface IInvoiceRepository : IRepository<Invoice, Guid>
{
    Task<IReadOnlyList<Invoice>> ListOpenAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    Task<Invoice?> GetByChargeAsync(string provider, string chargeId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A webhook notification already processed, keyed by the provider's event id: a provider retrying the same event never
/// records a payment twice (RF-48 "idempotent webhook reconciliation").
/// </summary>
public sealed class PaymentEvent : AggregateRoot<Guid>
{
    /// <summary>The provider reported a payment whose amount differs from the invoice: not applied, an operator reviews it.</summary>
    public const string MismatchOutcome = "amount-mismatch";

    private PaymentEvent(Guid id) : base(id) { }

    private PaymentEvent() { }

    public string Provider { get; private set; } = null!;

    public string ProviderEventId { get; private set; } = null!;

    public string Type { get; private set; } = null!;

    public Guid? InvoiceId { get; private set; }

    /// <summary>What happened: applied, ignored (unknown charge or type), mismatch (amount differs, needs review).</summary>
    public string Outcome { get; private set; } = null!;

    /// <summary>The amount the provider reported, in cents (null for events without an amount).</summary>
    public long? AmountCents { get; private set; }

    public string? ProviderPaymentId { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public Guid? ReviewedBy { get; private set; }

    /// <summary>What the operator did about it (refund at the provider, payment recorded by hand, agreement with the customer).</summary>
    public string? ReviewNote { get; private set; }

    public bool NeedsReview => Outcome == MismatchOutcome && ReviewedAt is null;

    public static PaymentEvent Record(
        string provider, string providerEventId, string type, Guid? invoiceId, string outcome, DateTimeOffset now, long? amountCents = null,
        string? providerPaymentId = null) =>
        new(Guid.CreateVersion7(now))
        {
            Provider = provider,
            ProviderEventId = providerEventId,
            Type = type,
            InvoiceId = invoiceId,
            Outcome = outcome,
            AmountCents = amountCents,
            ProviderPaymentId = providerPaymentId,
            ReceivedAt = now,
        };

    /// <summary>An operator closes a mismatch after handling it outside the webhook (the note says how).</summary>
    public Result Review(Guid operatorUserId, string note, DateTimeOffset now)
    {
        if (!NeedsReview)
        {
            return BillingErrors.PaymentEventNotPendingReview;
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            return BillingErrors.ReasonRequired;
        }

        ReviewedAt = now;
        ReviewedBy = operatorUserId;
        ReviewNote = note.Trim();
        return Result.Success();
    }
}

public interface IPaymentEventRepository : IRepository<PaymentEvent, Guid>
{
    Task<bool> ExistsAsync(string provider, string providerEventId, CancellationToken cancellationToken = default);
}
