using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableNotificationDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "delivery_request_id",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "notification_delivery_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effect_index = table.Column<int>(type: "integer", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    plan_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    effect_json = table.Column<string>(type: "jsonb", nullable: false),
                    effect_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    telegram_message_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconciled_sent = table.Column<bool>(type: "boolean", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_delivery_attempts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_delivery_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<long>(type: "bigint", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    plan_json = table.Column<string>(type: "jsonb", nullable: true),
                    plan_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    effect_index = table.Column<int>(type: "integer", nullable: false),
                    plain_text = table.Column<bool>(type: "boolean", nullable: false),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_delivery_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_delivery_attempts_request_id_effect_index_numb",
                table: "notification_delivery_attempts",
                columns: new[] { "request_id", "effect_index", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_delivery_requests_notification_id",
                table: "notification_delivery_requests",
                column: "notification_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_delivery_requests_stage_created_at",
                table: "notification_delivery_requests",
                columns: new[] { "stage", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE notification_delivery_requests, notification_delivery_attempts, notifications IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM notification_delivery_requests)
                    OR EXISTS (SELECT 1 FROM notification_delivery_attempts)
                    OR EXISTS (SELECT 1 FROM notifications WHERE delivery_request_id IS NOT NULL OR status IN ('Sending', 'OutcomeUnknown', 'Reconciled'))
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_notifications WHERE message_type LIKE '%DeliverNotification%' OR message_type LIKE 'jobfinder.notifications.delivery.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_notifications_scheduled WHERE message_type LIKE '%DeliverNotification%' OR message_type LIKE 'jobfinder.notifications.delivery.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE '%DeliverNotification%' OR message_type LIKE 'jobfinder.notifications.delivery.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE '%DeliverNotification%' OR message_type LIKE 'jobfinder.notifications.delivery.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%DeliverNotification%' OR message_type LIKE 'jobfinder.notifications.delivery.%') THEN
                    RAISE EXCEPTION 'Cannot roll back notification delivery while roots, attempts, source markers or envelopes remain';
                  END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "notification_delivery_attempts");

            migrationBuilder.DropTable(
                name: "notification_delivery_requests");

            migrationBuilder.DropColumn(
                name: "delivery_request_id",
                table: "notifications");
        }
    }
}
