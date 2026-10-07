using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "comment_drafts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    post_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    post_url = table.Column<string>(type: "text", nullable: false),
                    author = table.Column<string>(type: "text", nullable: false),
                    audience = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    post_text = table.Column<string>(type: "text", nullable: false),
                    post_published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    variants = table.Column<List<string>>(type: "text[]", nullable: false),
                    chosen_text = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment_drafts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    hh_id = table.Column<long>(type: "bigint", nullable: true),
                    habr_alias = table.Column<string>(type: "text", nullable: true),
                    site = table.Column<string>(type: "text", nullable: true),
                    area = table.Column<string>(type: "text", nullable: true),
                    size_category = table.Column<string>(type: "text", nullable: true),
                    industries = table.Column<List<string>>(type: "text[]", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    rating = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: true),
                    it_accredited = table.Column<bool>(type: "boolean", nullable: false),
                    open_vacancies = table.Column<int>(type: "integer", nullable: true),
                    dotnet_vacancies = table.Column<int>(type: "integer", nullable: false),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    watch = table.Column<bool>(type: "boolean", nullable: false),
                    blacklisted = table.Column<bool>(type: "boolean", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    details_fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_companies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    items = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "llm_calls",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: false),
                    completion_tokens = table.Column<int>(type: "integer", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    ok = table.Column<bool>(type: "boolean", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_llm_calls", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    buttons = table.Column<string>(type: "jsonb", nullable: false),
                    entity_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    telegram_message_id = table.Column<int>(type: "integer", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "raw_posts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    chat_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    chat_title = table.Column<string>(type: "text", nullable: true),
                    external_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    url = table.Column<string>(type: "text", nullable: true),
                    author = table.Column<string>(type: "text", nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    vacancy_id = table.Column<long>(type: "bigint", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_raw_posts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "telegram_channels",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    title = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    peer_id = table.Column<long>(type: "bigint", nullable: true),
                    access_hash = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    last_message_id = table.Column<int>(type: "integer", nullable: false),
                    posts_seen = table.Column<int>(type: "integer", nullable: false),
                    vacancies_found = table.Column<int>(type: "integer", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telegram_channels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vacancies",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    company_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    company_id = table.Column<long>(type: "bigint", nullable: true),
                    description = table.Column<string>(type: "text", nullable: false),
                    salary_from = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    salary_to = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    salary_gross = table.Column<bool>(type: "boolean", nullable: true),
                    city = table.Column<string>(type: "text", nullable: true),
                    country_code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    work_formats = table.Column<int>(type: "integer", nullable: false),
                    english_required = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    skills = table.Column<List<string>>(type: "text[]", nullable: false),
                    experience = table.Column<string>(type: "text", nullable: true),
                    contacts = table.Column<string>(type: "text", nullable: true),
                    url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    reject_reason = table.Column<string>(type: "text", nullable: true),
                    score = table.Column<int>(type: "integer", nullable: true),
                    llm_score = table.Column<int>(type: "integer", nullable: true),
                    salary_adjustment = table.Column<int>(type: "integer", nullable: false),
                    evaluation = table.Column<string>(type: "jsonb", nullable: true),
                    decision = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    cover_letter = table.Column<string>(type: "text", nullable: true),
                    notified = table.Column<bool>(type: "boolean", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    scored_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vacancies", x => x.id);
                    table.ForeignKey(
                        name: "fk_vacancies_companies_company_id",
                        column: x => x.company_id,
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "applications",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    vacancy_id = table.Column<long>(type: "bigint", nullable: false),
                    channel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    mode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    cover_letter = table.Column<string>(type: "text", nullable: true),
                    target = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    screenshot_path = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_applications", x => x.id);
                    table.ForeignKey(
                        name: "fk_applications_vacancies_vacancy_id",
                        column: x => x.vacancy_id,
                        principalTable: "vacancies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vacancy_sources",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    vacancy_id = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    external_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    host = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    letter_required = table.Column<bool>(type: "boolean", nullable: false),
                    has_employer_test = table.Column<bool>(type: "boolean", nullable: false),
                    raw_json = table.Column<string>(type: "jsonb", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vacancy_sources", x => x.id);
                    table.ForeignKey(
                        name: "fk_vacancy_sources_vacancies_vacancy_id",
                        column: x => x.vacancy_id,
                        principalTable: "vacancies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_applications_created_at",
                table: "applications",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_applications_status",
                table: "applications",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_applications_vacancy_id",
                table: "applications",
                column: "vacancy_id");

            migrationBuilder.CreateIndex(
                name: "ix_comment_drafts_author",
                table: "comment_drafts",
                column: "author");

            migrationBuilder.CreateIndex(
                name: "ix_comment_drafts_post_id",
                table: "comment_drafts",
                column: "post_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_comment_drafts_status",
                table: "comment_drafts",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_companies_habr_alias",
                table: "companies",
                column: "habr_alias",
                unique: true,
                filter: "habr_alias IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_companies_hh_id",
                table: "companies",
                column: "hh_id",
                unique: true,
                filter: "hh_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_companies_name",
                table: "companies",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_job_runs_job_started_at",
                table: "job_runs",
                columns: new[] { "job", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_llm_calls_at",
                table: "llm_calls",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_status",
                table: "notifications",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_raw_posts_source_chat_ref_external_id",
                table: "raw_posts",
                columns: new[] { "source", "chat_ref", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_raw_posts_status",
                table: "raw_posts",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_telegram_channels_peer_id",
                table: "telegram_channels",
                column: "peer_id",
                unique: true,
                filter: "peer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_telegram_channels_username",
                table: "telegram_channels",
                column: "username",
                unique: true,
                filter: "username IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_vacancies_company_id",
                table: "vacancies",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_vacancies_fingerprint",
                table: "vacancies",
                column: "fingerprint");

            migrationBuilder.CreateIndex(
                name: "ix_vacancies_first_seen_at",
                table: "vacancies",
                column: "first_seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_vacancies_score",
                table: "vacancies",
                column: "score");

            migrationBuilder.CreateIndex(
                name: "ix_vacancies_status",
                table: "vacancies",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_vacancy_sources_source_external_id",
                table: "vacancy_sources",
                columns: new[] { "source", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vacancy_sources_vacancy_id",
                table: "vacancy_sources",
                column: "vacancy_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "applications");

            migrationBuilder.DropTable(
                name: "comment_drafts");

            migrationBuilder.DropTable(
                name: "job_runs");

            migrationBuilder.DropTable(
                name: "llm_calls");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "raw_posts");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "telegram_channels");

            migrationBuilder.DropTable(
                name: "vacancy_sources");

            migrationBuilder.DropTable(
                name: "vacancies");

            migrationBuilder.DropTable(
                name: "companies");
        }
    }
}
