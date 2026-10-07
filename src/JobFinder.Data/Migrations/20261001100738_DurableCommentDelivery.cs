using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableCommentDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "delivery_request_id",
                table: "comment_drafts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "comment_delivery_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft_id = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    target = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    text_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    prepared_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    reconciled_sent = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment_delivery_attempts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "comment_delivery_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft_id = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prepared_json = table.Column<string>(type: "jsonb", nullable: true),
                    prepared_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment_delivery_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comment_delivery_attempts_request_id_created_at",
                table: "comment_delivery_attempts",
                columns: new[] { "request_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_comment_delivery_requests_draft_id_revision",
                table: "comment_delivery_requests",
                columns: new[] { "draft_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_comment_delivery_requests_stage_created_at",
                table: "comment_delivery_requests",
                columns: new[] { "stage", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE comment_delivery_requests, comment_delivery_attempts, comment_drafts IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM comment_delivery_requests)
                    OR EXISTS (SELECT 1 FROM comment_delivery_attempts)
                    OR EXISTS (SELECT 1 FROM comment_drafts WHERE delivery_request_id IS NOT NULL OR status IN ('Sending', 'OutcomeUnknown'))
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_preparation WHERE message_type LIKE '%CommentDelivery%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_preparation_scheduled WHERE message_type LIKE '%CommentDelivery%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_engagement WHERE message_type LIKE '%CommentDelivery%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_engagement_scheduled WHERE message_type LIKE '%CommentDelivery%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE '%CommentDelivery%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE '%CommentDelivery%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%CommentDelivery%') THEN
                    RAISE EXCEPTION 'Cannot roll back comment delivery while roots, attempts, source markers or envelopes remain';
                  END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "comment_delivery_attempts");

            migrationBuilder.DropTable(
                name: "comment_delivery_requests");

            migrationBuilder.DropColumn(
                name: "delivery_request_id",
                table: "comment_drafts");
        }
    }
}
