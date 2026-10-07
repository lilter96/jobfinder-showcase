using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableCommentSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "edit_keyboard_message_id",
                table: "notifications",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "revision",
                table: "comment_drafts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "source_request_id",
                table: "comment_drafts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "comment_source_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    post_id = table.Column<string>(type: "text", nullable: false),
                    author = table.Column<string>(type: "text", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    preparation_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    draft_id = table.Column<long>(type: "bigint", nullable: true),
                    replaces_draft_id = table.Column<long>(type: "bigint", nullable: true),
                    auto_publish = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment_source_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comment_drafts_source_request_id",
                table: "comment_drafts",
                column: "source_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_comment_source_requests_source_key",
                table: "comment_source_requests",
                column: "source_key",
                unique: true,
                filter: "stage IN ('Queued', 'Waiting')");

            migrationBuilder.CreateIndex(
                name: "ix_comment_source_requests_stage_created_at",
                table: "comment_source_requests",
                columns: new[] { "stage", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE comment_source_requests, comment_drafts, notifications IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM comment_source_requests)
                    OR EXISTS (SELECT 1 FROM comment_drafts WHERE revision <> 0 OR source_request_id IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM notifications WHERE edit_keyboard_message_id IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_preparation WHERE message_type LIKE '%PrepareCommentSource%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_preparation_scheduled WHERE message_type LIKE '%PrepareCommentSource%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE '%PrepareCommentSource%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE '%PrepareCommentSource%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%PrepareCommentSource%') THEN
                    RAISE EXCEPTION 'Cannot roll back comment sources while roots, revisions, card edits or envelopes remain';
                  END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "comment_source_requests");

            migrationBuilder.DropIndex(
                name: "ix_comment_drafts_source_request_id",
                table: "comment_drafts");

            migrationBuilder.DropColumn(
                name: "edit_keyboard_message_id",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "comment_drafts");

            migrationBuilder.DropColumn(
                name: "source_request_id",
                table: "comment_drafts");
        }
    }
}
