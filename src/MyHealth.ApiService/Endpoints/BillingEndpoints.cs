using MyHealth.ApiService.Infrastructure;
using MyHealth.Application.Abstractions.Messaging;
using MyHealth.Application.Billing;
using MyHealth.Domain.Billing;

namespace MyHealth.ApiService.Endpoints;

/// <summary>
/// Billing: organizations and solo practices pay MyHealth per module. Operators manage it under <c>/admin/billing</c>;
/// a tenant administrator sees and pays the selected tenant's invoices under <c>/tenant/billing</c> (also while
/// suspended); the payment provider confirms payments through the signed webhook.
/// </summary>
internal static class BillingEndpoints
{
    public sealed record PriceRequest(long MonthlyAmountCents, DateTimeOffset? EffectiveFrom);

    public sealed record ModulesRequest(IReadOnlyList<BillingModule> Modules, string Reason);

    public sealed record TrialRequest(DateTimeOffset Until, string Reason);

    public sealed record BillingReasonRequest(string Reason);

    public sealed record InvoiceNoteRequest(string Note);

    public static void MapBillingEndpoints(this IEndpointRouteBuilder authenticated, IEndpointRouteBuilder api)
    {
        var admin = authenticated.MapGroup("/admin/billing").WithTags("Billing (operators)").RequireAuthorization(AuthorizationPolicies.Administrator);

        admin.MapGet("/summary", async (IQueryHandler<GetRevenueSummaryQuery, RevenueSummary> handler, CancellationToken ct) =>
            (await handler.Handle(new GetRevenueSummaryQuery(), ct)).ToHttp());

        admin.MapGet("/module-prices", async (IQueryHandler<ListModulePricesQuery, IReadOnlyList<ModulePriceItem>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListModulePricesQuery(), ct)).ToHttp());

        admin.MapPut("/module-prices/{module}", async (BillingModule module, PriceRequest request, ICommandHandler<SetModulePriceCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new SetModulePriceCommand(module, request.MonthlyAmountCents, request.EffectiveFrom), ct)).ToHttp());

        admin.MapGet("/tenants", async (SubscriptionStatus? status, IQueryHandler<ListTenantBillingQuery, IReadOnlyList<TenantBillingItem>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListTenantBillingQuery(status), ct)).ToHttp());

        admin.MapGet("/tenants/{tenantId:guid}", async (Guid tenantId, IQueryHandler<GetTenantBillingQuery, TenantBillingDetail> handler, CancellationToken ct) =>
            (await handler.Handle(new GetTenantBillingQuery(tenantId), ct)).ToHttp());

        admin.MapPut("/tenants/{tenantId:guid}/modules", async (
                Guid tenantId, ModulesRequest request, ICommandHandler<ChangeTenantModulesCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ChangeTenantModulesCommand(tenantId, request.Modules, request.Reason), ct)).ToHttp());

        admin.MapPost("/tenants/{tenantId:guid}/trial", async (Guid tenantId, TrialRequest request, ICommandHandler<ExtendTrialCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ExtendTrialCommand(tenantId, request.Until, request.Reason), ct)).ToHttp());

        foreach (var action in Enum.GetValues<SubscriptionAction>())
        {
            admin.MapPost($"/tenants/{{tenantId:guid}}/{action.ToString().ToLowerInvariant()}", async (
                    Guid tenantId, BillingReasonRequest request, ICommandHandler<ChangeSubscriptionCommand> handler, CancellationToken ct) =>
                (await handler.Handle(new ChangeSubscriptionCommand(tenantId, action, request.Reason), ct)).ToHttp());
        }

        admin.MapPost("/tenants/{tenantId:guid}/invoices", async (Guid tenantId, ICommandHandler<IssueInvoiceNowCommand, Guid> handler, CancellationToken ct) =>
            (await handler.Handle(new IssueInvoiceNowCommand(tenantId), ct)).ToHttp(id => TypedResults.Created($"/api/v1/admin/billing/tenants/{tenantId}", new { id })));

        admin.MapPost("/invoices/{invoiceId:guid}/record-payment", async (
                Guid invoiceId, InvoiceNoteRequest request, ICommandHandler<ChangeInvoiceCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ChangeInvoiceCommand(invoiceId, InvoiceAction.RecordPayment, request.Note), ct)).ToHttp());

        admin.MapPost("/invoices/{invoiceId:guid}/void", async (
                Guid invoiceId, InvoiceNoteRequest request, ICommandHandler<ChangeInvoiceCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ChangeInvoiceCommand(invoiceId, InvoiceAction.Void, request.Note), ct)).ToHttp());

        admin.MapPost("/invoices/{invoiceId:guid}/simulate-payment", async (
                Guid invoiceId, ICommandHandler<SimulatePaymentCommand, WebhookResult> handler, CancellationToken ct) =>
            (await handler.Handle(new SimulatePaymentCommand(invoiceId), ct)).ToHttp());

        admin.MapGet("/payment-reviews", async (bool? pending, IQueryHandler<ListPaymentReviewsQuery, IReadOnlyList<PaymentReviewItem>> handler, CancellationToken ct) =>
            (await handler.Handle(new ListPaymentReviewsQuery(pending ?? true), ct)).ToHttp());

        admin.MapPost("/payment-reviews/{eventId:guid}/review", async (
                Guid eventId, InvoiceNoteRequest request, ICommandHandler<ReviewPaymentEventCommand> handler, CancellationToken ct) =>
            (await handler.Handle(new ReviewPaymentEventCommand(eventId, request.Note), ct)).ToHttp());

        admin.MapPost("/run-cycle", async (ICommandHandler<RunBillingCycleCommand, BillingCycleResult> handler, CancellationToken ct) =>
            (await handler.Handle(new RunBillingCycleCommand(), ct)).ToHttp());

        var tenant = authenticated.MapGroup("/tenant/billing").WithTags("Billing (organization)");

        tenant.MapGet("/", async (IQueryHandler<GetMyTenantBillingQuery, TenantBillingDetail> handler, CancellationToken ct) =>
            (await handler.Handle(new GetMyTenantBillingQuery(), ct)).ToHttp());

        tenant.MapPost("/invoices/{invoiceId:guid}/charge", async (Guid invoiceId, ICommandHandler<CreateInvoiceChargeCommand, ChargeResponse> handler, CancellationToken ct) =>
            (await handler.Handle(new CreateInvoiceChargeCommand(invoiceId), ct)).ToHttp());

        // The provider calls this; it authenticates with the body signature, not a user token.
        api.MapPost("/payments/webhooks", async (HttpRequest request, ICommandHandler<ProcessPaymentWebhookCommand, WebhookResult> handler, CancellationToken ct) =>
            {
                using var reader = new StreamReader(request.Body);
                var body = await reader.ReadToEndAsync(ct);
                var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                return (await handler.Handle(new ProcessPaymentWebhookCommand(body, headers), ct)).ToHttp();
            })
            .AllowAnonymous()
            .WithTags("Billing (provider)");
    }
}
