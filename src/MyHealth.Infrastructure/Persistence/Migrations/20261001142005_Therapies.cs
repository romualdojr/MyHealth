using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyHealth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Therapies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "therapy_modalities",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    accepted_councils = table.Column<List<string>>(type: "text[]", nullable: false),
                    required_record_fields = table.Column<int[]>(type: "integer[]", nullable: false),
                    requires_consent = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    legal_review_reference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    enabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    enabled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_therapy_modalities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "practitioner_qualifications",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    professional_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modality_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    issuer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    certificate_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    review_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_practitioner_qualifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_practitioner_qualifications_therapy_modalities_modality_id",
                        column: x => x.modality_id,
                        principalSchema: "myhealth",
                        principalTable: "therapy_modalities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "therapy_care_plans",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    practitioner_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    practitioner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modality_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goals = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    planned_sessions = table.Column<int>(type: "integer", nullable: true),
                    frequency = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    next_review_on = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reviews = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_therapy_care_plans", x => x.id);
                    table.ForeignKey(
                        name: "fk_therapy_care_plans_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_therapy_care_plans_therapy_modalities_modality_id",
                        column: x => x.modality_id,
                        principalSchema: "myhealth",
                        principalTable: "therapy_modalities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "therapy_consent_templates",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    modality_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    disclosed_risks = table.Column<List<string>>(type: "text[]", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_therapy_consent_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_therapy_consent_templates_therapy_modalities_modality_id",
                        column: x => x.modality_id,
                        principalSchema: "myhealth",
                        principalTable: "therapy_modalities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "therapy_services",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modality_id = table.Column<Guid>(type: "uuid", nullable: false),
                    practitioner_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    practitioner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    delivery = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_therapy_services", x => x.id);
                    table.ForeignKey(
                        name: "fk_therapy_services_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_therapy_services_therapy_modalities_modality_id",
                        column: x => x.modality_id,
                        principalSchema: "myhealth",
                        principalTable: "therapy_modalities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "therapy_consents",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modality_id = table.Column<Guid>(type: "uuid", nullable: false),
                    practitioner_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    practitioner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_version = table.Column<int>(type: "integer", nullable: false),
                    disclosed_risks = table.Column<List<string>>(type: "text[]", nullable: false),
                    decision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdrawal_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_therapy_consents", x => x.id);
                    table.ForeignKey(
                        name: "fk_therapy_consents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_therapy_consents_therapy_consent_templates_template_id",
                        column: x => x.template_id,
                        principalSchema: "myhealth",
                        principalTable: "therapy_consent_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "therapy_sessions",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    practitioner_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    practitioner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modality_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    delivery = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    record = table.Column<string>(type: "jsonb", nullable: true),
                    record_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    corrections = table.Column<string>(type: "jsonb", nullable: false),
                    client_report = table.Column<string>(type: "jsonb", nullable: true),
                    referral = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_therapy_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_therapy_sessions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_therapy_sessions_therapy_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "myhealth",
                        principalTable: "therapy_services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_practitioner_qualifications_modality_id",
                schema: "myhealth",
                table: "practitioner_qualifications",
                column: "modality_id");

            migrationBuilder.CreateIndex(
                name: "ix_practitioner_qualifications_professional_profile_id_modalit",
                schema: "myhealth",
                table: "practitioner_qualifications",
                columns: new[] { "professional_profile_id", "modality_id", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_practitioner_qualifications_status",
                schema: "myhealth",
                table: "practitioner_qualifications",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_therapy_care_plans_client_id",
                schema: "myhealth",
                table: "therapy_care_plans",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_therapy_care_plans_modality_id",
                schema: "myhealth",
                table: "therapy_care_plans",
                column: "modality_id");

            migrationBuilder.CreateIndex(
                name: "ix_therapy_care_plans_tenant_id_practitioner_profile_id_client",
                schema: "myhealth",
                table: "therapy_care_plans",
                columns: new[] { "tenant_id", "practitioner_profile_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_therapy_consent_templates_modality_id_version",
                schema: "myhealth",
                table: "therapy_consent_templates",
                columns: new[] { "modality_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_therapy_consents_client_id_tenant_id_practitioner_profile_i",
                schema: "myhealth",
                table: "therapy_consents",
                columns: new[] { "client_id", "tenant_id", "practitioner_profile_id", "modality_id", "decided_at" });

            migrationBuilder.CreateIndex(
                name: "ix_therapy_consents_template_id",
                schema: "myhealth",
                table: "therapy_consents",
                column: "template_id");

            migrationBuilder.CreateIndex(
                name: "ix_therapy_consents_tenant_id",
                schema: "myhealth",
                table: "therapy_consents",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_therapy_modalities_code",
                schema: "myhealth",
                table: "therapy_modalities",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_therapy_services_modality_id",
                schema: "myhealth",
                table: "therapy_services",
                column: "modality_id");

            migrationBuilder.CreateIndex(
                name: "ix_therapy_services_tenant_id_practitioner_profile_id",
                schema: "myhealth",
                table: "therapy_services",
                columns: new[] { "tenant_id", "practitioner_profile_id" });

            migrationBuilder.CreateIndex(
                name: "ix_therapy_sessions_client_id",
                schema: "myhealth",
                table: "therapy_sessions",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_therapy_sessions_practitioner_profile_id_status_starts_at",
                schema: "myhealth",
                table: "therapy_sessions",
                columns: new[] { "practitioner_profile_id", "status", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "ix_therapy_sessions_service_id",
                schema: "myhealth",
                table: "therapy_sessions",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_therapy_sessions_tenant_id_client_id",
                schema: "myhealth",
                table: "therapy_sessions",
                columns: new[] { "tenant_id", "client_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "practitioner_qualifications",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "therapy_care_plans",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "therapy_consents",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "therapy_sessions",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "therapy_consent_templates",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "therapy_services",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "therapy_modalities",
                schema: "myhealth");
        }
    }
}
