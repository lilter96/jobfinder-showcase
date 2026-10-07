using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurablePostProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "processing_request_id",
                table: "raw_posts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "processing_revision",
                table: "raw_posts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "post_processing_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reading_json = table.Column<string>(type: "jsonb", nullable: true),
                    reading_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    extraction_json = table.Column<string>(type: "jsonb", nullable: true),
                    extraction_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    verification_json = table.Column<string>(type: "jsonb", nullable: true),
                    verification_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    result_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post_processing_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_post_processing_requests_post_id_revision",
                table: "post_processing_requests",
                columns: new[] { "post_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_post_processing_requests_stage_created_at",
                table: "post_processing_requests",
                columns: new[] { "stage", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE post_processing_requests, raw_posts IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM post_processing_requests)
                    OR EXISTS (SELECT 1 FROM raw_posts WHERE processing_request_id IS NOT NULL OR processing_revision <> 0)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_discovery WHERE message_type LIKE '%ProcessPost%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_discovery_scheduled WHERE message_type LIKE '%ProcessPost%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE '%ProcessPost%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE '%ProcessPost%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%ProcessPost%') THEN
                    RAISE EXCEPTION 'Cannot roll back post processing while roots, source markers, revisions or envelopes remain';
                  END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "post_processing_requests");

            migrationBuilder.DropColumn(
                name: "processing_request_id",
                table: "raw_posts");

            migrationBuilder.DropColumn(
                name: "processing_revision",
                table: "raw_posts");
        }
    }
}
