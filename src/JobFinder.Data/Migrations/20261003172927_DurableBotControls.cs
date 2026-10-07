using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableBotControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Storage.WolverineStorage.BotControlsQueueSql);
            migrationBuilder.CreateTable(
                name: "bot_control_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bot_id = table.Column<long>(type: "bigint", nullable: false),
                    update_id = table.Column<int>(type: "integer", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_hash = table.Column<string>(type: "text", nullable: false),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    reconciliation_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bot_control_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bot_control_requests_bot_id_stage_update_id",
                table: "bot_control_requests",
                columns: new[] { "bot_id", "stage", "update_id" });

            migrationBuilder.CreateIndex(
                name: "ix_bot_control_requests_bot_id_update_id",
                table: "bot_control_requests",
                columns: new[] { "bot_id", "update_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE bot_control_requests, settings,
                  jobfinder_queues.wolverine_queue_bot_controls,
                  jobfinder_queues.wolverine_queue_bot_controls_scheduled IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM bot_control_requests)
                    OR EXISTS (SELECT 1 FROM settings WHERE key LIKE 'bot.poll.%')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_bot_controls)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_bot_controls_scheduled)
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type LIKE 'jobfinder.controls.process-bot.%' OR message_type LIKE '%ProcessBotControl%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type LIKE 'jobfinder.controls.process-bot.%' OR message_type LIKE '%ProcessBotControl%')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_dead_letters WHERE message_type LIKE 'jobfinder.controls.process-bot.%' OR message_type LIKE '%ProcessBotControl%') THEN
                    RAISE EXCEPTION 'Cannot roll back bot controls while roots, cursors or envelopes remain';
                  END IF;
                END
                $rollback$;
                DROP TABLE jobfinder_queues.wolverine_queue_bot_controls_scheduled;
                DROP TABLE jobfinder_queues.wolverine_queue_bot_controls;
                """);
            migrationBuilder.DropTable(
                name: "bot_control_requests");
        }
    }
}
