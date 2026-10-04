using FluentValidation;
using Microsoft.Extensions.Logging;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Abstractions.Payments;
using MyHealth.Application.Abstractions.Tenancy;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Billing;
using MyHealth.Domain.Notifications;
using MyHealth.Domain.Tenants;
using MyHealth.Domain.Users;

namespace MyHealth.Application.Billing;

/// <summary>Sets a module's monthly price from a date on (now by default). Earlier invoices keep their prices.</summary>
public sealed record SetModulePriceCommand(BillingModule Module, long MonthlyAmountCents, DateTimeOffset? EffectiveFrom) : ICommand;

internal sealed class SetModulePriceValidator : AbstractValidator<SetModulePriceCommand>
{
    public SetModulePriceValidator() => RuleFor(c => c.Module).IsInEnum();
}

internal sealed class SetModulePriceHandler(IUserContext user, IModulePriceRepository prices, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<SetModulePriceCommand>
{
    public async Task<Result> Handle(SetModulePriceCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var price = ModulePrice.Set(command.Module, command.MonthlyAmountCents, command.EffectiveFrom ?? now, user.UserId, now);
        if (price.IsFailure)
        {
            return price;
        }

        prices.Add(price.Value);
        audit.Record(AuditActions.Update, $"ModulePrice.{command.Module}", price.Value.Id, subjectUserId: null, tenantId: null);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public enum SubscriptionAction
{
    Suspend = 1,
    Reactivate = 2,
    Cancel = 3,
}

/// <summary>An operator suspends, reactivates or cancels a tenant's subscription, always with a reason (audited).</summary>
public sealed record ChangeSubscriptionCommand(Guid TenantId, SubscriptionAction Action, string Reason) : ICommand;

internal sealed class ChangeSubscriptionValidator : AbstractValidator<ChangeSubscriptionCommand>
{
    public ChangeSubscriptionValidator()
    {
        RuleFor(c => c.Action).IsInEnum();
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(1000);
    }
}

internal sealed class ChangeSubscriptionHandler(
    IUserContext user, ITenantSubscriptionRepository subscriptions, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ChangeSubscriptionCommand>
{
    public async Task<Result> Handle(ChangeSubscriptionCommand command, CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByTenantAsync(command.TenantId, cancellationToken);
        if (subscription is null)
        {
            return BillingErrors.SubscriptionNotFound;
        }

        var now = clock.GetUtcNow();
        var result = command.Action switch
        {
            SubscriptionAction.Suspend => subscription.Suspend(user.UserId, command.Reason, now),
            SubscriptionAction.Reactivate => subscription.Reactivate(user.UserId, command.Reason, now),
            _ => subscription.Cancel(user.UserId, command.Reason, now),
        };

        if (result.IsSuccess)
        {
            audit.Record(command.Action.ToString().ToLowerInvariant(), nameof(TenantSubscription), subscription.Id, subjectUserId: null, subscription.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>Changes the modules a tenant pays for; the next invoice reflects it.</summary>
public sealed record ChangeTenantModulesCommand(Guid TenantId, IReadOnlyList<BillingModule> Modules, string Reason) : ICommand;

internal sealed class ChangeTenantModulesValidator : AbstractValidator<ChangeTenantModulesCommand>
{
    public ChangeTenantModulesValidator()
    {
        RuleFor(c => c.Modules).NotNull();
        RuleForEach(c => c.Modules).IsInEnum();
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(1000);
    }
}

internal sealed class ChangeTenantModulesHandler(
    IUserContext user, ITenantSubscriptionRepository subscriptions, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ChangeTenantModulesCommand>
{
    public async Task<Result> Handle(ChangeTenantModulesCommand command, CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByTenantAsync(command.TenantId, cancellationToken);
        if (subscription is null)
        {
            return BillingErrors.SubscriptionNotFound;
        }

        var result = subscription.ChangeModules(command.Modules, user.UserId, command.Reason, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Update, nameof(TenantSubscription), subscription.Id, subjectUserId: null, subscription.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

public sealed record ExtendTrialCommand(Guid TenantId, DateTimeOffset Until, string Reason) : ICommand;

internal sealed class ExtendTrialValidator : AbstractValidator<ExtendTrialCommand>
{
    public ExtendTrialValidator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(1000);
}

internal sealed class ExtendTrialHandler(
    IUserContext user, ITenantSubscriptionRepository subscriptions, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ExtendTrialCommand>
{
    public async Task<Result> Handle(ExtendTrialCommand command, CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByTenantAsync(command.TenantId, cancellationToken);
        if (subscription is null)
        {
            return BillingErrors.SubscriptionNotFound;
        }

        var result = subscription.ExtendTrial(command.Until, user.UserId, command.Reason, clock.GetUtcNow());
        if (result.IsSuccess)
        {
            audit.Record(AuditActions.Update, nameof(TenantSubscription), subscription.Id, subjectUserId: null, subscription.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

/// <summary>An operator issues the next invoice now (e.g. to end a trial early), at the current prices.</summary>
public sealed record IssueInvoiceNowCommand(Guid TenantId) : ICommand<Guid>;

internal sealed class IssueInvoiceNowHandler(
    ITenantSubscriptionRepository subscriptions, IModulePriceRepository prices, IInvoiceRepository invoices, IAuditLog audit, IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<IssueInvoiceNowCommand, Guid>
{
    public async Task<Result<Guid>> Handle(IssueInvoiceNowCommand command, CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByTenantAsync(command.TenantId, cancellationToken);
        if (subscription is null)
        {
            return BillingErrors.SubscriptionNotFound;
        }

        var now = clock.GetUtcNow();
        var invoice = subscription.IssueNextInvoice(await prices.GetCurrentAsync(now, cancellationToken), now, startNow: true);
        if (invoice.IsFailure)
        {
            return invoice.Error;
        }

        invoices.Add(invoice.Value);
        audit.Record(AuditActions.Create, nameof(Invoice), invoice.Value.Id, subjectUserId: null, subscription.TenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return invoice.Value.Id;
    }
}

public enum InvoiceAction
{
    /// <summary>Paid outside the provider (bank transfer, agreement); the note says how.</summary>
    RecordPayment = 1,

    Void = 2,
}

public sealed record ChangeInvoiceCommand(Guid InvoiceId, InvoiceAction Action, string Note) : ICommand;

internal sealed class ChangeInvoiceValidator : AbstractValidator<ChangeInvoiceCommand>
{
    public ChangeInvoiceValidator()
    {
        RuleFor(c => c.Action).IsInEnum();
        RuleFor(c => c.Note).NotEmpty().MaximumLength(1000);
    }
}

internal sealed class ChangeInvoiceHandler(IUserContext user, IInvoiceRepository invoices, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ChangeInvoiceCommand>
{
    public async Task<Result> Handle(ChangeInvoiceCommand command, CancellationToken cancellationToken)
    {
        var invoice = await invoices.GetByIdAsync(command.InvoiceId, cancellationToken);
        if (invoice is null)
        {
            return BillingErrors.InvoiceNotFound;
        }

        var now = clock.GetUtcNow();
        var result = command.Action == InvoiceAction.RecordPayment
            ? invoice.RecordManualPayment(user.UserId, command.Note, now)
            : invoice.Void(user.UserId, command.Note, now);

        if (result.IsSuccess)
        {
            audit.Record(command.Action == InvoiceAction.RecordPayment ? "pay" : "void", nameof(Invoice), invoice.Id, subjectUserId: null, invoice.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

public sealed record BillingCycleResult(int Subscriptions, int InvoicesIssued, int PastDue, int Suspended, int Active, IReadOnlyList<string> Skipped);

/// <summary>
/// The daily billing run: issues the invoices whose period has started, then re-evaluates each subscription (past due,
/// suspended after the grace period, active again when paid). Safe to run more than once: nothing is issued twice
/// because each invoice moves the next period forward.
/// </summary>
public sealed record RunBillingCycleCommand : ICommand<BillingCycleResult>;

internal sealed class RunBillingCycleHandler(
    ITenantSubscriptionRepository subscriptions, IModulePriceRepository prices, IInvoiceRepository invoices, IUnitOfWork unitOfWork, TimeProvider clock,
    ILogger<RunBillingCycleHandler> logger)
    : ICommandHandler<RunBillingCycleCommand, BillingCycleResult>
{
    private const int MaxPeriodsPerRun = 12;

    public async Task<Result<BillingCycleResult>> Handle(RunBillingCycleCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var current = await prices.GetCurrentAsync(now, cancellationToken);
        var all = await subscriptions.ListBillableAsync(cancellationToken);
        var issued = 0;
        var skipped = new List<string>();

        foreach (var subscription in all)
        {
            var open = (await invoices.ListOpenAsync(subscription.Id, cancellationToken)).ToList();

            // A suspended organization is not invoiced for periods it cannot use.
            for (var period = 0; period < MaxPeriodsPerRun && !subscription.BlocksProfessionals && now >= subscription.NextInvoiceAt; period++)
            {
                var invoice = subscription.IssueNextInvoice(current, now);
                if (invoice.IsFailure)
                {
                    skipped.Add($"{subscription.TenantId}: {invoice.Error.Code}");
                    logger.LogWarning("Billing skipped tenant {TenantId}: {ErrorCode}", subscription.TenantId, invoice.Error.Code);
                    break;
                }

                invoices.Add(invoice.Value);
                open.Add(invoice.Value);
                issued++;
            }

            subscription.Reconcile(open, now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new BillingCycleResult(
            all.Count, issued, all.Count(s => s.Status == SubscriptionStatus.PastDue), all.Count(s => s.Status == SubscriptionStatus.Suspended),
            all.Count(s => s.Status == SubscriptionStatus.Active), skipped);
    }
}

/// <summary>
/// The tenant administrator asks to pay an open invoice: a provider charge (Pix code, checkout page) is created, or the
/// still-valid one is returned, so double clicks never create two charges.
/// </summary>
public sealed record CreateInvoiceChargeCommand(Guid InvoiceId) : ICommand<ChargeResponse>;

internal sealed class CreateInvoiceChargeHandler(
    ITenantContext tenant, IUserContext user, IUserProfileRepository users, ITenantRepository tenants, IInvoiceRepository invoices, IPaymentProvider provider,
    IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<CreateInvoiceChargeCommand, ChargeResponse>
{
    public async Task<Result<ChargeResponse>> Handle(CreateInvoiceChargeCommand command, CancellationToken cancellationToken)
    {
        var admin = await BillingMapping.RequireTenantAdministratorAsync(tenant, cancellationToken);
        if (admin.IsFailure)
        {
            return admin.Error;
        }

        var invoice = await invoices.GetByIdAsync(command.InvoiceId, cancellationToken);
        if (invoice is null || invoice.TenantId != admin.Value.TenantId)
        {
            if (invoice is not null)
            {
                await audit.RecordDeniedAsync(nameof(Invoice), invoice.Id, admin.Value.TenantId, cancellationToken);
            }

            return BillingErrors.InvoiceNotFound;
        }

        if (invoice.Status != InvoiceStatus.Open)
        {
            return BillingErrors.InvoiceNotOpen;
        }

        var now = clock.GetUtcNow();
        if (invoice.NeedsNewCharge(now))
        {
            var organization = (await tenants.GetByIdAsync(invoice.TenantId, cancellationToken))!;
            var payer = await users.GetByIdAsync(user.UserId, cancellationToken);
            var charge = await provider.CreateChargeAsync(
                new ChargeRequest(
                    invoice.Id, invoice.TotalCents, invoice.Currency, $"MyHealth {invoice.PeriodStart:MM/yyyy} — {organization.Name}", organization.Name,
                    payer?.Email ?? user.Email, invoice.DueAt),
                cancellationToken);
            invoice.AttachCharge(charge);
            audit.Record(AuditActions.Request, "Invoice.Charge", invoice.Id, subjectUserId: null, invoice.TenantId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var c = invoice.Charge!;
        return new ChargeResponse(c.Provider, c.PixCopyPaste, c.CheckoutUrl, c.ExpiresAt);
    }
}

public sealed record WebhookResult(string Outcome);

/// <summary>
/// A provider webhook (anonymous endpoint). The signature is verified by the provider adapter; each provider event id is
/// processed once (RF-48), and an amount that does not match the invoice is recorded for review instead of being applied.
/// </summary>
public sealed record ProcessPaymentWebhookCommand(string Body, IReadOnlyDictionary<string, string> Headers) : ICommand<WebhookResult>;

internal sealed class ProcessPaymentWebhookHandler(
    IPaymentProvider provider, IPaymentEventRepository events, IInvoiceRepository invoices, IUnitOfWork unitOfWork, TimeProvider clock,
    ILogger<ProcessPaymentWebhookHandler> logger)
    : ICommandHandler<ProcessPaymentWebhookCommand, WebhookResult>
{
    public const string Applied = "applied";
    public const string Duplicate = "duplicate";
    public const string Ignored = "ignored";
    public const string Mismatch = PaymentEvent.MismatchOutcome;

    public async Task<Result<WebhookResult>> Handle(ProcessPaymentWebhookCommand command, CancellationToken cancellationToken)
    {
        var notification = provider.ReadWebhook(command.Body, command.Headers);
        if (notification is null)
        {
            return Error.Forbidden("Payments.InvalidSignature", "The webhook signature is not valid.");
        }

        if (await events.ExistsAsync(provider.Name, notification.EventId, cancellationToken))
        {
            return new WebhookResult(Duplicate);
        }

        var now = clock.GetUtcNow();
        var invoice = await invoices.GetByChargeAsync(provider.Name, notification.ChargeId, cancellationToken);
        var outcome = Ignored;
        if (invoice is not null && notification.Type == PaymentNotification.Paid)
        {
            var confirmed = invoice.ConfirmPayment(notification.AmountCents, notification.ProviderPaymentId, notification.OccurredAt);
            outcome = confirmed.IsSuccess ? Applied : Mismatch;
            if (confirmed.IsFailure)
            {
                logger.LogWarning("Payment for invoice {InvoiceId} not applied: {ErrorCode}", invoice.Id, confirmed.Error.Code);
            }
        }

        events.Add(PaymentEvent.Record(
            provider.Name, notification.EventId, notification.Type, invoice?.Id, outcome, now, notification.AmountCents, notification.ProviderPaymentId));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new WebhookResult(outcome);
    }
}

/// <summary>
/// Development and back-office aid for the fake provider: pays an invoice's charge through the real webhook pipeline
/// (signature, idempotency, reconciliation). Unavailable with a real provider.
/// </summary>
public sealed record SimulatePaymentCommand(Guid InvoiceId) : ICommand<WebhookResult>;

internal sealed class SimulatePaymentHandler(
    IInvoiceRepository invoices, IPaymentProvider provider, IEnumerable<IPaymentSimulator> simulators,
    ICommandHandler<ProcessPaymentWebhookCommand, WebhookResult> webhook, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<SimulatePaymentCommand, WebhookResult>
{
    public async Task<Result<WebhookResult>> Handle(SimulatePaymentCommand command, CancellationToken cancellationToken)
    {
        var simulator = simulators.FirstOrDefault();
        if (simulator is null)
        {
            return Error.Conflict("Payments.SimulationUnavailable", "Payments can only be simulated with the fake provider.");
        }

        var invoice = await invoices.GetByIdAsync(command.InvoiceId, cancellationToken);
        if (invoice is null)
        {
            return BillingErrors.InvoiceNotFound;
        }

        if (invoice.Status != InvoiceStatus.Open)
        {
            return BillingErrors.InvoiceNotOpen;
        }

        if (invoice.NeedsNewCharge(clock.GetUtcNow()))
        {
            invoice.AttachCharge(await provider.CreateChargeAsync(
                new ChargeRequest(invoice.Id, invoice.TotalCents, invoice.Currency, "Simulação", "Simulação", "simulacao@myhealth.local", invoice.DueAt),
                cancellationToken));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var (body, headers) = simulator.SimulatePayment(invoice.Charge!, invoice.TotalCents);
        return await webhook.Handle(new ProcessPaymentWebhookCommand(body, headers), cancellationToken);
    }
}

/// <summary>
/// Keeps billing consistent in the same transaction: a settled invoice re-evaluates the subscription, and tenant
/// administrators are told about new invoices and status changes (pointers only, like every notification).
/// </summary>
internal sealed class BillingEventHandlers(
    ITenantSubscriptionRepository subscriptions, IInvoiceRepository invoices, ITenantMembershipRepository memberships, INotificationRepository notifications,
    TimeProvider clock) :
    IDomainEventHandler<InvoiceSettled>,
    IDomainEventHandler<InvoiceIssued>,
    IDomainEventHandler<SubscriptionStatusChanged>
{
    public async Task Handle(InvoiceSettled e, CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.GetByIdAsync(e.SubscriptionId, cancellationToken);
        if (subscription is not null)
        {
            subscription.Reconcile(await invoices.ListOpenAsync(subscription.Id, cancellationToken), clock.GetUtcNow());
        }
    }

    public Task Handle(InvoiceIssued e, CancellationToken cancellationToken) =>
        e.TotalCents == 0 ? Task.CompletedTask : NotifyAdministratorsAsync(e.TenantId, NotificationType.InvoiceIssued, nameof(Invoice), e.InvoiceId, e.OccurredAt, cancellationToken);

    public Task Handle(SubscriptionStatusChanged e, CancellationToken cancellationToken) =>
        e.Status is SubscriptionStatus.PastDue or SubscriptionStatus.Suspended or SubscriptionStatus.Active or SubscriptionStatus.Cancelled
            ? NotifyAdministratorsAsync(e.TenantId, NotificationType.SubscriptionStatusChanged, nameof(TenantSubscription), e.SubscriptionId, e.OccurredAt, cancellationToken)
            : Task.CompletedTask;

    private async Task NotifyAdministratorsAsync(
        Guid tenantId, NotificationType type, string resourceType, Guid resourceId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        foreach (var administrator in await memberships.ListActiveAdministratorIdsAsync(tenantId, cancellationToken))
        {
            notifications.Add(Notification.ForProfessional(administrator, tenantId, type, resourceType, resourceId, at));
        }
    }
}
