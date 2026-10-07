using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableInterviewPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "preparation_request_id",
                table: "interviews",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "preparation_revision",
                table: "interviews",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "interview_preparation_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    interview_id = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    seed_json = table.Column<string>(type: "jsonb", nullable: false),
                    seed_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: true),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    plan_json = table.Column<string>(type: "jsonb", nullable: true),
                    plan_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    copilot_json = table.Column<string>(type: "jsonb", nullable: true),
                    copilot_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    pdf_path = table.Column<string>(type: "text", nullable: true),
                    pdf_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_interview_preparation_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_interview_preparation_requests_interview_id_revision",
                table: "interview_preparation_requests",
                columns: new[] { "interview_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_interview_preparation_requests_stage_created_at",
                table: "interview_preparation_requests",
                columns: new[] { "stage", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE interview_preparation_requests, interviews IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM interview_preparation_requests)
                    OR EXISTS (SELECT 1 FROM interviews WHERE preparation_request_id IS NOT NULL OR preparation_revision <> 0)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_preparation WHERE message_type LIKE '%PrepareInterview%' OR message_type LIKE 'jobfinder.preparation.interview.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_preparation_scheduled WHERE message_type LIKE '%PrepareInterview%' OR message_type LIKE 'jobfinder.preparation.interview.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_hiring_prepare WHERE message_type LIKE '%PrepareInterview%' OR message_type LIKE 'jobfinder.preparation.interview.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_hiring_prepare_scheduled WHERE message_type LIKE '%PrepareInterview%' OR message_type LIKE 'jobfinder.preparation.interview.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE '%PrepareInterview%' OR message_type LIKE 'jobfinder.preparation.interview.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE '%PrepareInterview%' OR message_type LIKE 'jobfinder.preparation.interview.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%PrepareInterview%' OR message_type LIKE 'jobfinder.preparation.interview.%') THEN
                    RAISE EXCEPTION 'Cannot roll back interview preparation while roots, source markers, revisions or envelopes remain';
                  END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "interview_preparation_requests");

            migrationBuilder.DropColumn(
                name: "preparation_request_id",
                table: "interviews");

            migrationBuilder.DropColumn(
                name: "preparation_revision",
                table: "interviews");
        }
    }
}
