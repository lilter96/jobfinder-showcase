using System;
using Microsoft.EntityFrameworkCore.Migrations;
using JobFinder.Data.Storage;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableReplyProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(WolverineStorage.ReplyQueueSql);
            migrationBuilder.CreateTable(
                name: "reply_processing_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_key = table.Column<string>(type: "text", nullable: false),
                    application_id = table.Column<long>(type: "bigint", nullable: false),
                    vacancy_id = table.Column<long>(type: "bigint", nullable: false),
                    event_id = table.Column<long>(type: "bigint", nullable: true),
                    retry_of_id = table.Column<Guid>(type: "uuid", nullable: true),
                    mode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false),
                    raw_hash = table.Column<string>(type: "text", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: true),
                    input_hash = table.Column<string>(type: "text", nullable: true),
                    best_json = table.Column<string>(type: "jsonb", nullable: true),
                    deep_attempt = table.Column<int>(type: "integer", nullable: false),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reply_processing_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reply_processing_requests_application_id_stage_created_at",
                table: "reply_processing_requests",
                columns: new[] { "application_id", "stage", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reply_processing_requests_business_key",
                table: "reply_processing_requests",
                column: "business_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reply_processing_requests_retry_of_id",
                table: "reply_processing_requests",
                column: "retry_of_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE reply_processing_requests IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM reply_processing_requests)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_hiring_replies)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_hiring_replies_scheduled)
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE '%ProcessReply%' OR message_type LIKE 'jobfinder.hiring.process-reply.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE '%ProcessReply%' OR message_type LIKE 'jobfinder.hiring.process-reply.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%ProcessReply%' OR message_type LIKE 'jobfinder.hiring.process-reply.%') THEN
                    RAISE EXCEPTION 'Cannot roll back durable replies while requests or envelopes remain';
                  END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "reply_processing_requests");
        }
    }
}
