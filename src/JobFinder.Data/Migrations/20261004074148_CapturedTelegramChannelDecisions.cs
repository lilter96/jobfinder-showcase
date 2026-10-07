using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class CapturedTelegramChannelDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "decision_revision",
                table: "telegram_channels",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "telegram_channel_approval_batches",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    version = table.Column<int>(type: "integer", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notification_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telegram_channel_approval_batches", x => x.id);
                    table.ForeignKey(
                        name: "fk_telegram_channel_approval_batches_notifications_notificatio",
                        column: x => x.notification_id,
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "telegram_channel_decision_revisions",
                columns: table => new
                {
                    channel_id = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    batch_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telegram_channel_decision_revisions", x => new { x.channel_id, x.revision });
                    table.ForeignKey(
                        name: "fk_telegram_channel_decision_revisions_telegram_channel_approv",
                        column: x => x.batch_id,
                        principalTable: "telegram_channel_approval_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_telegram_channel_decision_revisions_telegram_channels_chann",
                        column: x => x.channel_id,
                        principalTable: "telegram_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_telegram_channel_approval_batches_notification_id",
                table: "telegram_channel_approval_batches",
                column: "notification_id");

            migrationBuilder.CreateIndex(
                name: "ix_telegram_channel_decision_revisions_batch_id",
                table: "telegram_channel_decision_revisions",
                column: "batch_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE telegram_channels, telegram_channel_approval_batches, telegram_channel_decision_revisions IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM telegram_channel_approval_batches)
                    OR EXISTS (SELECT 1 FROM telegram_channel_decision_revisions)
                    OR EXISTS (SELECT 1 FROM telegram_channels WHERE decision_revision <> 0) THEN
                    RAISE EXCEPTION 'Cannot downgrade retained channel controls, batches or decision revisions';
                  END IF;
                END
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "telegram_channel_decision_revisions");

            migrationBuilder.DropTable(
                name: "telegram_channel_approval_batches");

            migrationBuilder.DropColumn(
                name: "decision_revision",
                table: "telegram_channels");
        }
    }
}
