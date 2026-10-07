using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableMockPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "preparation_request_id",
                table: "mock_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "revision",
                table: "mock_sessions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "mock_preparation_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("pk_mock_preparation_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_mock_preparation_requests_mock_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "mock_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mock_session_revisions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: true),
                    after_json = table.Column<string>(type: "jsonb", nullable: false),
                    change = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mock_session_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_mock_session_revisions_mock_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "mock_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mock_preparation_requests_business_key",
                table: "mock_preparation_requests",
                column: "business_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mock_preparation_requests_session_id_stage",
                table: "mock_preparation_requests",
                columns: new[] { "session_id", "stage" });

            migrationBuilder.CreateIndex(
                name: "ix_mock_session_revisions_session_id_revision",
                table: "mock_session_revisions",
                columns: new[] { "session_id", "revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE mock_sessions, mock_preparation_requests, mock_session_revisions IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                DECLARE storage text; has_work boolean;
                BEGIN
                  IF EXISTS (SELECT 1 FROM mock_preparation_requests)
                    OR EXISTS (SELECT 1 FROM mock_session_revisions)
                    OR EXISTS (SELECT 1 FROM mock_sessions WHERE revision <> 0 OR preparation_request_id IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM settings WHERE key = 'bot.conversation' AND (value LIKE '%"mockRequestId"%' OR value LIKE '%"mockPending"%')) THEN
                    RAISE EXCEPTION 'Cannot downgrade retained mock preparation roots, source, history or dialogue evidence';
                  END IF;
                  FOREACH storage IN ARRAY ARRAY[
                    'jobfinder_queues.wolverine_queue_preparation', 'jobfinder_queues.wolverine_queue_preparation_scheduled',
                    'jobfinder_messages.wolverine_incoming_envelopes', 'jobfinder_messages.wolverine_outgoing_envelopes',
                    'jobfinder_messages.wolverine_dead_letters'] LOOP
                    IF EXISTS (SELECT FROM pg_class WHERE oid = to_regclass(storage)) THEN
                      EXECUTE format('SELECT EXISTS (SELECT 1 FROM %s WHERE message_type LIKE ''jobfinder.preparation.prepare-mock.%%'' OR message_type LIKE ''jobfinder.preparation.publish-mock.%%'' OR message_type LIKE ''%%PrepareMock%%'' OR message_type LIKE ''%%PublishMock%%'')', storage) INTO STRICT has_work;
                      IF has_work THEN RAISE EXCEPTION 'Cannot downgrade retained mock preparation envelopes'; END IF;
                    END IF;
                  END LOOP;
                END
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "mock_preparation_requests");

            migrationBuilder.DropTable(
                name: "mock_session_revisions");

            migrationBuilder.DropColumn(
                name: "preparation_request_id",
                table: "mock_sessions");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "mock_sessions");
        }
    }
}
