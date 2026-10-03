using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyHealth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WhatsApp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "whatsapp_contacts",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone_number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    allow_professional_contact = table.Column<bool>(type: "boolean", nullable: false),
                    verification_code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    verification_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verification_attempts = table.Column<int>(type: "integer", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    history = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_contacts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_contacts_phone_number",
                schema: "myhealth",
                table: "whatsapp_contacts",
                column: "phone_number");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_contacts_user_id",
                schema: "myhealth",
                table: "whatsapp_contacts",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "whatsapp_contacts",
                schema: "myhealth");
        }
    }
}
