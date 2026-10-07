using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class LegacyNotificationReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "legacy_reconciliation_hash",
                table: "notification_delivery_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "legacy_reconciliation_json",
                table: "notification_delivery_requests",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE notification_delivery_requests IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM notification_delivery_requests WHERE input_json->'legacySource' IS NOT NULL OR admission_json->'legacySource' IS NOT NULL OR legacy_reconciliation_json IS NOT NULL OR legacy_reconciliation_hash IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_notifications WHERE message_type LIKE '%QuarantineLegacyNotification%' OR message_type LIKE 'jobfinder.notifications.quarantine-legacy.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_notifications_scheduled WHERE message_type LIKE '%QuarantineLegacyNotification%' OR message_type LIKE 'jobfinder.notifications.quarantine-legacy.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE '%QuarantineLegacyNotification%' OR message_type LIKE 'jobfinder.notifications.quarantine-legacy.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE '%QuarantineLegacyNotification%' OR message_type LIKE 'jobfinder.notifications.quarantine-legacy.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%QuarantineLegacyNotification%' OR message_type LIKE 'jobfinder.notifications.quarantine-legacy.%') THEN
                    RAISE EXCEPTION 'Cannot roll back legacy notification admission while evidence or commands remain';
                  END IF;
                END
                $rollback$;
                """);
            migrationBuilder.DropColumn(
                name: "legacy_reconciliation_hash",
                table: "notification_delivery_requests");

            migrationBuilder.DropColumn(
                name: "legacy_reconciliation_json",
                table: "notification_delivery_requests");
        }
    }
}
