using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyHealth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "amount_cents",
                schema: "myhealth",
                table: "billing_payment_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_payment_id",
                schema: "myhealth",
                table: "billing_payment_events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_note",
                schema: "myhealth",
                table: "billing_payment_events",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reviewed_at",
                schema: "myhealth",
                table: "billing_payment_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reviewed_by",
                schema: "myhealth",
                table: "billing_payment_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_billing_payment_events_outcome_reviewed_at",
                schema: "myhealth",
                table: "billing_payment_events",
                columns: new[] { "outcome", "reviewed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_billing_payment_events_outcome_reviewed_at",
                schema: "myhealth",
                table: "billing_payment_events");

            migrationBuilder.DropColumn(
                name: "amount_cents",
                schema: "myhealth",
                table: "billing_payment_events");

            migrationBuilder.DropColumn(
                name: "provider_payment_id",
                schema: "myhealth",
                table: "billing_payment_events");

            migrationBuilder.DropColumn(
                name: "review_note",
                schema: "myhealth",
                table: "billing_payment_events");

            migrationBuilder.DropColumn(
                name: "reviewed_at",
                schema: "myhealth",
                table: "billing_payment_events");

            migrationBuilder.DropColumn(
                name: "reviewed_by",
                schema: "myhealth",
                table: "billing_payment_events");
        }
    }
}
