using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyHealth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MotionCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "motion_capture_jobs",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exercise_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    provider_job_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    source_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    source_media_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    duration_seconds = table.Column<int>(type: "integer", nullable: false),
                    consent_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    progress_percent = table.Column<int>(type: "integer", nullable: false),
                    transient_errors = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    outputs = table.Column<string>(type: "jsonb", nullable: false),
                    next_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_motion_capture_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_motion_capture_jobs_exercises_exercise_id",
                        column: x => x.exercise_id,
                        principalSchema: "myhealth",
                        principalTable: "exercises",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_motion_capture_jobs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_motion_capture_jobs_exercise_id_created_at",
                schema: "myhealth",
                table: "motion_capture_jobs",
                columns: new[] { "exercise_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_motion_capture_jobs_status_next_check_at",
                schema: "myhealth",
                table: "motion_capture_jobs",
                columns: new[] { "status", "next_check_at" });

            migrationBuilder.CreateIndex(
                name: "ix_motion_capture_jobs_tenant_id_created_at",
                schema: "myhealth",
                table: "motion_capture_jobs",
                columns: new[] { "tenant_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "motion_capture_jobs",
                schema: "myhealth");
        }
    }
}
