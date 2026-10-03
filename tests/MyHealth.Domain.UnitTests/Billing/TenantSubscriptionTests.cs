using MyHealth.Domain.Billing;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.UnitTests.Billing;

public sealed class TenantSubscriptionTests
{
    private static readonly DateTimeOffset Now = TestData.Now;
    private static readonly Guid Operator = Guid.CreateVersion7();

    private static readonly Dictionary<BillingModule, long> Prices = new()
    {
        [BillingModule.Training] = 9900, [BillingModule.Nutrition] = 4900, [BillingModule.Therapies] = 4900,
    };

    private static TenantSubscription Trial(params BillingModule[] modules) => TenantSubscription.StartTrial(TestData.TenantId, modules, Now);

    /// <summary>A subscription past its trial with one open invoice, issued when the trial ended.</summary>
    private static (TenantSubscription Subscription, Invoice Invoice) Billed(params BillingModule[] modules)
    {
        var subscription = Trial(modules.Length == 0 ? [BillingModule.Training] : modules);
        var invoice = subscription.IssueNextInvoice(Prices, subscription.NextInvoiceAt).Value;
        subscription.ClearDomainEvents();
        return (subscription, invoice);
    }

    [Fact]
    public void A_trial_opens_every_module_but_bills_only_the_chosen_ones()
    {
        var subscription = Trial(BillingModule.Nutrition, BillingModule.Training, BillingModule.Nutrition);

        subscription.Status.ShouldBe(SubscriptionStatus.Trial);
        subscription.Modules.ShouldBe([BillingModule.Training, BillingModule.Nutrition]);
        subscription.TrialEndsAt.ShouldBe(Now.AddDays(30));
        subscription.Allows(Discipline.TherapyPractitioner).ShouldBeTrue();
        Trial().Modules.ShouldBe(BillingModules.All);
    }

    [Fact]
    public void Nothing_is_invoiced_before_the_trial_ends_unless_an_operator_starts_billing_now()
    {
        var subscription = Trial(BillingModule.Training);

        subscription.IssueNextInvoice(Prices, Now.AddDays(1)).Error.ShouldBe(BillingErrors.NothingToInvoice);

        var invoice = subscription.IssueNextInvoice(Prices, Now.AddDays(1), startNow: true).Value;
        invoice.PeriodStart.ShouldBe(Now.AddDays(1));
        invoice.PeriodEnd.ShouldBe(Now.AddDays(1).AddMonths(1));
        subscription.TrialEndsAt.ShouldBe(Now.AddDays(1));
        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        subscription.NextInvoiceAt.ShouldBe(invoice.PeriodEnd);
    }

    [Fact]
    public void An_invoice_snapshots_one_line_per_module_and_is_due_after_the_due_days()
    {
        var (subscription, invoice) = Billed(BillingModule.Training, BillingModule.Therapies);

        invoice.Lines.ShouldBe([new InvoiceLine(BillingModule.Training, 9900), new InvoiceLine(BillingModule.Therapies, 4900)]);
        invoice.TotalCents.ShouldBe(14800);
        invoice.DueAt.ShouldBe(invoice.IssuedAt.AddDays(subscription.DueDays));
        invoice.Status.ShouldBe(InvoiceStatus.Open);

        // A module without a price is never invoiced at zero, and an issued invoice keeps its amounts.
        subscription.IssueNextInvoice(new Dictionary<BillingModule, long> { [BillingModule.Training] = 1 }, invoice.PeriodEnd)
            .Error.ShouldBe(BillingErrors.MissingPrice);
        invoice.TotalCents.ShouldBe(14800);
    }

    [Fact]
    public void After_the_trial_only_subscribed_modules_are_allowed()
    {
        var (subscription, _) = Billed(BillingModule.Training);

        subscription.Allows(Discipline.PersonalTrainer).ShouldBeTrue();
        subscription.Allows(Discipline.Dietitian).ShouldBeFalse();
        subscription.Allows(Discipline.TherapyPractitioner).ShouldBeFalse();
    }

    [Fact]
    public void Non_payment_moves_to_past_due_then_suspended_and_paying_reactivates()
    {
        var (subscription, invoice) = Billed();

        subscription.Reconcile([invoice], invoice.DueAt.AddMinutes(-1));
        subscription.Status.ShouldBe(SubscriptionStatus.Active);

        subscription.Reconcile([invoice], invoice.DueAt.AddDays(1));
        subscription.Status.ShouldBe(SubscriptionStatus.PastDue);
        subscription.Allows(Discipline.PersonalTrainer).ShouldBeTrue();

        subscription.Reconcile([invoice], invoice.DueAt.AddDays(subscription.GraceDays).AddHours(1));
        subscription.Status.ShouldBe(SubscriptionStatus.Suspended);
        subscription.BlocksProfessionals.ShouldBeTrue();
        subscription.Allows(Discipline.PersonalTrainer).ShouldBeFalse();

        invoice.ConfirmPayment(invoice.TotalCents, "pay_1", invoice.DueAt.AddDays(12)).IsSuccess.ShouldBeTrue();
        subscription.Reconcile([], invoice.DueAt.AddDays(12));
        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        subscription.History.Select(h => h.Status).ShouldBe(
            [SubscriptionStatus.Trial, SubscriptionStatus.Active, SubscriptionStatus.PastDue, SubscriptionStatus.Suspended, SubscriptionStatus.Active]);
        subscription.DomainEvents.OfType<SubscriptionStatusChanged>().Count().ShouldBe(3);
    }

    [Fact]
    public void Reconciling_twice_does_not_repeat_the_change()
    {
        var (subscription, invoice) = Billed();

        subscription.Reconcile([invoice], invoice.DueAt.AddDays(1));
        subscription.Reconcile([invoice], invoice.DueAt.AddDays(2));

        subscription.DomainEvents.OfType<SubscriptionStatusChanged>().ShouldHaveSingleItem().Status.ShouldBe(SubscriptionStatus.PastDue);
    }

    [Fact]
    public void An_operator_suspension_survives_payment_until_an_operator_reactivates()
    {
        var (subscription, invoice) = Billed();

        subscription.Suspend(Operator, "  ", Now).Error.ShouldBe(BillingErrors.ReasonRequired);
        subscription.Suspend(Operator, "Fraude em análise", Now).IsSuccess.ShouldBeTrue();
        invoice.ConfirmPayment(invoice.TotalCents, "pay_1", Now).IsSuccess.ShouldBeTrue();
        subscription.Reconcile([], Now.AddDays(40));
        subscription.Status.ShouldBe(SubscriptionStatus.Suspended);

        subscription.Reactivate(Operator, "Análise concluída", Now.AddDays(40)).IsSuccess.ShouldBeTrue();
        subscription.Status.ShouldBe(SubscriptionStatus.Active);
        subscription.SuspendedByOperator.ShouldBeFalse();
        subscription.History[^1].ShouldBe(new SubscriptionChange(SubscriptionStatus.Active, Now.AddDays(40), Operator, "Análise concluída"));
    }

    [Fact]
    public void A_cancelled_subscription_blocks_everyone_and_cannot_be_billed_or_changed()
    {
        var (subscription, _) = Billed();

        subscription.Cancel(Operator, "Encerrou o contrato", Now).IsSuccess.ShouldBeTrue();

        subscription.BlocksProfessionals.ShouldBeTrue();
        subscription.IssueNextInvoice(Prices, Now.AddYears(1)).Error.ShouldBe(BillingErrors.Cancelled);
        subscription.ChangeModules([BillingModule.Nutrition], Operator, "x", Now).Error.ShouldBe(BillingErrors.Cancelled);
        subscription.Reactivate(Operator, "x", Now).Error.ShouldBe(BillingErrors.Cancelled);
    }

    [Fact]
    public void Operators_change_modules_and_extend_trials_with_a_reason()
    {
        var subscription = Trial(BillingModule.Training);

        subscription.ChangeModules([], Operator, "x", Now).Error.ShouldBe(BillingErrors.NoModules);
        subscription.ChangeModules([BillingModule.Nutrition, BillingModule.Training], Operator, "Contratou nutrição", Now).IsSuccess.ShouldBeTrue();
        subscription.Modules.ShouldBe([BillingModule.Training, BillingModule.Nutrition]);

        subscription.ExtendTrial(Now.AddDays(10), Operator, "x", Now).IsSuccess.ShouldBeFalse();
        subscription.ExtendTrial(Now.AddDays(45), Operator, "Negociação", Now).IsSuccess.ShouldBeTrue();
        subscription.TrialEndsAt.ShouldBe(Now.AddDays(45));
        subscription.IssueNextInvoice(Prices, Now.AddDays(31)).Error.ShouldBe(BillingErrors.NothingToInvoice);

        subscription.IssueNextInvoice(Prices, Now.AddDays(45));
        subscription.ExtendTrial(Now.AddDays(90), Operator, "x", Now).Error.ShouldBe(BillingErrors.NotInTrial);
    }
}
