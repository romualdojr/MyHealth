using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyHealth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssessmentsNutritionPrivacy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "review_reason",
                schema: "myhealth",
                table: "training_plans",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "review_requested_at",
                schema: "myhealth",
                table: "training_plans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivered_at",
                schema: "myhealth",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "delivery_attempts",
                schema: "myhealth",
                table: "notifications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "delivery_status",
                schema: "myhealth",
                table: "notifications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                // Notifications created before the outbox existed were never meant to be pushed or e-mailed.
                defaultValue: "Skipped");

            migrationBuilder.AddColumn<string>(
                name: "last_delivery_error",
                schema: "myhealth",
                table: "notifications",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_delivery_attempt_at",
                schema: "myhealth",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "food_diary_entries",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dietitian_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    meal_plan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    meal_plan_version_number = table.Column<int>(type: "integer", nullable: true),
                    planned_meal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    meal_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    eaten_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    water_ml = table.Column<int>(type: "integer", nullable: true),
                    adherence = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    client_notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_food_diary_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_food_diary_entries_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "health_histories",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_revision_number = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_histories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "meal_plans",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dietitian_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dietitian_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    current_version_number = table.Column<int>(type: "integer", nullable: false),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    has_unpublished_changes = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    goal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meal_plans", x => x.id);
                    table.ForeignKey(
                        name: "fk_meal_plans_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "nutrition_assessments",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dietitian_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dietitian_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    corrections = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nutrition_assessments", x => x.id);
                    table.ForeignKey(
                        name: "fk_nutrition_assessments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "physical_assessments",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessor_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    measurements = table.Column<string>(type: "jsonb", nullable: false),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    corrections = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_physical_assessments", x => x.id);
                    table.ForeignKey(
                        name: "fk_physical_assessments_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "privacy_requests",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    handled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivery_count = table.Column<int>(type: "integer", nullable: false),
                    last_delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_privacy_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "push_devices",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_push_devices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "retention_policies",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    retention_months = table.Column<int>(type: "integer", nullable: false),
                    legal_basis = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "food_diary_comments",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    food_diary_entry_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_food_diary_comments", x => x.id);
                    table.ForeignKey(
                        name: "fk_food_diary_comments_food_diary_entries_food_diary_entry_id",
                        column: x => x.food_diary_entry_id,
                        principalSchema: "myhealth",
                        principalTable: "food_diary_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "health_history_reviews",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    health_history_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_history_reviews", x => x.id);
                    table.ForeignKey(
                        name: "fk_health_history_reviews_health_histories_health_history_id",
                        column: x => x.health_history_id,
                        principalSchema: "myhealth",
                        principalTable: "health_histories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_health_history_reviews_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "health_history_revisions",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    is_significant_change = table.Column<bool>(type: "boolean", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    health_history_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_history_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_health_history_revisions_health_histories_health_history_id",
                        column: x => x.health_history_id,
                        principalSchema: "myhealth",
                        principalTable: "health_histories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "meal_plan_versions",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    meal_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dietitian_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dietitian_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    change_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    goal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meal_plan_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_meal_plan_versions_meal_plans_meal_plan_id",
                        column: x => x.meal_plan_id,
                        principalSchema: "myhealth",
                        principalTable: "meal_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_meal_plan_versions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_delivery_status_next_delivery_attempt_at",
                schema: "myhealth",
                table: "notifications",
                columns: new[] { "delivery_status", "next_delivery_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_food_diary_comments_food_diary_entry_id",
                schema: "myhealth",
                table: "food_diary_comments",
                column: "food_diary_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_food_diary_entries_client_id_local_date",
                schema: "myhealth",
                table: "food_diary_entries",
                columns: new[] { "client_id", "local_date" });

            migrationBuilder.CreateIndex(
                name: "ix_food_diary_entries_tenant_id_dietitian_profile_id_client_id",
                schema: "myhealth",
                table: "food_diary_entries",
                columns: new[] { "tenant_id", "dietitian_profile_id", "client_id", "local_date" });

            migrationBuilder.CreateIndex(
                name: "ix_health_histories_client_id",
                schema: "myhealth",
                table: "health_histories",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_health_history_reviews_health_history_id",
                schema: "myhealth",
                table: "health_history_reviews",
                column: "health_history_id");

            migrationBuilder.CreateIndex(
                name: "ix_health_history_reviews_tenant_id",
                schema: "myhealth",
                table: "health_history_reviews",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_health_history_revisions_health_history_id_number",
                schema: "myhealth",
                table: "health_history_revisions",
                columns: new[] { "health_history_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_meal_plan_versions_client_id",
                schema: "myhealth",
                table: "meal_plan_versions",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_meal_plan_versions_meal_plan_id_version_number",
                schema: "myhealth",
                table: "meal_plan_versions",
                columns: new[] { "meal_plan_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_meal_plan_versions_tenant_id",
                schema: "myhealth",
                table: "meal_plan_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_meal_plans_client_id_status",
                schema: "myhealth",
                table: "meal_plans",
                columns: new[] { "client_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_meal_plans_tenant_id_dietitian_profile_id_client_id",
                schema: "myhealth",
                table: "meal_plans",
                columns: new[] { "tenant_id", "dietitian_profile_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_nutrition_assessments_client_id_assessed_on",
                schema: "myhealth",
                table: "nutrition_assessments",
                columns: new[] { "client_id", "assessed_on" });

            migrationBuilder.CreateIndex(
                name: "ix_nutrition_assessments_tenant_id_dietitian_profile_id_client",
                schema: "myhealth",
                table: "nutrition_assessments",
                columns: new[] { "tenant_id", "dietitian_profile_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_physical_assessments_client_id_assessed_on",
                schema: "myhealth",
                table: "physical_assessments",
                columns: new[] { "client_id", "assessed_on" });

            migrationBuilder.CreateIndex(
                name: "ix_physical_assessments_tenant_id_client_id",
                schema: "myhealth",
                table: "physical_assessments",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_privacy_requests_status_type",
                schema: "myhealth",
                table: "privacy_requests",
                columns: new[] { "status", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_privacy_requests_user_id_submitted_at",
                schema: "myhealth",
                table: "privacy_requests",
                columns: new[] { "user_id", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_push_devices_installation_id",
                schema: "myhealth",
                table: "push_devices",
                column: "installation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_push_devices_user_id",
                schema: "myhealth",
                table: "push_devices",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_retention_policies_record_type",
                schema: "myhealth",
                table: "retention_policies",
                column: "record_type",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "food_diary_comments",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "health_history_reviews",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "health_history_revisions",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "meal_plan_versions",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "nutrition_assessments",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "physical_assessments",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "privacy_requests",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "push_devices",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "retention_policies",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "food_diary_entries",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "health_histories",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "meal_plans",
                schema: "myhealth");

            migrationBuilder.DropIndex(
                name: "ix_notifications_delivery_status_next_delivery_attempt_at",
                schema: "myhealth",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "review_reason",
                schema: "myhealth",
                table: "training_plans");

            migrationBuilder.DropColumn(
                name: "review_requested_at",
                schema: "myhealth",
                table: "training_plans");

            migrationBuilder.DropColumn(
                name: "delivered_at",
                schema: "myhealth",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "delivery_attempts",
                schema: "myhealth",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "delivery_status",
                schema: "myhealth",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "last_delivery_error",
                schema: "myhealth",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "next_delivery_attempt_at",
                schema: "myhealth",
                table: "notifications");
        }
    }
}
