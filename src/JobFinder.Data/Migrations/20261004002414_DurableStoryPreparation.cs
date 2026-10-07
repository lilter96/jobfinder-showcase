using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableStoryPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(name: "preparation_request_id", table: "stories", type: "uuid", nullable: true);
            migrationBuilder.CreateIndex(name: "ix_stories_preparation_request_id", table: "stories", column: "preparation_request_id", unique: true,
                filter: "preparation_request_id IS NOT NULL");
            migrationBuilder.CreateTable(
                name: "story_preparation_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_key = table.Column<string>(type: "text", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_hash = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    result_hash = table.Column<string>(type: "text", nullable: true),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_story_preparation_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_story_preparation_requests_business_key",
                table: "story_preparation_requests",
                column: "business_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_story_preparation_requests_stage_created_at",
                table: "story_preparation_requests",
                columns: new[] { "stage", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE stories, story_preparation_requests IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                DECLARE storage text; has_work boolean;
                BEGIN
                  IF EXISTS (SELECT 1 FROM story_preparation_requests)
                    OR EXISTS (SELECT 1 FROM stories WHERE preparation_request_id IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM story_revisions WHERE coalesce(before_json->>'preparationRequestId', after_json->>'preparationRequestId') IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM settings WHERE key = 'bot.conversation' AND (value LIKE '%"storyRequestId"%' OR value LIKE '%"storyCandidate"%')) THEN
                    RAISE EXCEPTION 'Cannot downgrade retained story preparation roots, source or dialogue evidence';
                  END IF;
                  FOREACH storage IN ARRAY ARRAY[
                    'jobfinder_queues.wolverine_queue_preparation', 'jobfinder_queues.wolverine_queue_preparation_scheduled',
                    'jobfinder_messages.wolverine_incoming_envelopes', 'jobfinder_messages.wolverine_outgoing_envelopes',
                    'jobfinder_messages.wolverine_dead_letters'] LOOP
                    IF EXISTS (SELECT FROM pg_class WHERE oid = to_regclass(storage)) THEN
                      EXECUTE format('SELECT EXISTS (SELECT 1 FROM %s WHERE message_type LIKE ''jobfinder.candidate.draft-story.%%'' OR message_type LIKE ''jobfinder.candidate.publish-story.%%'' OR message_type LIKE ''%%DraftStory%%'' OR message_type LIKE ''%%PublishStory%%'')', storage) INTO STRICT has_work;
                      IF has_work THEN RAISE EXCEPTION 'Cannot downgrade retained story preparation envelopes'; END IF;
                    END IF;
                  END LOOP;
                END
                $rollback$;
                """);
            migrationBuilder.DropIndex(name: "ix_stories_preparation_request_id", table: "stories");
            migrationBuilder.DropColumn(name: "preparation_request_id", table: "stories");
            migrationBuilder.DropTable(
                name: "story_preparation_requests");
        }
    }
}
