using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyHealth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Billing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_module_prices",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    monthly_amount_cents = table.Column<long>(type: "bigint", nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    set_by = table.Column<Guid>(type: "uuid", nullable: true),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_module_prices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "billing_payment_events",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_event_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_payment_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "billing_subscriptions",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modules = table.Column<int[]>(type: "integer[]", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    trial_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_invoice_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_days = table.Column<int>(type: "integer", nullable: false),
                    grace_days = table.Column<int>(type: "integer", nullable: false),
                    suspended_by_operator = table.Column<bool>(type: "boolean", nullable: false),
                    history = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_billing_subscriptions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "billing_invoices",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: false),
                    total_cents = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    charge_provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    charge_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    charge_pix_copy_paste = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    charge_checkout_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    charge_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    payment_reference = table.Column<string>(type: "character varying(1100)", maxLength: 1100, nullable: true),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_invoices", x => x.id);
                    table.ForeignKey(
                        name: "fk_billing_invoices_tenant_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "myhealth",
                        principalTable: "billing_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_billing_invoices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_billing_invoices_charge_provider_charge_id",
                schema: "myhealth",
                table: "billing_invoices",
                columns: new[] { "charge_provider", "charge_id" });

            migrationBuilder.CreateIndex(
                name: "ix_billing_invoices_subscription_id_status",
                schema: "myhealth",
                table: "billing_invoices",
                columns: new[] { "subscription_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_billing_invoices_tenant_id_period_start",
                schema: "myhealth",
                table: "billing_invoices",
                columns: new[] { "tenant_id", "period_start" });

            migrationBuilder.CreateIndex(
                name: "ix_billing_module_prices_module_effective_from",
                schema: "myhealth",
                table: "billing_module_prices",
                columns: new[] { "module", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ix_billing_payment_events_provider_provider_event_id",
                schema: "myhealth",
                table: "billing_payment_events",
                columns: new[] { "provider", "provider_event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_billing_subscriptions_status_next_invoice_at",
                schema: "myhealth",
                table: "billing_subscriptions",
                columns: new[] { "status", "next_invoice_at" });

            migrationBuilder.CreateIndex(
                name: "ix_billing_subscriptions_tenant_id",
                schema: "myhealth",
                table: "billing_subscriptions",
                column: "tenant_id",
                unique: true);

            // Tenants created before billing get the same 30-day trial with every module a new tenant gets, so nobody is
            // blocked by the upgrade (a tenant without a subscription is treated as suspended).
            migrationBuilder.Sql("""
                INSERT INTO myhealth.billing_subscriptions
                    (id, tenant_id, modules, status, trial_ends_at, next_invoice_at, due_days, grace_days, suspended_by_operator, history, created_at)
                SELECT gen_random_uuid(), t.id, ARRAY[1, 2, 3], 'Trial', now() + interval '30 days', now() + interval '30 days', 5, 10, false,
                       jsonb_build_array(jsonb_build_object(
                           'status', 'Trial', 'at', now(), 'by', null, 'reason', 'Trial of 30 days started when billing was introduced.')),
                       now()
                FROM myhealth.tenants t
                WHERE NOT EXISTS (SELECT 1 FROM myhealth.billing_subscriptions s WHERE s.tenant_id = t.id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_invoices",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "billing_module_prices",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "billing_payment_events",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "billing_subscriptions",
                schema: "myhealth");
        }
    }
}
