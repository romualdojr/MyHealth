using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MyHealth.Application.Abstractions.Auditing;
using MyHealth.Application.Abstractions.Authentication;
using MyHealth.Application.Abstractions.Data;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Domain.Abstractions;
using MyHealth.Domain.Billing;

namespace MyHealth.Application.Billing;

public sealed record PaymentReviewItem(
    Guid Id, string Provider, string ProviderEventId, string? ProviderPaymentId, Guid? InvoiceId, Guid? TenantId, string? TenantName, long? InvoiceTotalCents,
    string? InvoiceStatus, long? AmountCents, DateTimeOffset ReceivedAt, DateTimeOffset? ReviewedAt, Guid? ReviewedBy, string? ReviewNote);

/// <summary>
/// Payments the provider reported with an amount different from the invoice (back office review queue): they were not
/// applied, so an operator refunds the difference, records the payment by hand or agrees with the customer, then closes it.
/// </summary>
public sealed record ListPaymentReviewsQuery(bool PendingOnly) : IQuery<IReadOnlyList<PaymentReviewItem>>;

internal sealed class ListPaymentReviewsHandler(IReadDbContext db) : IQueryHandler<ListPaymentReviewsQuery, IReadOnlyList<PaymentReviewItem>>
{
    public async Task<Result<IReadOnlyList<PaymentReviewItem>>> Handle(ListPaymentReviewsQuery query, CancellationToken cancellationToken)
    {
        var events = db.PaymentEvents.Where(e => e.Outcome == PaymentEvent.MismatchOutcome);
        if (query.PendingOnly)
        {
            events = events.Where(e => e.ReviewedAt == null);
        }

        var items = await (
                from e in events
                join i in db.Invoices on e.InvoiceId equals i.Id into invoices
                from i in invoices.DefaultIfEmpty()
                join t in db.Tenants on i.TenantId equals t.Id into tenants
                from t in tenants.DefaultIfEmpty()
                orderby e.ReceivedAt descending
                select new PaymentReviewItem(
                    e.Id, e.Provider, e.ProviderEventId, e.ProviderPaymentId, e.InvoiceId, i == null ? null : i.TenantId, t == null ? null : t.Name,
                    i == null ? null : i.TotalCents, i == null ? null : i.Status.ToString(), e.AmountCents, e.ReceivedAt, e.ReviewedAt, e.ReviewedBy, e.ReviewNote))
            .Take(500)
            .ToListAsync(cancellationToken);
        return items;
    }
}

public sealed record ReviewPaymentEventCommand(Guid EventId, string Note) : ICommand;

internal sealed class ReviewPaymentEventValidator : AbstractValidator<ReviewPaymentEventCommand>
{
    public ReviewPaymentEventValidator() => RuleFor(c => c.Note).NotEmpty().MaximumLength(1000);
}

internal sealed class ReviewPaymentEventHandler(
    IPaymentEventRepository events, IReadDbContext db, IUserContext user, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<ReviewPaymentEventCommand>
{
    public async Task<Result> Handle(ReviewPaymentEventCommand command, CancellationToken cancellationToken)
    {
        var paymentEvent = await events.GetByIdAsync(command.EventId, cancellationToken);
        if (paymentEvent is null)
        {
            return BillingErrors.PaymentEventNotFound;
        }

        var result = paymentEvent.Review(user.UserId, command.Note, clock.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        var tenantId = paymentEvent.InvoiceId is { } invoiceId
            ? await db.Invoices.Where(i => i.Id == invoiceId).Select(i => (Guid?)i.TenantId).FirstOrDefaultAsync(cancellationToken)
            : null;
        audit.Record("review", nameof(PaymentEvent), paymentEvent.Id, subjectUserId: null, tenantId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}
