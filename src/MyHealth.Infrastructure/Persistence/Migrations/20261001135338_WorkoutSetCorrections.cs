using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyHealth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkoutSetCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "corrections",
                schema: "myhealth",
                table: "workout_set_logs",
                type: "jsonb",
                nullable: false,
                // Existing sets have no corrections; an empty string would not be valid JSON.
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "corrections",
                schema: "myhealth",
                table: "workout_set_logs");
        }
    }
}
