using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableResumePreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "resume_preparation_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vacancy_id = table.Column<long>(type: "bigint", nullable: false),
                    input_hash = table.Column<string>(type: "text", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_json = table.Column<string>(type: "jsonb", nullable: true),
                    best_json = table.Column<string>(type: "jsonb", nullable: true),
                    result_id = table.Column<long>(type: "bigint", nullable: true),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resume_preparation_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_resume_preparation_requests_stage_created_at",
                table: "resume_preparation_requests",
                columns: new[] { "stage", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_resume_preparation_requests_vacancy_id",
                table: "resume_preparation_requests",
                column: "vacancy_id",
                unique: true,
                filter: "stage IN ('Drafting', 'Printing', 'Reviewing', 'Finalizing')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE resume_preparation_requests IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                    IF EXISTS (SELECT 1 FROM resume_preparation_requests) THEN
                        RAISE EXCEPTION 'resume rollback refused: requests contain data; stop producers/consumers and export/reconcile them first'
                            USING ERRCODE = 'P0001';
                    END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "resume_preparation_requests");
        }
    }
}
