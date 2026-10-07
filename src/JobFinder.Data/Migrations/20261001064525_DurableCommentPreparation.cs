using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableCommentPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "comment_preparation_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_json = table.Column<string>(type: "jsonb", nullable: true),
                    first_json = table.Column<string>(type: "jsonb", nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment_preparation_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comment_preparation_requests_business_key",
                table: "comment_preparation_requests",
                column: "business_key",
                unique: true,
                filter: "stage IN ('Drafting', 'Checking')");

            migrationBuilder.CreateIndex(
                name: "ix_comment_preparation_requests_stage_created_at",
                table: "comment_preparation_requests",
                columns: new[] { "stage", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE comment_preparation_requests IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM comment_preparation_requests)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_engagement)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_engagement_scheduled)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_preparation WHERE message_type LIKE '%CheckComment%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_preparation_scheduled WHERE message_type LIKE '%CheckComment%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE '%DraftComment%' OR message_type LIKE '%CheckComment%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE '%DraftComment%' OR message_type LIKE '%CheckComment%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%DraftComment%' OR message_type LIKE '%CheckComment%') THEN
                    RAISE EXCEPTION 'Cannot roll back durable comments while requests or envelopes remain';
                  END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "comment_preparation_requests");
        }
    }
}
