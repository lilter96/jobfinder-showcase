using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class CareerSuite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "legitimacy",
                table: "vacancies",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                // A valid enum name: rows written by an older build (which does not know the column) must still load.
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "original_posted_at",
                table: "vacancies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "responses_count",
                table: "vacancies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "attachment_name",
                table: "notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "attachment_path",
                table: "notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email_message_id",
                table: "applications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "follow_up_sent_at",
                table: "applications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_inbound_at",
                table: "applications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_outbound_at",
                table: "applications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stage",
                table: "applications",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "stage_changed_at",
                table: "applications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "tailored_resume_id",
                table: "applications",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "target_peer_id",
                table: "applications",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "application_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    application_id = table.Column<long>(type: "bigint", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    channel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    author = table.Column<string>(type: "text", nullable: true),
                    text = table.Column<string>(type: "text", nullable: true),
                    external_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    classification = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    draft = table.Column<string>(type: "text", nullable: true),
                    draft_status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_application_events_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "company_contacts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    company_id = table.Column<long>(type: "bigint", nullable: true),
                    company_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    company_name = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    person_name = table.Column<string>(type: "text", nullable: true),
                    role = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    source_ref = table.Column<string>(type: "text", nullable: true),
                    seen_count = table.Column<int>(type: "integer", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company_contacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "interviews",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    application_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    scheduled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    pack_json = table.Column<string>(type: "jsonb", nullable: true),
                    pack_pdf_path = table.Column<string>(type: "text", nullable: true),
                    reminded = table.Column<bool>(type: "boolean", nullable: false),
                    debrief_asked = table.Column<bool>(type: "boolean", nullable: false),
                    debrief = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_interviews", x => x.id);
                    table.ForeignKey(
                        name: "fk_interviews_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mock_sessions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    vacancy_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    turns_json = table.Column<string>(type: "jsonb", nullable: false),
                    current = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mock_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "offers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    application_id = table.Column<long>(type: "bigint", nullable: true),
                    company = table.Column<string>(type: "text", nullable: false),
                    raw_text = table.Column<string>(type: "text", nullable: false),
                    monthly_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "text", nullable: true),
                    gross = table.Column<bool>(type: "boolean", nullable: true),
                    monthly_net_usd = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    analysis_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_offers", x => x.id);
                    table.ForeignKey(
                        name: "fk_offers_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "stories",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "text", nullable: false),
                    achievement = table.Column<string>(type: "text", nullable: true),
                    situation = table.Column<string>(type: "text", nullable: false),
                    task = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    result = table.Column<string>(type: "text", nullable: false),
                    reflection = table.Column<string>(type: "text", nullable: false),
                    skills = table.Column<List<string>>(type: "text[]", nullable: false),
                    source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    raw_notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tailored_resumes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    vacancy_id = table.Column<long>(type: "bigint", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    content_json = table.Column<string>(type: "jsonb", nullable: false),
                    pdf_path = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    keywords_covered = table.Column<List<string>>(type: "text[]", nullable: false),
                    keywords_missing = table.Column<List<string>>(type: "text[]", nullable: false),
                    corrections = table.Column<List<string>>(type: "text[]", nullable: false),
                    model = table.Column<string>(type: "text", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tailored_resumes", x => x.id);
                    table.ForeignKey(
                        name: "fk_tailored_resumes_vacancies_vacancy_id",
                        column: x => x.vacancy_id,
                        principalTable: "vacancies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outreaches",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    vacancy_id = table.Column<long>(type: "bigint", nullable: false),
                    contact_id = table.Column<long>(type: "bigint", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outreaches", x => x.id);
                    table.ForeignKey(
                        name: "fk_outreaches_company_contacts_contact_id",
                        column: x => x.contact_id,
                        principalTable: "company_contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outreaches_vacancies_vacancy_id",
                        column: x => x.vacancy_id,
                        principalTable: "vacancies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vacancies_legitimacy",
                table: "vacancies",
                column: "legitimacy");

            migrationBuilder.CreateIndex(
                name: "ix_applications_stage",
                table: "applications",
                column: "stage");

            migrationBuilder.CreateIndex(
                name: "ix_applications_target_peer_id",
                table: "applications",
                column: "target_peer_id");

            migrationBuilder.CreateIndex(
                name: "ix_application_events_application_id_at",
                table: "application_events",
                columns: new[] { "application_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_application_events_channel_external_id",
                table: "application_events",
                columns: new[] { "channel", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_application_events_draft_status",
                table: "application_events",
                column: "draft_status");

            migrationBuilder.CreateIndex(
                name: "ix_company_contacts_company_id",
                table: "company_contacts",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_company_contacts_company_key_kind_value",
                table: "company_contacts",
                columns: new[] { "company_key", "kind", "value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_interviews_application_id",
                table: "interviews",
                column: "application_id");

            migrationBuilder.CreateIndex(
                name: "ix_interviews_scheduled_at",
                table: "interviews",
                column: "scheduled_at");

            migrationBuilder.CreateIndex(
                name: "ix_mock_sessions_status",
                table: "mock_sessions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_offers_application_id",
                table: "offers",
                column: "application_id");

            migrationBuilder.CreateIndex(
                name: "ix_outreaches_contact_id",
                table: "outreaches",
                column: "contact_id");

            migrationBuilder.CreateIndex(
                name: "ix_outreaches_status",
                table: "outreaches",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_outreaches_vacancy_id",
                table: "outreaches",
                column: "vacancy_id");

            migrationBuilder.CreateIndex(
                name: "ix_stories_confirmed",
                table: "stories",
                column: "confirmed");

            migrationBuilder.CreateIndex(
                name: "ix_tailored_resumes_vacancy_id",
                table: "tailored_resumes",
                column: "vacancy_id");
            // Applications sent before the funnel existed enter it at "Sent".
            migrationBuilder.Sql("UPDATE applications SET stage = 'Sent', stage_changed_at = sent_at WHERE status = 'Sent' AND stage IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application_events");

            migrationBuilder.DropTable(
                name: "interviews");

            migrationBuilder.DropTable(
                name: "mock_sessions");

            migrationBuilder.DropTable(
                name: "offers");

            migrationBuilder.DropTable(
                name: "outreaches");

            migrationBuilder.DropTable(
                name: "stories");

            migrationBuilder.DropTable(
                name: "tailored_resumes");

            migrationBuilder.DropTable(
                name: "company_contacts");

            migrationBuilder.DropIndex(
                name: "ix_vacancies_legitimacy",
                table: "vacancies");

            migrationBuilder.DropIndex(
                name: "ix_applications_stage",
                table: "applications");

            migrationBuilder.DropIndex(
                name: "ix_applications_target_peer_id",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "legitimacy",
                table: "vacancies");

            migrationBuilder.DropColumn(
                name: "original_posted_at",
                table: "vacancies");

            migrationBuilder.DropColumn(
                name: "responses_count",
                table: "vacancies");

            migrationBuilder.DropColumn(
                name: "attachment_name",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "attachment_path",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "email_message_id",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "follow_up_sent_at",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "last_inbound_at",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "last_outbound_at",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "stage",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "stage_changed_at",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "tailored_resume_id",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "target_peer_id",
                table: "applications");
        }
    }
}
