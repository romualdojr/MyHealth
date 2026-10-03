using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyHealth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppleHealthIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_heart_rate_samples_workout_heart_rate_id_external_id",
                schema: "myhealth",
                table: "heart_rate_samples");

            migrationBuilder.AddColumn<Guid>(
                name: "apple_health_workout_id",
                schema: "myhealth",
                table: "workout_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "apple_health_workout_recorded_at",
                schema: "myhealth",
                table: "workout_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "apple_health_workout_saved_at",
                schema: "myhealth",
                table: "workout_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "summary_self_reported_sample_count",
                schema: "myhealth",
                table: "workout_heart_rates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "summary_time_weighted_average_bpm",
                schema: "myhealth",
                table: "workout_heart_rates",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "summary_time_weighted_method",
                schema: "myhealth",
                table: "workout_heart_rates",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "claimed_by_other_session",
                schema: "myhealth",
                table: "heart_rate_syncs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "client_id",
                schema: "myhealth",
                table: "heart_rate_samples",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "myhealth",
                table: "heart_rate_samples",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Measured");

            migrationBuilder.AddColumn<bool>(
                name: "save_workouts",
                schema: "myhealth",
                table: "health_connections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "save_workouts_changed_at",
                schema: "myhealth",
                table: "health_connections",
                type: "timestamp with time zone",
                nullable: true);

            // Existing rows: owner comes from the parent record, everything imported so far was device-measured,
            // and summaries get the new fields (time-weighted mean is recalculated on the next sync).
            migrationBuilder.Sql("""
                UPDATE myhealth.heart_rate_samples s SET client_id = h.client_id
                FROM myhealth.workout_heart_rates h WHERE s.workout_heart_rate_id = h.id;
                UPDATE myhealth.workout_heart_rates
                SET summary_self_reported_sample_count = 0, summary_time_weighted_method = 'time-weighted-trapezoidal-excluding-gaps-over-60s'
                WHERE summary_sample_count IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_heart_rate_samples_client_id_external_id",
                schema: "myhealth",
                table: "heart_rate_samples",
                columns: new[] { "client_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_heart_rate_samples_workout_heart_rate_id",
                schema: "myhealth",
                table: "heart_rate_samples",
                column: "workout_heart_rate_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_heart_rate_samples_client_id_external_id",
                schema: "myhealth",
                table: "heart_rate_samples");

            migrationBuilder.DropIndex(
                name: "ix_heart_rate_samples_workout_heart_rate_id",
                schema: "myhealth",
                table: "heart_rate_samples");

            migrationBuilder.DropColumn(
                name: "apple_health_workout_id",
                schema: "myhealth",
                table: "workout_sessions");

            migrationBuilder.DropColumn(
                name: "apple_health_workout_recorded_at",
                schema: "myhealth",
                table: "workout_sessions");

            migrationBuilder.DropColumn(
                name: "apple_health_workout_saved_at",
                schema: "myhealth",
                table: "workout_sessions");

            migrationBuilder.DropColumn(
                name: "summary_self_reported_sample_count",
                schema: "myhealth",
                table: "workout_heart_rates");

            migrationBuilder.DropColumn(
                name: "summary_time_weighted_average_bpm",
                schema: "myhealth",
                table: "workout_heart_rates");

            migrationBuilder.DropColumn(
                name: "summary_time_weighted_method",
                schema: "myhealth",
                table: "workout_heart_rates");

            migrationBuilder.DropColumn(
                name: "claimed_by_other_session",
                schema: "myhealth",
                table: "heart_rate_syncs");

            migrationBuilder.DropColumn(
                name: "client_id",
                schema: "myhealth",
                table: "heart_rate_samples");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "myhealth",
                table: "heart_rate_samples");

            migrationBuilder.DropColumn(
                name: "save_workouts",
                schema: "myhealth",
                table: "health_connections");

            migrationBuilder.DropColumn(
                name: "save_workouts_changed_at",
                schema: "myhealth",
                table: "health_connections");

            migrationBuilder.CreateIndex(
                name: "ix_heart_rate_samples_workout_heart_rate_id_external_id",
                schema: "myhealth",
                table: "heart_rate_samples",
                columns: new[] { "workout_heart_rate_id", "external_id" },
                unique: true);
        }
    }
}
