using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableChannelMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "membership_request_id",
                table: "telegram_channels",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "channel_membership_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    decision_revision = table.Column<long>(type: "bigint", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_hash = table.Column<string>(type: "text", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: true),
                    chat_json = table.Column<string>(type: "jsonb", nullable: true),
                    chat_hash = table.Column<string>(type: "text", nullable: true),
                    history_json = table.Column<string>(type: "jsonb", nullable: true),
                    history_hash = table.Column<string>(type: "text", nullable: true),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    spacing_seconds = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_membership_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_channel_membership_requests_telegram_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "telegram_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "channel_membership_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    envelope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    peer_id = table.Column<long>(type: "bigint", nullable: false),
                    input_hash = table.Column<string>(type: "text", nullable: false),
                    chat_hash = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    evidence = table.Column<string>(type: "text", nullable: true),
                    checked_applied = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_membership_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_channel_membership_attempts_channel_membership_requests_req",
                        column: x => x.request_id,
                        principalTable: "channel_membership_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_channel_membership_attempts_request_id",
                table: "channel_membership_attempts",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_membership_attempts_stage_started_at",
                table: "channel_membership_attempts",
                columns: new[] { "stage", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_channel_membership_requests_channel_id_decision_revision",
                table: "channel_membership_requests",
                columns: new[] { "channel_id", "decision_revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE telegram_channels, channel_membership_requests, channel_membership_attempts IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                DECLARE storage text; has_work boolean;
                BEGIN
                  IF EXISTS (SELECT 1 FROM channel_membership_requests)
                    OR EXISTS (SELECT 1 FROM channel_membership_attempts)
                    OR EXISTS (SELECT 1 FROM telegram_channels WHERE membership_request_id IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM settings WHERE key IN ('telegram.membership.notBefore', 'telegram.membership.floodUntil')) THEN
                    RAISE EXCEPTION 'Cannot downgrade retained Telegram membership evidence';
                  END IF;
                  FOREACH storage IN ARRAY ARRAY['jobfinder_queues.wolverine_queue_discovery',
                    'jobfinder_queues.wolverine_queue_discovery_scheduled', 'jobfinder_messages.wolverine_incoming_envelopes',
                    'jobfinder_messages.wolverine_outgoing_envelopes', 'jobfinder_messages.wolverine_dead_letters'] LOOP
                    IF EXISTS (SELECT FROM pg_class WHERE oid = to_regclass(storage)) THEN
                      EXECUTE format('SELECT EXISTS (SELECT 1 FROM %s WHERE message_type LIKE ''jobfinder.discovery.channel-membership.%%'' OR message_type LIKE ''%%ProcessChannelMembership%%'')', storage) INTO STRICT has_work;
                      IF has_work THEN RAISE EXCEPTION 'Cannot downgrade retained Telegram membership envelopes'; END IF;
                    END IF;
                  END LOOP;
                END
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "channel_membership_attempts");

            migrationBuilder.DropTable(
                name: "channel_membership_requests");

            migrationBuilder.DropColumn(
                name: "membership_request_id",
                table: "telegram_channels");
        }
    }
}
