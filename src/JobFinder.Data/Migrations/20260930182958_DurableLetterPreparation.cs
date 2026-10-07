using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableLetterPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(JobFinder.Data.Storage.WolverineStorage.LetterQueuesSql);
            migrationBuilder.CreateTable(
                name: "letter_preparation_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vacancy_id = table.Column<long>(type: "bigint", nullable: true),
                    business_key = table.Column<string>(type: "text", nullable: false),
                    input_hash = table.Column<string>(type: "text", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    best_json = table.Column<string>(type: "jsonb", nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    grace_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_letter_preparation_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "letter_preparation_lanes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    lane = table.Column<int>(type: "integer", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    draft_json = table.Column<string>(type: "jsonb", nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_letter_preparation_lanes", x => x.id);
                    table.ForeignKey(
                        name: "fk_letter_preparation_lanes_letter_preparation_requests_reques",
                        column: x => x.request_id,
                        principalTable: "letter_preparation_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_letter_preparation_lanes_request_id_round_lane",
                table: "letter_preparation_lanes",
                columns: new[] { "request_id", "round", "lane" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_letter_preparation_requests_business_key",
                table: "letter_preparation_requests",
                column: "business_key",
                unique: true,
                filter: "stage = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_letter_preparation_requests_business_key_created_at",
                table: "letter_preparation_requests",
                columns: new[] { "business_key", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE letter_preparation_requests, letter_preparation_lanes IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM letter_preparation_requests)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_hiring_letter_draft)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_hiring_letter_draft_scheduled)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_hiring_letter_review)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_hiring_letter_review_scheduled) THEN
                    RAISE EXCEPTION 'Cannot roll back durable letters while requests or envelopes remain';
                  END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "letter_preparation_lanes");

            migrationBuilder.DropTable(
                name: "letter_preparation_requests");
        }
    }
}
