using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Professionals;

namespace MyHealth.Domain.Billing;

/// <summary>What an organization pays for (§2: nutrition and therapies are modules enabled per organization).</summary>
public enum BillingModule
{
    Training = 1,
    Nutrition = 2,
    Therapies = 3,
}

public static class BillingModules
{
    public static readonly IReadOnlyList<BillingModule> All = [BillingModule.Training, BillingModule.Nutrition, BillingModule.Therapies];

    /// <summary>The module a professional needs in the tenant to work in their discipline.</summary>
    public static BillingModule For(Discipline discipline) => discipline switch
    {
        Discipline.PersonalTrainer => BillingModule.Training,
        Discipline.Dietitian => BillingModule.Nutrition,
        Discipline.TherapyPractitioner => BillingModule.Therapies,
        _ => throw new ArgumentOutOfRangeException(nameof(discipline), discipline, null),
    };
}

/// <summary>
/// The monthly price of a module from a date on. Prices are never edited: a new price is a new row, so every invoice can
/// be traced to the price in force when it was issued. Amounts are in cents of BRL.
/// </summary>
public sealed class ModulePrice : AggregateRoot<Guid>
{
    public const string Currency = "BRL";

    private ModulePrice(Guid id) : base(id) { }

    private ModulePrice() { }

    public BillingModule Module { get; private set; }

    public long MonthlyAmountCents { get; private set; }

    public DateTimeOffset EffectiveFrom { get; private set; }

    public Guid? SetBy { get; private set; }

    public DateTimeOffset SetAt { get; private set; }

    public static Result<ModulePrice> Set(BillingModule module, long monthlyAmountCents, DateTimeOffset effectiveFrom, Guid? operatorUserId, DateTimeOffset now)
    {
        if (monthlyAmountCents is < 0 or > 100_000_000)
        {
            return BillingErrors.InvalidAmount;
        }

        if (effectiveFrom < now.AddMinutes(-1))
        {
            return BillingErrors.PriceInThePast;
        }

        return new ModulePrice(Guid.CreateVersion7(now))
        {
            Module = module,
            MonthlyAmountCents = monthlyAmountCents,
            EffectiveFrom = effectiveFrom,
            SetBy = operatorUserId,
            SetAt = now,
        };
    }
}

public interface IModulePriceRepository : IRepository<ModulePrice, Guid>
{
    /// <summary>The price in force at <paramref name="at"/> for each module that has one.</summary>
    Task<IReadOnlyDictionary<BillingModule, long>> GetCurrentAsync(DateTimeOffset at, CancellationToken cancellationToken = default);
}

public static class BillingErrors
{
    public static readonly Error SubscriptionNotFound = Error.NotFound("Billing.SubscriptionNotFound", "The organization has no subscription.");
    public static readonly Error InvoiceNotFound = Error.NotFound("Billing.InvoiceNotFound", "The invoice was not found.");
    public static readonly Error InvalidAmount = Error.Validation("Billing.InvalidAmount", "The amount must be between zero and one million reais.");
    public static readonly Error PriceInThePast = Error.Validation("Billing.PriceInThePast", "A new price cannot take effect in the past; it would change issued invoices' basis.");
    public static readonly Error NoModules = Error.Validation("Billing.NoModules", "Choose at least one module.");
    public static readonly Error MissingPrice = Error.Conflict("Billing.MissingPrice", "A subscribed module has no price yet; set it before issuing invoices.");
    public static readonly Error NotInTrial = Error.Conflict("Billing.NotInTrial", "Only a subscription in trial can have its trial extended.");
    public static readonly Error Cancelled = Error.Conflict("Billing.Cancelled", "The subscription is cancelled.");
    public static readonly Error ReasonRequired = Error.Validation("Billing.ReasonRequired", "Explain the change; it is audited.");
    public static readonly Error InvoiceNotOpen = Error.Conflict("Billing.InvoiceNotOpen", "The invoice is already paid or void.");
    public static readonly Error NothingToInvoice = Error.Conflict("Billing.NothingToInvoice", "The next billing period has not started yet.");
    public static readonly Error AmountMismatch = Error.Conflict("Billing.AmountMismatch", "The amount paid differs from the invoice total.");

    public static readonly Error Suspended = Error.Forbidden(
        "Tenant.Suspended", "This organization's subscription is suspended. Its administrator can settle the open invoices under Plano e cobrança.");

    public static Error ModuleNotSubscribed(BillingModule module) =>
        Error.Forbidden("Tenant.ModuleNotSubscribed", $"This organization does not subscribe to the {module} module.");
}
