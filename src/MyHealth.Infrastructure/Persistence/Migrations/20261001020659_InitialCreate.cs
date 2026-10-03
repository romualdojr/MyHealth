using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MyHealth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.EnsureSchema(
                name: "myhealth");

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "text", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_entries",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "care_relationships",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    professional_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    professional_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    invitee_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    invited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by = table.Column<Guid>(type: "uuid", nullable: true),
                    end_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_care_relationships", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "exercises",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    muscle_groups = table.Column<List<string>>(type: "text[]", nullable: false),
                    equipment = table.Column<List<string>>(type: "text[]", nullable: false),
                    purpose = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    precautions = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    media_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    media_caption = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exercises", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "health_connections",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "professional_profiles",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    bio = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    photo_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    verification_status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    review_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    registration_council = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    registration_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    registration_region = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_professional_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "training_plans",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trainer_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trainer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    current_version_number = table.Column<int>(type: "integer", nullable: false),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    has_unpublished_changes = table.Column<bool>(type: "boolean", nullable: false),
                    copied_from_plan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    goal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    level = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    suggested_sessions_per_week = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_training_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_profiles",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_role_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_asp_net_role_claims_asp_net_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "AspNetRoles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_type = table.Column<string>(type: "text", nullable: true),
                    claim_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_asp_net_user_claims_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                schema: "identity",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    provider_key = table.Column<string>(type: "text", nullable: false),
                    provider_display_name = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_logins", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "fk_asp_net_user_logins_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_asp_net_user_roles_asp_net_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "AspNetRoles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_asp_net_user_roles_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    login_provider = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asp_net_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "fk_asp_net_user_tokens_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "data_sharing_grants",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    care_relationship_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_sharing_grants", x => x.id);
                    table.ForeignKey(
                        name: "fk_data_sharing_grants_care_relationships_care_relationship_id",
                        column: x => x.care_relationship_id,
                        principalSchema: "myhealth",
                        principalTable: "care_relationships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "health_connection_changes",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    health_connection_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_connection_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_health_connection_changes_health_connections_health_connect",
                        column: x => x.health_connection_id,
                        principalSchema: "myhealth",
                        principalTable: "health_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "training_plan_versions",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    training_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trainer_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trainer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    change_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    goal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    level = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    suggested_sessions_per_week = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_training_plan_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_training_plan_versions_training_plans_training_plan_id",
                        column: x => x.training_plan_id,
                        principalSchema: "myhealth",
                        principalTable: "training_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workout_sessions",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    training_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_version_number = table.Column<int>(type: "integer", nullable: false),
                    planned_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    planned_session_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    trainer_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trainer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    abandoned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: true),
                    allowed_exercise_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    feedback_perceived_exertion = table.Column<int>(type: "integer", nullable: true),
                    feedback_reported_discomfort = table.Column<bool>(type: "boolean", nullable: true),
                    feedback_discomfort_details = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    feedback_comments = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reply_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reply_replied_by_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reply_replied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workout_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_workout_sessions_training_plan_versions_plan_version_id",
                        column: x => x.plan_version_id,
                        principalSchema: "myhealth",
                        principalTable: "training_plan_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workout_heart_rates",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_finalized = table.Column<bool>(type: "boolean", nullable: false),
                    finalized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_revised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    summary_sample_count = table.Column<int>(type: "integer", nullable: true),
                    summary_average_bpm = table.Column<double>(type: "double precision", nullable: true),
                    summary_maximum_bpm = table.Column<double>(type: "double precision", nullable: true),
                    summary_minimum_bpm = table.Column<double>(type: "double precision", nullable: true),
                    summary_first_sample_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    summary_last_sample_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    summary_method = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    summary_calculated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workout_heart_rates", x => x.id);
                    table.ForeignKey(
                        name: "fk_workout_heart_rates_workout_sessions_id",
                        column: x => x.id,
                        principalSchema: "myhealth",
                        principalTable: "workout_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workout_pauses",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    paused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    workout_session_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workout_pauses", x => x.id);
                    table.ForeignKey(
                        name: "fk_workout_pauses_workout_sessions_workout_session_id",
                        column: x => x.workout_session_id,
                        principalSchema: "myhealth",
                        principalTable: "workout_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workout_set_logs",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    exercise_id = table.Column<Guid>(type: "uuid", nullable: false),
                    planned_exercise_id = table.Column<Guid>(type: "uuid", nullable: true),
                    set_number = table.Column<int>(type: "integer", nullable: false),
                    repetitions = table.Column<int>(type: "integer", nullable: true),
                    load_kg = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                    duration_seconds = table.Column<int>(type: "integer", nullable: true),
                    distance_meters = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    difficulty = table.Column<int>(type: "integer", nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    logged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    workout_session_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workout_set_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_workout_set_logs_workout_sessions_workout_session_id",
                        column: x => x.workout_session_id,
                        principalSchema: "myhealth",
                        principalTable: "workout_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "heart_rate_samples",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    beats_per_minute = table.Column<double>(type: "double precision", nullable: false),
                    source_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    device_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    corrected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    workout_heart_rate_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_heart_rate_samples", x => x.id);
                    table.ForeignKey(
                        name: "fk_heart_rate_samples_workout_heart_rates_workout_heart_rate_id",
                        column: x => x.workout_heart_rate_id,
                        principalSchema: "myhealth",
                        principalTable: "workout_heart_rates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "heart_rate_syncs",
                schema: "myhealth",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    accepted = table.Column<int>(type: "integer", nullable: false),
                    duplicates = table.Column<int>(type: "integer", nullable: false),
                    corrected = table.Column<int>(type: "integer", nullable: false),
                    rejected = table.Column<int>(type: "integer", nullable: false),
                    removed = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    workout_heart_rate_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_heart_rate_syncs", x => x.id);
                    table.ForeignKey(
                        name: "fk_heart_rate_syncs_workout_heart_rates_workout_heart_rate_id",
                        column: x => x.workout_heart_rate_id,
                        principalSchema: "myhealth",
                        principalTable: "workout_heart_rates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_role_claims_role_id",
                schema: "identity",
                table: "AspNetRoleClaims",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "identity",
                table: "AspNetRoles",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_user_claims_user_id",
                schema: "identity",
                table: "AspNetUserClaims",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_user_logins_user_id",
                schema: "identity",
                table: "AspNetUserLogins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_user_roles_role_id",
                schema: "identity",
                table: "AspNetUserRoles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "identity",
                table: "AspNetUsers",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "identity",
                table: "AspNetUsers",
                column: "normalized_user_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_actor_user_id_occurred_at",
                schema: "myhealth",
                table: "audit_entries",
                columns: new[] { "actor_user_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_subject_user_id_occurred_at",
                schema: "myhealth",
                table: "audit_entries",
                columns: new[] { "subject_user_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_care_relationships_client_id_status",
                schema: "myhealth",
                table: "care_relationships",
                columns: new[] { "client_id", "status" });

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

            migrationBuilder.CreateIndex(
                name: "ix_data_sharing_grants_care_relationship_id",
                schema: "myhealth",
                table: "data_sharing_grants",
                column: "care_relationship_id");

            migrationBuilder.CreateIndex(
                name: "ix_exercises_name",
                schema: "myhealth",
                table: "exercises",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_exercises_owner_profile_id",
                schema: "myhealth",
                table: "exercises",
                column: "owner_profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_health_connection_changes_health_connection_id",
                schema: "myhealth",
                table: "health_connection_changes",
                column: "health_connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_health_connections_client_id_provider",
                schema: "myhealth",
                table: "health_connections",
                columns: new[] { "client_id", "provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_heart_rate_samples_workout_heart_rate_id_external_id",
                schema: "myhealth",
                table: "heart_rate_samples",
                columns: new[] { "workout_heart_rate_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_heart_rate_syncs_workout_heart_rate_id",
                schema: "myhealth",
                table: "heart_rate_syncs",
                column: "workout_heart_rate_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_user_id_created_at",
                schema: "myhealth",
                table: "notifications",
                columns: new[] { "recipient_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_professional_profiles_user_id_discipline",
                schema: "myhealth",
                table: "professional_profiles",
                columns: new[] { "user_id", "discipline" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_professional_profiles_verification_status",
                schema: "myhealth",
                table: "professional_profiles",
                column: "verification_status");

            migrationBuilder.CreateIndex(
                name: "ix_training_plan_versions_training_plan_id_version_number",
                schema: "myhealth",
                table: "training_plan_versions",
                columns: new[] { "training_plan_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_training_plans_client_id_status",
                schema: "myhealth",
                table: "training_plans",
                columns: new[] { "client_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_training_plans_trainer_profile_id_client_id",
                schema: "myhealth",
                table: "training_plans",
                columns: new[] { "trainer_profile_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_profiles_email",
                schema: "myhealth",
                table: "user_profiles",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workout_heart_rates_client_id",
                schema: "myhealth",
                table: "workout_heart_rates",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_workout_pauses_workout_session_id",
                schema: "myhealth",
                table: "workout_pauses",
                column: "workout_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_workout_sessions_client_id_status_local_date",
                schema: "myhealth",
                table: "workout_sessions",
                columns: new[] { "client_id", "status", "local_date" });

            migrationBuilder.CreateIndex(
                name: "ix_workout_sessions_plan_version_id",
                schema: "myhealth",
                table: "workout_sessions",
                column: "plan_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_workout_sessions_trainer_profile_id_client_id",
                schema: "myhealth",
                table: "workout_sessions",
                columns: new[] { "trainer_profile_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workout_sessions_training_plan_id",
                schema: "myhealth",
                table: "workout_sessions",
                column: "training_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_workout_set_logs_exercise_id",
                schema: "myhealth",
                table: "workout_set_logs",
                column: "exercise_id");

            migrationBuilder.CreateIndex(
                name: "ix_workout_set_logs_workout_session_id",
                schema: "myhealth",
                table: "workout_set_logs",
                column: "workout_session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AspNetRoleClaims",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "audit_entries",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "data_sharing_grants",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "exercises",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "health_connection_changes",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "heart_rate_samples",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "heart_rate_syncs",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "professional_profiles",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "user_profiles",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "workout_pauses",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "workout_set_logs",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "AspNetRoles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "AspNetUsers",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "care_relationships",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "health_connections",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "workout_heart_rates",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "workout_sessions",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "training_plan_versions",
                schema: "myhealth");

            migrationBuilder.DropTable(
                name: "training_plans",
                schema: "myhealth");
        }
    }
}
