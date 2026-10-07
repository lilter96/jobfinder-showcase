using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableLinkedInPeople : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "linked_in_people_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_key = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_hash = table.Column<string>(type: "text", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    result_hash = table.Column<string>(type: "text", nullable: true),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_linked_in_people_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_linked_in_people_requests_business_key",
                table: "linked_in_people_requests",
                column: "business_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_linked_in_people_requests_stage_created_at",
                table: "linked_in_people_requests",
                columns: new[] { "stage", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE linked_in_people_requests IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM linked_in_people_requests)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_hiring_replies WHERE message_type LIKE '%ReadLinkedInPeople%' OR message_type LIKE 'jobfinder.hiring.read-linkedin-people.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_hiring_replies_scheduled WHERE message_type LIKE '%ReadLinkedInPeople%' OR message_type LIKE 'jobfinder.hiring.read-linkedin-people.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE '%ReadLinkedInPeople%' OR message_type LIKE 'jobfinder.hiring.read-linkedin-people.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE '%ReadLinkedInPeople%' OR message_type LIKE 'jobfinder.hiring.read-linkedin-people.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%ReadLinkedInPeople%' OR message_type LIKE 'jobfinder.hiring.read-linkedin-people.%') THEN
                    RAISE EXCEPTION 'Cannot roll back LinkedIn people preparation while roots or envelopes remain';
                  END IF;
                END
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "linked_in_people_requests");
        }
    }
}
