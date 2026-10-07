using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableApplicationDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "application_delivery_request_id",
                table: "delivery_attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "delivery_request_id",
                table: "applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "application_delivery_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<long>(type: "bigint", nullable: false),
                    application_revision = table.Column<long>(type: "bigint", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_hash = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    letter_review_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resume_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    review = table.Column<string>(type: "jsonb", nullable: true),
                    tailored_resume_id = table.Column<long>(type: "bigint", nullable: true),
                    resume_hash = table.Column<string>(type: "text", nullable: true),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_delivery_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_application_delivery_requests_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_application_delivery_request_id",
                table: "delivery_attempts",
                column: "application_delivery_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_application_delivery_requests_application_id_application_re",
                table: "application_delivery_requests",
                columns: new[] { "application_id", "application_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_application_delivery_requests_stage_created_at",
                table: "application_delivery_requests",
                columns: new[] { "stage", "created_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_delivery_attempts_application_delivery_requests_application",
                table: "delivery_attempts",
                column: "application_delivery_request_id",
                principalTable: "application_delivery_requests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE application_delivery_requests, applications, delivery_attempts,
                  jobfinder_queues.wolverine_queue_hiring_deliver, jobfinder_queues.wolverine_queue_hiring_deliver_scheduled,
                  jobfinder_queues.wolverine_queue_preparation, jobfinder_queues.wolverine_queue_preparation_scheduled,
                  jobfinder_messages.wolverine_incoming_envelopes, jobfinder_messages.wolverine_outgoing_envelopes,
                  jobfinder_messages.wolverine_dead_letters IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM application_delivery_requests)
                    OR EXISTS (SELECT 1 FROM applications WHERE delivery_request_id IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM delivery_attempts WHERE application_delivery_request_id IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_hiring_deliver WHERE message_type LIKE 'jobfinder.hiring.application-%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_hiring_deliver_scheduled WHERE message_type LIKE 'jobfinder.hiring.application-%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_preparation WHERE message_type LIKE 'jobfinder.hiring.application-%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_preparation_scheduled WHERE message_type LIKE 'jobfinder.hiring.application-%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE 'jobfinder.hiring.application-%' AND status <> 'Handled')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE 'jobfinder.hiring.application-%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE 'jobfinder.hiring.application-%') THEN
                    RAISE EXCEPTION 'Cannot roll back application delivery while requests, markers, attempts or envelopes remain';
                  END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropForeignKey(
                name: "fk_delivery_attempts_application_delivery_requests_application",
                table: "delivery_attempts");

            migrationBuilder.DropTable(
                name: "application_delivery_requests");

            migrationBuilder.DropIndex(
                name: "ix_delivery_attempts_application_delivery_request_id",
                table: "delivery_attempts");

            migrationBuilder.DropColumn(
                name: "application_delivery_request_id",
                table: "delivery_attempts");

            migrationBuilder.DropColumn(
                name: "delivery_request_id",
                table: "applications");
        }
    }
}
