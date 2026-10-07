using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class TargetIndependentNotificationAdmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "edit_parent_notification_id",
                table: "notifications",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "admission_hash",
                table: "notification_delivery_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "admission_json",
                table: "notification_delivery_requests",
                type: "jsonb",
                nullable: true);
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
                    OR EXISTS (SELECT 1 FROM notifications WHERE delivery_request_id IS NOT NULL OR edit_parent_notification_id IS NOT NULL OR status IN ('Sending', 'OutcomeUnknown', 'Reconciled'))
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_notifications WHERE message_type LIKE '%DeliverNotification%' OR message_type LIKE '%BindNotificationTarget%' OR message_type LIKE 'jobfinder.notifications.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_notifications_scheduled WHERE message_type LIKE '%DeliverNotification%' OR message_type LIKE '%BindNotificationTarget%' OR message_type LIKE 'jobfinder.notifications.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE '%DeliverNotification%' OR message_type LIKE '%BindNotificationTarget%' OR message_type LIKE 'jobfinder.notifications.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE '%DeliverNotification%' OR message_type LIKE '%BindNotificationTarget%' OR message_type LIKE 'jobfinder.notifications.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE '%DeliverNotification%' OR message_type LIKE '%BindNotificationTarget%' OR message_type LIKE 'jobfinder.notifications.%') THEN
                    RAISE EXCEPTION 'Cannot roll back notification delivery while roots, attempts, source markers or envelopes remain';
                  END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropColumn(
                name: "edit_parent_notification_id",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "admission_hash",
                table: "notification_delivery_requests");

            migrationBuilder.DropColumn(
                name: "admission_json",
                table: "notification_delivery_requests");
        }
    }
}
