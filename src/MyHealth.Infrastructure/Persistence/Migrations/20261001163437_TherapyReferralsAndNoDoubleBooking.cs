using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyHealth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TherapyReferralsAndNoDoubleBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Referrals become a history: an existing single referral becomes the first entry.
            migrationBuilder.AddColumn<string>(
                name: "referrals",
                schema: "myhealth",
                table: "therapy_sessions",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.Sql(
                "UPDATE myhealth.therapy_sessions SET referrals = jsonb_build_array(referral) WHERE referral IS NOT NULL AND referral <> 'null'::jsonb;");

            migrationBuilder.DropColumn(
                name: "referral",
                schema: "myhealth",
                table: "therapy_sessions");

            // A practitioner can never hold two scheduled sessions at overlapping times, even when two bookings race
            // (the application check alone cannot guarantee it). Violations surface as 409.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql("""
                ALTER TABLE myhealth.therapy_sessions
                ADD CONSTRAINT ex_therapy_sessions_practitioner_no_overlap
                EXCLUDE USING gist (practitioner_profile_id WITH =, tstzrange(starts_at, ends_at) WITH &&)
                WHERE (status = 'Scheduled');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE myhealth.therapy_sessions DROP CONSTRAINT IF EXISTS ex_therapy_sessions_practitioner_no_overlap;");

            migrationBuilder.AddColumn<string>(
                name: "referral",
                schema: "myhealth",
                table: "therapy_sessions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.Sql("UPDATE myhealth.therapy_sessions SET referral = referrals -> -1 WHERE jsonb_array_length(referrals) > 0;");

            migrationBuilder.DropColumn(
                name: "referrals",
                schema: "myhealth",
                table: "therapy_sessions");
        }
    }
}
