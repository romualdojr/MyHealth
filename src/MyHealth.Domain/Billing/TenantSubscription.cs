using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.Billing;

public enum SubscriptionStatus
{
    /// <summary>Free period; every module is available.</summary>
    Trial = 1,

    Active = 2,

    /// <summary>An invoice is past its due date. Still usable: a warning, not a block.</summary>
    PastDue = 3,

    /// <summary>Professionals cannot work in the tenant; its administrators can still see and pay invoices.</summary>
    Suspended = 4,

    Cancelled = 5,
}

/// <summary>One status change, with who made it (null for the automatic billing cycle) and why.</summary>
public sealed record SubscriptionChange(SubscriptionStatus Status, DateTimeOffset At, Guid? By, string Reason);

/// <summary>
/// What a tenant (organization or solo practice) pays MyHealth for: a set of modules billed monthly at their current
/// prices. It starts with a trial that includes every module. Non-payment moves it to past due at the due date and to
/// suspended after the grace period; paying every overdue invoice makes it active again. An operator's manual
/// suspension is only lifted by an operator.
/// </summary>
public sealed class TenantSubscription : AggregateRoot<Guid>
{
    public const int DefaultTrialDays = 30;
    public const int DefaultDueDays = 5;
    public const int DefaultGraceDays = 10;

    private TenantSubscription(Guid id) : base(id) { }

    private TenantSubscription() { }

    public Guid TenantId { get; private set; }

    public List<BillingModule> Modules { get; private set; } = [];

    public SubscriptionStatus Status { get; private set; }

    public DateTimeOffset TrialEndsAt { get; private set; }

    /// <summary>Start of the next period to invoice (invoices are issued at the start of their period).</summary>
    public DateTimeOffset NextInvoiceAt { get; private set; }

    public int DueDays { get; private set; }

    public int GraceDays { get; private set; }

    /// <summary>Set when an operator suspended it; a payment does not lift that.</summary>
    public bool SuspendedByOperator { get; private set; }

    public List<SubscriptionChange> History { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public bool BlocksProfessionals => Status is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled;

    public static TenantSubscription StartTrial(Guid tenantId, IEnumerable<BillingModule> modules, DateTimeOffset now, int trialDays = DefaultTrialDays)
    {
        var chosen = Normalize(modules);
        var subscription = new TenantSubscription(Guid.CreateVersion7(now))
        {
            TenantId = tenantId,
            Modules = chosen.Count == 0 ? [.. BillingModules.All] : chosen,
            Status = SubscriptionStatus.Trial,
            TrialEndsAt = now.AddDays(trialDays),
            NextInvoiceAt = now.AddDays(trialDays),
            DueDays = DefaultDueDays,
            GraceDays = DefaultGraceDays,
            CreatedAt = now,
        };
        subscription.History.Add(new SubscriptionChange(SubscriptionStatus.Trial, now, null, $"Trial of {trialDays} days started."));
        return subscription;
    }

    /// <summary>Whether a professional of <paramref name="discipline"/> may work in the tenant now.</summary>
    public bool Allows(Discipline discipline) => Permits(Status, Modules, discipline);

    /// <summary>The access rule, also used for snapshots of a subscription (e.g. per request).</summary>
    public static bool Permits(SubscriptionStatus status, IReadOnlyCollection<BillingModule> modules, Discipline discipline) =>
        status is not (SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled)
        && (status == SubscriptionStatus.Trial || modules.Contains(BillingModules.For(discipline)));

    public Result ChangeModules(IEnumerable<BillingModule> modules, Guid operatorUserId, string reason, DateTimeOffset now)
    {
        var chosen = Normalize(modules);
        if (chosen.Count == 0)
        {
            return BillingErrors.NoModules;
        }

        if (Status == SubscriptionStatus.Cancelled)
        {
            return BillingErrors.Cancelled;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return BillingErrors.ReasonRequired;
        }

        Modules = chosen;
        History.Add(new SubscriptionChange(Status, now, operatorUserId, $"Modules: {string.Join(", ", chosen)}. {reason.Trim()}"));
        return Result.Success();
    }

    public Result ExtendTrial(DateTimeOffset until, Guid operatorUserId, string reason, DateTimeOffset now)
    {
        if (Status != SubscriptionStatus.Trial)
        {
            return BillingErrors.NotInTrial;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return BillingErrors.ReasonRequired;
        }

        if (until <= TrialEndsAt)
        {
            return Error.Validation("Billing.TrialNotLonger", "The new trial end must be after the current one.");
        }

        TrialEndsAt = until;
        NextInvoiceAt = until;
        History.Add(new SubscriptionChange(Status, now, operatorUserId, $"Trial extended to {until:yyyy-MM-dd}. {reason.Trim()}"));
        return Result.Success();
    }

    public Result Suspend(Guid operatorUserId, string reason, DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Cancelled)
        {
            return BillingErrors.Cancelled;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return BillingErrors.ReasonRequired;
        }

        SuspendedByOperator = true;
        ChangeStatus(SubscriptionStatus.Suspended, now, operatorUserId, reason.Trim());
        return Result.Success();
    }

    /// <summary>An operator lifts a suspension (theirs or for non-payment), e.g. after an agreement with the organization.</summary>
    public Result Reactivate(Guid operatorUserId, string reason, DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Cancelled)
        {
            return BillingErrors.Cancelled;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return BillingErrors.ReasonRequired;
        }

        SuspendedByOperator = false;
        ChangeStatus(now < TrialEndsAt ? SubscriptionStatus.Trial : SubscriptionStatus.Active, now, operatorUserId, reason.Trim());
        return Result.Success();
    }

    /// <summary>Ends the subscription. Records stay with the tenant (MT-08); clients keep access to their own data.</summary>
    public Result Cancel(Guid operatorUserId, string reason, DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Cancelled)
        {
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return BillingErrors.ReasonRequired;
        }

        ChangeStatus(SubscriptionStatus.Cancelled, now, operatorUserId, reason.Trim());
        return Result.Success();
    }

    /// <summary>
    /// Issues the invoice for the period starting at <see cref="NextInvoiceAt"/>, at the prices in force now. With
    /// <paramref name="startNow"/> (an operator ending a trial early, for example) a period that has not started yet starts now.
    /// </summary>
    public Result<Invoice> IssueNextInvoice(IReadOnlyDictionary<BillingModule, long> prices, DateTimeOffset now, bool startNow = false)
    {
        if (Status == SubscriptionStatus.Cancelled)
        {
            return BillingErrors.Cancelled;
        }

        if (Modules.Any(m => !prices.ContainsKey(m)))
        {
            return BillingErrors.MissingPrice;
        }

        if (now < NextInvoiceAt)
        {
            if (!startNow)
            {
                return BillingErrors.NothingToInvoice;
            }

            NextInvoiceAt = now;
            if (TrialEndsAt > now)
            {
                TrialEndsAt = now;
            }
        }

        var periodStart = NextInvoiceAt;
        var periodEnd = periodStart.AddMonths(1);
        var invoice = Invoice.Issue(this, periodStart, periodEnd, Modules.Select(m => new InvoiceLine(m, prices[m])).ToList(), now);
        NextInvoiceAt = periodEnd;

        if (Status == SubscriptionStatus.Trial)
        {
            ChangeStatus(SubscriptionStatus.Active, now, null, "Trial ended; first invoice issued.");
        }

        return invoice;
    }

    /// <summary>
    /// Applies the effect of the open invoices: past due from the first due date passed, suspended after the grace
    /// period, active again once nothing is overdue (unless an operator suspended it).
    /// </summary>
    public void Reconcile(IReadOnlyCollection<Invoice> openInvoices, DateTimeOffset now)
    {
        if (Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Trial)
        {
            return;
        }

        var oldestOverdue = openInvoices.Where(i => i.Status == InvoiceStatus.Open && i.DueAt < now).Select(i => (DateTimeOffset?)i.DueAt).Min();
        if (oldestOverdue is null)
        {
            if (Status is SubscriptionStatus.PastDue || (Status == SubscriptionStatus.Suspended && !SuspendedByOperator))
            {
                ChangeStatus(SubscriptionStatus.Active, now, null, "Every invoice is paid.");
            }

            return;
        }

        if (Status == SubscriptionStatus.Suspended)
        {
            return;
        }

        if (now > oldestOverdue.Value.AddDays(GraceDays))
        {
            ChangeStatus(SubscriptionStatus.Suspended, now, null, $"Invoice overdue for more than {GraceDays} days.");
        }
        else if (Status != SubscriptionStatus.PastDue)
        {
            ChangeStatus(SubscriptionStatus.PastDue, now, null, "An invoice is past its due date.");
        }
    }

    private void ChangeStatus(SubscriptionStatus status, DateTimeOffset now, Guid? by, string reason)
    {
        if (Status == status && History.Count > 0)
        {
            return;
        }

        Status = status;
        History.Add(new SubscriptionChange(status, now, by, reason));
        Raise(new SubscriptionStatusChanged(Id, TenantId, status, now));
    }

    private static List<BillingModule> Normalize(IEnumerable<BillingModule> modules) => modules.Distinct().Order().ToList();
}

public sealed record SubscriptionStatusChanged(Guid SubscriptionId, Guid TenantId, SubscriptionStatus Status, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public interface ITenantSubscriptionRepository : IRepository<TenantSubscription, Guid>
{
    Task<TenantSubscription?> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Subscriptions the billing cycle must look at: not cancelled.</summary>
    Task<IReadOnlyList<TenantSubscription>> ListBillableAsync(CancellationToken cancellationToken = default);
}
