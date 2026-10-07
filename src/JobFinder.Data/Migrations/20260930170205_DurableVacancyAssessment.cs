using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableVacancyAssessment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "assessment_revision",
                table: "vacancies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "vacancy_assessment_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vacancy_id = table.Column<long>(type: "bigint", nullable: false),
                    input_hash = table.Column<string>(type: "text", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    score_json = table.Column<string>(type: "jsonb", nullable: true),
                    letter_json = table.Column<string>(type: "jsonb", nullable: true),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vacancy_assessment_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vacancy_assessment_requests_stage_created_at",
                table: "vacancy_assessment_requests",
                columns: new[] { "stage", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_vacancy_assessment_requests_vacancy_id",
                table: "vacancy_assessment_requests",
                column: "vacancy_id",
                unique: true,
                filter: "stage IN ('Scoring', 'Lettering', 'Finishing')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE vacancy_assessment_requests, vacancies IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                    IF EXISTS (SELECT 1 FROM vacancy_assessment_requests) OR EXISTS (SELECT 1 FROM vacancies WHERE assessment_revision <> 0) THEN
                        RAISE EXCEPTION 'assessment rollback refused: stop producers/consumers and export/reconcile assessment history first'
                            USING ERRCODE = 'P0001';
                    END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "vacancy_assessment_requests");

            migrationBuilder.DropColumn(
                name: "assessment_revision",
                table: "vacancies");
        }
    }
}
