using MyHealth.Domain.Billing;

namespace MyHealth.Domain.UnitTests.Billing;

public sealed class InvoiceTests
{
    private static readonly DateTimeOffset Now = TestData.Now;
    private static readonly Guid Operator = Guid.CreateVersion7();

    private static Invoice Open(long trainingCents = 9900)
    {
        var subscription = TenantSubscription.StartTrial(TestData.TenantId, [BillingModule.Training], Now);
        return subscription.IssueNextInvoice(new Dictionary<BillingModule, long> { [BillingModule.Training] = trainingCents }, Now, startNow: true).Value;
    }

    [Fact]
    public void A_payment_for_the_exact_amount_settles_the_invoice_once()
    {
        var invoice = Open();
        invoice.ClearDomainEvents();

        invoice.ConfirmPayment(9900, "pay_1", Now.AddDays(1)).IsSuccess.ShouldBeTrue();
        invoice.ConfirmPayment(9900, "pay_1", Now.AddDays(2)).IsSuccess.ShouldBeTrue();

        (invoice.Status, invoice.PaidAt, invoice.PaymentReference).ShouldBe((InvoiceStatus.Paid, Now.AddDays(1), "pay_1"));
        invoice.DomainEvents.OfType<InvoiceSettled>().ShouldHaveSingleItem();
        invoice.IsOverdue(Now.AddYears(1)).ShouldBeFalse();
    }

    [Fact]
    public void A_different_amount_is_refused()
    {
        var invoice = Open();

        invoice.ConfirmPayment(100, "pay_1", Now).Error.ShouldBe(BillingErrors.AmountMismatch);

        invoice.Status.ShouldBe(InvoiceStatus.Open);
    }

    [Fact]
    public void Operators_record_offline_payments_or_void_open_invoices_with_a_note()
    {
        var paid = Open();
        paid.RecordManualPayment(Operator, " ", Now).Error.ShouldBe(BillingErrors.ReasonRequired);
        paid.RecordManualPayment(Operator, " TED ", Now).IsSuccess.ShouldBeTrue();
        (paid.PaymentReference, paid.RecordedBy).ShouldBe(("manual: TED", Operator));
        paid.Void(Operator, "x", Now).Error.ShouldBe(BillingErrors.InvoiceNotOpen);

        var voided = Open();
        voided.Void(Operator, "Emitida por engano", Now).IsSuccess.ShouldBeTrue();
        voided.Status.ShouldBe(InvoiceStatus.Void);
        voided.ConfirmPayment(voided.TotalCents, "pay_1", Now).Error.ShouldBe(BillingErrors.InvoiceNotOpen);
        voided.IsOverdue(Now.AddYears(1)).ShouldBeFalse();
    }

    [Fact]
    public void A_free_invoice_is_paid_on_issue()
    {
        Open(trainingCents: 0).Status.ShouldBe(InvoiceStatus.Paid);
    }

    [Fact]
    public void A_new_charge_is_needed_only_while_open_and_once_the_last_one_expired()
    {
        var invoice = Open();
        invoice.NeedsNewCharge(Now).ShouldBeTrue();

        invoice.AttachCharge(new ProviderCharge("fake", "ch_1", "pix", null, Now.AddDays(1))).IsSuccess.ShouldBeTrue();

        invoice.NeedsNewCharge(Now.AddHours(1)).ShouldBeFalse();
        invoice.NeedsNewCharge(Now.AddDays(2)).ShouldBeTrue();
    }

    [Fact]
    public void Prices_are_bounded_and_never_take_effect_in_the_past()
    {
        ModulePrice.Set(BillingModule.Training, -1, Now, Operator, Now).Error.ShouldBe(BillingErrors.InvalidAmount);
        ModulePrice.Set(BillingModule.Training, 100, Now.AddDays(-1), Operator, Now).Error.ShouldBe(BillingErrors.PriceInThePast);
        ModulePrice.Set(BillingModule.Training, 12900, Now.AddDays(30), Operator, Now).Value.MonthlyAmountCents.ShouldBe(12900);
    }
}
