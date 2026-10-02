using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyHealth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClientExperience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Exercise media moves to a list (loop, full video, angles, chapters). The single URL was never filled in.
            migrationBuilder.DropColumn(
                name: "media_url",
                schema: "myhealth",
                table: "exercises");

            migrationBuilder.AddColumn<string>(
                name: "body_checks",
                schema: "myhealth",
                table: "workout_sessions",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "media",
                schema: "myhealth",
                table: "exercises",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "assistant_consents",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    terms_version = table.Column<int>(type: "integer", nullable: true),
                    scopes = table.Column<int[]>(type: "integer[]", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    history = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_consents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assistant_proposals",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_proposals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "wellbeing_check_ins",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    sleep_quality = table.Column<int>(type: "integer", nullable: true),
                    energy = table.Column<int>(type: "integer", nullable: true),
                    workout_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wellbeing_check_ins", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_consents_user_id",
                schema: "myhealth",
                table: "assistant_consents",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assistant_proposals_user_id_created_at",
                schema: "myhealth",
                table: "assistant_proposals",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_wellbeing_check_ins_client_id_local_date",
                schema: "myhealth",
                table: "wellbeing_check_ins",
                columns: new[] { "client_id", "local_date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_consents",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "assistant_proposals",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "wellbeing_check_ins",
                schema: "myhealth");

            migrationBuilder.DropColumn(
                name: "body_checks",
                schema: "myhealth",
                table: "workout_sessions");

            migrationBuilder.DropColumn(
                name: "media",
                schema: "myhealth",
                table: "exercises");

            migrationBuilder.AddColumn<string>(
                name: "media_url",
                schema: "myhealth",
                table: "exercises",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }
    }
}
