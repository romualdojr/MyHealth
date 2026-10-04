using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyHealth.Api.IntegrationTests.Infrastructure;
using MyHealth.Application.Billing;
using Microsoft.Extensions.DependencyInjection;
using MyHealth.Application.Users;
using MyHealth.Domain.Billing;
using MyHealth.Domain.Professionals;
using MyHealth.Domain.Tenants;

namespace MyHealth.Api.IntegrationTests;

/// <summary>Organizations and solo practices pay MyHealth per module (RF-47/48 for the platform's own billing).</summary>
public sealed class BillingTests(ApiFactory factory) : IntegrationTest(factory)
{
    private const string WebhookSecret = "myhealth-fake-webhook-secret";

    private TestUser Operator() => Scenario.Factory.NewUser("operator", administrator: true);

    private async Task<TenantBillingDetail> TenantBillingAsync(Guid tenantId) =>
        await Scenario.Read<TenantBillingDetail>(await Operator().Http.GetAsync($"/api/v1/admin/billing/tenants/{tenantId}", Ct));

    /// <summary>A clinic whose owner is a trainer and which also has a dietitian member.</summary>
    private async Task<(TestUser Owner, TestUser Dietitian, Guid TenantId)> ClinicAsync()
    {
        var (owner, _) = await Scenario.ProfessionalAsync("owner", ownTenant: false);
        var tenantId = await Scenario.CreateTenantAsync(owner, "Clínica Movimento", TenantKind.Organization, Discipline.PersonalTrainer);
        Scenario.UseTenant(owner, tenantId);
        var (dietitian, _) = await Scenario.ProfessionalAsync("dietitian", Discipline.Dietitian, ownTenant: false);
        await Scenario.AddMemberAsync(owner, dietitian, Discipline.Dietitian);
        Scenario.UseTenant(dietitian, tenantId);
        return (owner, dietitian, tenantId);
    }

    private static string Sign(string body) => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes(body)));

    private async Task<HttpResponseMessage> WebhookAsync(string body, string? signature)
    {
        using var anonymous = Scenario.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/webhooks") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (signature is not null)
        {
            request.Headers.Add("X-Fake-Signature", signature);
        }

        return await anonymous.SendAsync(request, Ct);
    }

    [Fact]
    public async Task A_new_organization_starts_a_trial_with_every_module_and_its_disciplines_as_paid_modules()
    {
        var (owner, dietitian, tenantId) = await ClinicAsync();

        var billing = await TenantBillingAsync(tenantId);
        billing.Summary.Status.ShouldBe(SubscriptionStatus.Trial);
        billing.Summary.Modules.ShouldBe([BillingModule.Training]);
        billing.Summary.TrialEndsAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(29));
        billing.AdministratorEmails.ShouldBe([owner.Email]);

        // During the trial every module is open, so the dietitian can work although the clinic will only pay for training.
        (await dietitian.Http.GetAsync("/api/v1/meal-plans", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await Scenario.Read<MeResponse>(await owner.Http.GetAsync("/api/v1/me", Ct));
        me.Memberships.Single(m => m.TenantId == tenantId).SubscriptionStatus.ShouldBe(SubscriptionStatus.Trial);
    }

    [Fact]
    public async Task After_the_trial_only_paid_modules_work_and_the_invoice_has_one_line_per_module_at_the_current_price()
    {
        var (owner, dietitian, tenantId) = await ClinicAsync();
        var admin = Operator();

        await Scenario.EnsureSuccess(await admin.Http.PutAsJsonAsync($"/api/v1/admin/billing/tenants/{tenantId}/modules", new
        {
            modules = new[] { "Training", "Nutrition" }, reason = "Contratou nutrição",
        }, Ct));
        var invoiceId = (await Scenario.Read<Scenario.IdResponse>(await admin.Http.PostAsync($"/api/v1/admin/billing/tenants/{tenantId}/invoices", null, Ct))).Id;

        var billing = await TenantBillingAsync(tenantId);
        billing.Summary.Status.ShouldBe(SubscriptionStatus.Active);
        var invoice = billing.Invoices.ShouldHaveSingleItem();
        invoice.Id.ShouldBe(invoiceId);
        invoice.Lines.Select(l => l.Module).ShouldBe([BillingModule.Training, BillingModule.Nutrition]);
        var prices = await Scenario.Read<List<ModulePriceItem>>(await admin.Http.GetAsync("/api/v1/admin/billing/module-prices", Ct));
        invoice.TotalCents.ShouldBe(prices.Where(p => p.Module is BillingModule.Training or BillingModule.Nutrition).Sum(p => p.MonthlyAmountCents!.Value));
        (invoice.Status, invoice.IsOverdue).ShouldBe((InvoiceStatus.Open, false));

        // Nutrition is paid for, so the dietitian keeps working; without it they would not.
        (await dietitian.Http.GetAsync("/api/v1/meal-plans", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Scenario.EnsureSuccess(await admin.Http.PutAsJsonAsync($"/api/v1/admin/billing/tenants/{tenantId}/modules", new
        {
            modules = new[] { "Training" }, reason = "Cancelou nutrição",
        }, Ct));
        var blocked = await dietitian.Http.GetAsync("/api/v1/meal-plans", Ct);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await blocked.Content.ReadAsStringAsync(Ct)).ShouldContain("Tenant.ModuleNotSubscribed");
        (await owner.Http.GetAsync("/api/v1/trainer/dashboard", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_organization_administrator_pays_through_one_charge_and_the_webhook_settles_the_invoice_once()
    {
        var (owner, dietitian, tenantId) = await ClinicAsync();
        var invoiceId = (await Scenario.Read<Scenario.IdResponse>(await Operator().Http.PostAsync($"/api/v1/admin/billing/tenants/{tenantId}/invoices", null, Ct))).Id;

        // Only administrators of the organization see and pay its invoices.
        (await dietitian.Http.GetAsync("/api/v1/tenant/billing", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var mine = await Scenario.Read<TenantBillingDetail>(await owner.Http.GetAsync("/api/v1/tenant/billing", Ct));
        mine.Invoices.ShouldHaveSingleItem().Id.ShouldBe(invoiceId);

        var charge = await Scenario.Read<ChargeResponse>(await owner.Http.PostAsync($"/api/v1/tenant/billing/invoices/{invoiceId}/charge", null, Ct));
        charge.PixCopyPaste.ShouldNotBeNullOrEmpty();
        (await Scenario.Read<ChargeResponse>(await owner.Http.PostAsync($"/api/v1/tenant/billing/invoices/{invoiceId}/charge", null, Ct)))
            .PixCopyPaste.ShouldBe(charge.PixCopyPaste);

        // Another organization's administrator cannot use the invoice id.
        var (otherOwner, _) = await Scenario.ProfessionalAsync("other-owner");
        (await otherOwner.Http.PostAsync($"/api/v1/tenant/billing/invoices/{invoiceId}/charge", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The provider confirms the payment; a retry of the same event changes nothing.
        var total = mine.Invoices[0].TotalCents;
        var body = JsonSerializer.Serialize(new
        {
            eventId = $"evt_{Guid.NewGuid():N}", type = "charge.paid", chargeId = await InvoiceChargeIdAsync(tenantId, invoiceId), amountCents = total,
            paymentId = "pay_123", occurredAt = DateTimeOffset.UtcNow,
        });
        (await WebhookAsync(body, "bad-signature")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await WebhookAsync(body, null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Scenario.Read<WebhookResult>(await WebhookAsync(body, Sign(body)))).Outcome.ShouldBe("applied");
        (await Scenario.Read<WebhookResult>(await WebhookAsync(body, Sign(body)))).Outcome.ShouldBe("duplicate");

        var paid = (await TenantBillingAsync(tenantId)).Invoices.ShouldHaveSingleItem();
        (paid.Status, paid.PaymentReference).ShouldBe((InvoiceStatus.Paid, "pay_123"));
        (await owner.Http.PostAsync($"/api/v1/tenant/billing/invoices/{invoiceId}/charge", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_payment_with_a_different_amount_is_recorded_for_review_and_not_applied()
    {
        var (owner, _, tenantId) = await ClinicAsync();
        var invoiceId = (await Scenario.Read<Scenario.IdResponse>(await Operator().Http.PostAsync($"/api/v1/admin/billing/tenants/{tenantId}/invoices", null, Ct))).Id;
        await Scenario.EnsureSuccess(await owner.Http.PostAsync($"/api/v1/tenant/billing/invoices/{invoiceId}/charge", null, Ct));

        var body = JsonSerializer.Serialize(new
        {
            eventId = $"evt_{Guid.NewGuid():N}", type = "charge.paid", chargeId = await InvoiceChargeIdAsync(tenantId, invoiceId), amountCents = 1,
            paymentId = "pay_partial", occurredAt = DateTimeOffset.UtcNow,
        });

        (await Scenario.Read<WebhookResult>(await WebhookAsync(body, Sign(body)))).Outcome.ShouldBe("amount-mismatch");
        (await TenantBillingAsync(tenantId)).Invoices.ShouldHaveSingleItem().Status.ShouldBe(InvoiceStatus.Open);

        // It waits in the operators' review queue with both amounts, until an operator says how it was handled.
        var admin = Operator();
        var queue = await Scenario.Read<List<PaymentReviewItem>>(await admin.Http.GetAsync("/api/v1/admin/billing/payment-reviews", Ct));
        var review = queue.Single(r => r.InvoiceId == invoiceId);
        (review.AmountCents, review.ProviderPaymentId, review.TenantId, review.InvoiceStatus, review.ReviewedAt).ShouldBe((1L, "pay_partial", tenantId, "Open", null));
        review.InvoiceTotalCents.ShouldNotBeNull().ShouldBeGreaterThan(1);

        (await admin.Http.PostAsJsonAsync($"/api/v1/admin/billing/payment-reviews/{review.Id}/review", new { note = " " }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.Http.PostAsJsonAsync($"/api/v1/admin/billing/payment-reviews/{review.Id}/review", new { note = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync($"/api/v1/admin/billing/payment-reviews/{review.Id}/review", new { note = "Valor estornado no provedor." }, Ct));
        (await admin.Http.PostAsJsonAsync($"/api/v1/admin/billing/payment-reviews/{review.Id}/review", new { note = "De novo" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await Scenario.Read<List<PaymentReviewItem>>(await admin.Http.GetAsync("/api/v1/admin/billing/payment-reviews", Ct))).ShouldNotContain(r => r.Id == review.Id);
        var all = await Scenario.Read<List<PaymentReviewItem>>(await admin.Http.GetAsync("/api/v1/admin/billing/payment-reviews?pending=false", Ct));
        all.Single(r => r.Id == review.Id).ReviewNote.ShouldBe("Valor estornado no provedor.");

        // The billing audit shows the review, apart from clinical access.
        var audit = await Scenario.Read<MyHealth.Application.Common.PagedResponse<MyHealth.Application.Auditing.AuditEntryResponse>>(
            await admin.Http.GetAsync($"/api/v1/admin/audit-entries?area=billing&tenantId={tenantId}", Ct));
        audit.Items.ShouldContain(e => e.Action == "review" && e.ResourceType == "PaymentEvent" && e.ResourceId == review.Id);
        audit.Items.ShouldAllBe(e => e.ResourceType == "TenantSubscription" || e.ResourceType == "Invoice" || e.ResourceType == "Invoice.Charge" || e.ResourceType == "PaymentEvent");
    }

    [Fact]
    public async Task A_suspended_organization_blocks_its_professionals_but_its_administrator_can_still_pay_and_clients_keep_their_data()
    {
        var (owner, dietitian, tenantId) = await ClinicAsync();
        var client = await Scenario.ClientAsync();
        await Scenario.LinkAsync(owner, client);
        var plan = await Scenario.DraftPlanAsync(owner, client.Id);
        await Scenario.PublishAsync(owner, plan);
        var admin = Operator();
        var invoiceId = (await Scenario.Read<Scenario.IdResponse>(await admin.Http.PostAsync($"/api/v1/admin/billing/tenants/{tenantId}/invoices", null, Ct))).Id;

        (await admin.Http.PostAsJsonAsync($"/api/v1/admin/billing/tenants/{tenantId}/suspend", new { reason = " " }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync($"/api/v1/admin/billing/tenants/{tenantId}/suspend", new { reason = "Inadimplência" }, Ct));

        var blocked = await owner.Http.GetAsync("/api/v1/trainer/dashboard", Ct);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await blocked.Content.ReadAsStringAsync(Ct)).ShouldContain("Tenant.Suspended");
        (await dietitian.Http.GetAsync("/api/v1/meal-plans", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Http.GetAsync($"/api/v1/training-plans/{plan}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Administration and billing stay open; the client still sees their plan.
        (await owner.Http.GetAsync("/api/v1/tenant/memberships", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Scenario.Read<TenantBillingDetail>(await owner.Http.GetAsync("/api/v1/tenant/billing", Ct))).Summary.Status.ShouldBe(SubscriptionStatus.Suspended);
        (await client.Http.GetAsync($"/api/v1/me/training-plans/{plan}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Paying does not lift an operator's suspension; the operator does.
        (await Scenario.Read<WebhookResult>(await admin.Http.PostAsync($"/api/v1/admin/billing/invoices/{invoiceId}/simulate-payment", null, Ct))).Outcome.ShouldBe("applied");
        (await TenantBillingAsync(tenantId)).Summary.Status.ShouldBe(SubscriptionStatus.Suspended);
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync($"/api/v1/admin/billing/tenants/{tenantId}/reactivate", new { reason = "Acordo firmado" }, Ct));
        (await owner.Http.GetAsync("/api/v1/trainer/dashboard", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var history = (await TenantBillingAsync(tenantId)).History;
        history.ShouldContain(h => h.Status == SubscriptionStatus.Suspended && h.Reason == "Inadimplência");
        history.ShouldContain(h => h.Status == SubscriptionStatus.Active && h.Reason == "Acordo firmado");
    }

    [Fact]
    public async Task Operators_set_prices_record_offline_payments_void_invoices_and_see_the_revenue_summary()
    {
        var (owner, _, tenantId) = await ClinicAsync();
        var admin = Operator();

        (await admin.Http.PutAsJsonAsync("/api/v1/admin/billing/module-prices/Training", new { monthlyAmountCents = -1 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.Http.PutAsJsonAsync("/api/v1/admin/billing/module-prices/Training", new { monthlyAmountCents = 100, effectiveFrom = DateTimeOffset.UtcNow.AddDays(-2) }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var first = (await Scenario.Read<Scenario.IdResponse>(await admin.Http.PostAsync($"/api/v1/admin/billing/tenants/{tenantId}/invoices", null, Ct))).Id;
        (await admin.Http.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{first}/record-payment", new { note = "" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{first}/record-payment", new { note = "TED recebida em 01/10" }, Ct));
        var second = (await Scenario.Read<Scenario.IdResponse>(await admin.Http.PostAsync($"/api/v1/admin/billing/tenants/{tenantId}/invoices", null, Ct))).Id;
        await Scenario.EnsureSuccess(await admin.Http.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{second}/void", new { note = "Emitida por engano" }, Ct));
        (await admin.Http.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{second}/record-payment", new { note = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var invoices = (await TenantBillingAsync(tenantId)).Invoices;
        invoices.Single(i => i.Id == first).PaymentReference.ShouldBe("manual: TED recebida em 01/10");
        invoices.Single(i => i.Id == second).ShouldSatisfyAllConditions(i => i.Status.ShouldBe(InvoiceStatus.Void), i => i.VoidReason.ShouldBe("Emitida por engano"));

        var summary = await Scenario.Read<RevenueSummary>(await admin.Http.GetAsync("/api/v1/admin/billing/summary", Ct));
        summary.MonthlyRecurringRevenueCents.ShouldBeGreaterThan(0);
        summary.RecentPayments.ShouldContain(p => p.InvoiceId == first);
        (await Scenario.Read<List<TenantBillingItem>>(await admin.Http.GetAsync("/api/v1/admin/billing/tenants?status=Active", Ct)))
            .ShouldContain(t => t.TenantId == tenantId);

        // The cycle is safe to run at any time: nothing is due yet, so nothing new is issued for this tenant.
        await Scenario.EnsureSuccess(await admin.Http.PostAsync("/api/v1/admin/billing/run-cycle", null, Ct));
        (await TenantBillingAsync(tenantId)).Invoices.Count.ShouldBe(2);

        // None of this is reachable by organization administrators.
        (await owner.Http.GetAsync("/api/v1/admin/billing/summary", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<string?> InvoiceChargeIdAsync(Guid tenantId, Guid invoiceId)
    {
        await using var scope = Scenario.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyHealth.Infrastructure.Persistence.ApplicationDbContext>();
        var invoice = await db.Invoices.FindAsync([invoiceId], Ct);
        return invoice?.TenantId == tenantId ? invoice.Charge?.ChargeId : null;
    }
}
