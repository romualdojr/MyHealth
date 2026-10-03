using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyHealth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultiTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workout_sessions_trainer_profile_id_client_id",
                schema: "myhealth",
                table: "workout_sessions");

            migrationBuilder.DropIndex(
                name: "ix_training_plans_trainer_profile_id_client_id",
                schema: "myhealth",
                table: "training_plans");

            migrationBuilder.DropIndex(
                name: "ix_notifications_recipient_user_id_created_at",
                schema: "myhealth",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_care_relationships_professional_profile_id_invitee_email_st",
                schema: "myhealth",
                table: "care_relationships");

            migrationBuilder.DropIndex(
                name: "ix_care_relationships_professional_user_id_discipline_client_i",
                schema: "myhealth",
                table: "care_relationships");

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "myhealth",
                table: "workout_sessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "myhealth",
                table: "workout_heart_rates",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "myhealth",
                table: "training_plans",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "myhealth",
                table: "training_plan_versions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "audience",
                schema: "myhealth",
                table: "notifications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "myhealth",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "myhealth",
                table: "exercises",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "myhealth",
                table: "care_relationships",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "myhealth",
                table: "audit_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_memberships",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invitee_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    disciplines = table.Column<string>(type: "jsonb", nullable: false),
                    is_administrator = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    invited_by = table.Column<Guid>(type: "uuid", nullable: false),
                    invited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoke_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_memberships", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenant_memberships_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "myhealth",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            BackfillExistingData(migrationBuilder);

            migrationBuilder.CreateIndex(
                name: "ix_workout_sessions_tenant_id_trainer_profile_id_client_id",
                schema: "myhealth",
                table: "workout_sessions",
                columns: new[] { "tenant_id", "trainer_profile_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workout_heart_rates_tenant_id_client_id",
                schema: "myhealth",
                table: "workout_heart_rates",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_training_plans_tenant_id_trainer_profile_id_client_id",
                schema: "myhealth",
                table: "training_plans",
                columns: new[] { "tenant_id", "trainer_profile_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_training_plan_versions_tenant_id",
                schema: "myhealth",
                table: "training_plan_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_user_id_audience_tenant_id_created_",
                schema: "myhealth",
                table: "notifications",
                columns: new[] { "recipient_user_id", "audience", "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_exercises_tenant_id",
                schema: "myhealth",
                table: "exercises",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_care_relationships_tenant_id_professional_profile_id_invite",
                schema: "myhealth",
                table: "care_relationships",
                columns: new[] { "tenant_id", "professional_profile_id", "invitee_email", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_care_relationships_tenant_id_professional_user_id_disciplin",
                schema: "myhealth",
                table: "care_relationships",
                columns: new[] { "tenant_id", "professional_user_id", "discipline", "client_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_tenant_id_occurred_at",
                schema: "myhealth",
                table: "audit_entries",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_memberships_tenant_id_invitee_email_status",
                schema: "myhealth",
                table: "tenant_memberships",
                columns: new[] { "tenant_id", "invitee_email", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_memberships_tenant_id_user_id",
                schema: "myhealth",
                table: "tenant_memberships",
                columns: new[] { "tenant_id", "user_id" },
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_memberships_user_id_status",
                schema: "myhealth",
                table: "tenant_memberships",
                columns: new[] { "user_id", "status" });

            migrationBuilder.AddForeignKey(
                name: "fk_care_relationships_tenants_tenant_id",
                schema: "myhealth",
                table: "care_relationships",
                column: "tenant_id",
                principalSchema: "myhealth",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_exercises_tenants_tenant_id",
                schema: "myhealth",
                table: "exercises",
                column: "tenant_id",
                principalSchema: "myhealth",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_training_plan_versions_tenants_tenant_id",
                schema: "myhealth",
                table: "training_plan_versions",
                column: "tenant_id",
                principalSchema: "myhealth",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_training_plans_tenants_tenant_id",
                schema: "myhealth",
                table: "training_plans",
                column: "tenant_id",
                principalSchema: "myhealth",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_workout_heart_rates_tenants_tenant_id",
                schema: "myhealth",
                table: "workout_heart_rates",
                column: "tenant_id",
                principalSchema: "myhealth",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_workout_sessions_tenants_tenant_id",
                schema: "myhealth",
                table: "workout_sessions",
                column: "tenant_id",
                principalSchema: "myhealth",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <summary>
        /// Data created before multi-tenancy belongs to independent professionals: each one gets a solo-practice tenant
        /// (id = their user id, so the mapping needs no lookup table) with an administrator membership for the
        /// disciplines they hold, and every record is attached to the tenant of the professional responsible for it.
        /// Runs before the foreign keys are created so no row is left pointing at the placeholder empty id.
        /// </summary>
        private static void BackfillExistingData(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO myhealth.tenants (id, name, kind, created_by, created_at)
                SELECT p.user_id, COALESCE(MIN(u.full_name), MIN(p.display_name)), 'SoloPractice', p.user_id, MIN(p.created_at)
                FROM myhealth.professional_profiles p
                LEFT JOIN myhealth.user_profiles u ON u.id = p.user_id
                GROUP BY p.user_id;

                INSERT INTO myhealth.tenant_memberships
                    (id, tenant_id, user_id, invitee_email, disciplines, is_administrator, status, invited_by, invited_at, joined_at)
                SELECT gen_random_uuid(), p.user_id, p.user_id, COALESCE(MIN(u.email), ''), jsonb_agg(p.discipline ORDER BY p.discipline),
                       TRUE, 'Active', p.user_id, MIN(p.created_at), MIN(p.created_at)
                FROM myhealth.professional_profiles p
                LEFT JOIN myhealth.user_profiles u ON u.id = p.user_id
                GROUP BY p.user_id;

                UPDATE myhealth.care_relationships SET tenant_id = professional_user_id;
                UPDATE myhealth.training_plans SET tenant_id = trainer_user_id;
                UPDATE myhealth.training_plan_versions v SET tenant_id = p.tenant_id
                    FROM myhealth.training_plans p WHERE p.id = v.training_plan_id;
                UPDATE myhealth.workout_sessions s SET tenant_id = p.tenant_id
                    FROM myhealth.training_plans p WHERE p.id = s.training_plan_id;
                UPDATE myhealth.workout_heart_rates h SET tenant_id = s.tenant_id
                    FROM myhealth.workout_sessions s WHERE s.id = h.id;
                UPDATE myhealth.exercises e SET tenant_id = p.user_id
                    FROM myhealth.professional_profiles p WHERE p.id = e.owner_profile_id;

                UPDATE myhealth.notifications SET audience = CASE type
                    WHEN 'DiscomfortReported' THEN 'Professional'
                    WHEN 'CareRelationshipActivated' THEN 'Professional'
                    WHEN 'WorkoutCompleted' THEN 'Professional'
                    WHEN 'ProfessionalVerified' THEN 'Account'
                    ELSE 'Client' END;
                UPDATE myhealth.notifications n SET tenant_id = p.tenant_id
                    FROM myhealth.training_plans p WHERE n.resource_type = 'TrainingPlan' AND p.id = n.resource_id;
                UPDATE myhealth.notifications n SET tenant_id = s.tenant_id
                    FROM myhealth.workout_sessions s WHERE n.resource_type = 'WorkoutSession' AND s.id = n.resource_id;
                UPDATE myhealth.notifications n SET tenant_id = r.tenant_id
                    FROM myhealth.care_relationships r WHERE n.resource_type = 'CareRelationship' AND r.id = n.resource_id;

                ALTER TABLE myhealth.care_relationships ALTER COLUMN tenant_id DROP DEFAULT;
                ALTER TABLE myhealth.training_plans ALTER COLUMN tenant_id DROP DEFAULT;
                ALTER TABLE myhealth.training_plan_versions ALTER COLUMN tenant_id DROP DEFAULT;
                ALTER TABLE myhealth.workout_sessions ALTER COLUMN tenant_id DROP DEFAULT;
                ALTER TABLE myhealth.workout_heart_rates ALTER COLUMN tenant_id DROP DEFAULT;
                ALTER TABLE myhealth.notifications ALTER COLUMN audience DROP DEFAULT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_care_relationships_tenants_tenant_id",
                schema: "myhealth",
                table: "care_relationships");

            migrationBuilder.DropForeignKey(
                name: "fk_exercises_tenants_tenant_id",
                schema: "myhealth",
                table: "exercises");

            migrationBuilder.DropForeignKey(
                name: "fk_training_plan_versions_tenants_tenant_id",
                schema: "myhealth",
                table: "training_plan_versions");

            migrationBuilder.DropForeignKey(
                name: "fk_training_plans_tenants_tenant_id",
                schema: "myhealth",
                table: "training_plans");

            migrationBuilder.DropForeignKey(
                name: "fk_workout_heart_rates_tenants_tenant_id",
                schema: "myhealth",
                table: "workout_heart_rates");

            migrationBuilder.DropForeignKey(
                name: "fk_workout_sessions_tenants_tenant_id",
                schema: "myhealth",
                table: "workout_sessions");

            migrationBuilder.DropTable(
                name: "tenant_memberships",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "myhealth");

            migrationBuilder.DropIndex(
                name: "ix_workout_sessions_tenant_id_trainer_profile_id_client_id",
                schema: "myhealth",
                table: "workout_sessions");

            migrationBuilder.DropIndex(
                name: "ix_workout_heart_rates_tenant_id_client_id",
                schema: "myhealth",
                table: "workout_heart_rates");

            migrationBuilder.DropIndex(
                name: "ix_training_plans_tenant_id_trainer_profile_id_client_id",
                schema: "myhealth",
                table: "training_plans");

            migrationBuilder.DropIndex(
                name: "ix_training_plan_versions_tenant_id",
                schema: "myhealth",
                table: "training_plan_versions");

            migrationBuilder.DropIndex(
                name: "ix_notifications_recipient_user_id_audience_tenant_id_created_",
                schema: "myhealth",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_exercises_tenant_id",
                schema: "myhealth",
                table: "exercises");

            migrationBuilder.DropIndex(
                name: "ix_care_relationships_tenant_id_professional_profile_id_invite",
                schema: "myhealth",
                table: "care_relationships");

            migrationBuilder.DropIndex(
                name: "ix_care_relationships_tenant_id_professional_user_id_disciplin",
                schema: "myhealth",
                table: "care_relationships");

            migrationBuilder.DropIndex(
                name: "ix_audit_entries_tenant_id_occurred_at",
                schema: "myhealth",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "myhealth",
                table: "workout_sessions");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "myhealth",
                table: "workout_heart_rates");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "myhealth",
                table: "training_plans");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "myhealth",
                table: "training_plan_versions");

            migrationBuilder.DropColumn(
                name: "audience",
                schema: "myhealth",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "myhealth",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "myhealth",
                table: "exercises");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "myhealth",
                table: "care_relationships");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "myhealth",
                table: "audit_entries");

            migrationBuilder.CreateIndex(
                name: "ix_workout_sessions_trainer_profile_id_client_id",
                schema: "myhealth",
                table: "workout_sessions",
                columns: new[] { "trainer_profile_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_training_plans_trainer_profile_id_client_id",
                schema: "myhealth",
                table: "training_plans",
                columns: new[] { "trainer_profile_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_user_id_created_at",
                schema: "myhealth",
                table: "notifications",
                columns: new[] { "recipient_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_care_relationships_professional_profile_id_invitee_email_st",
                schema: "myhealth",
                table: "care_relationships",
                columns: new[] { "professional_profile_id", "invitee_email", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_care_relationships_professional_user_id_discipline_client_i",
                schema: "myhealth",
                table: "care_relationships",
                columns: new[] { "professional_user_id", "discipline", "client_id", "status" });
        }
    }
}
