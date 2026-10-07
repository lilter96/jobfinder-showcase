using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableHrPlaybookPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hr_playbook_requests",
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
                    table.PrimaryKey("pk_hr_playbook_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hr_playbook_requests_business_key",
                table: "hr_playbook_requests",
                column: "business_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_hr_playbook_requests_stage_created_at",
                table: "hr_playbook_requests",
                columns: new[] { "stage", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE hr_playbook_requests IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                DECLARE storage text; has_work boolean;
                BEGIN
                  IF EXISTS (SELECT 1 FROM hr_playbook_requests)
                    OR EXISTS (SELECT 1 FROM settings WHERE key IN ('hr.playbook.request', 'hr.playbook.owner')) THEN
                    RAISE EXCEPTION 'Cannot downgrade retained HR preparation roots or publication evidence';
                  END IF;
                  FOREACH storage IN ARRAY ARRAY[
                    'jobfinder_queues.wolverine_queue_preparation', 'jobfinder_queues.wolverine_queue_preparation_scheduled',
                    'jobfinder_messages.wolverine_incoming_envelopes', 'jobfinder_messages.wolverine_outgoing_envelopes',
                    'jobfinder_messages.wolverine_dead_letters'] LOOP
                    IF EXISTS (SELECT FROM pg_class WHERE oid = to_regclass(storage)) THEN
                      EXECUTE format('SELECT EXISTS (SELECT 1 FROM %s WHERE message_type LIKE ''jobfinder.preparation.build-hr-playbook.%%'' OR message_type LIKE ''jobfinder.preparation.publish-hr-playbook.%%'' OR message_type LIKE ''%%BuildHrPlaybook%%'' OR message_type LIKE ''%%PublishHrPlaybook%%'')', storage) INTO STRICT has_work;
                      IF has_work THEN RAISE EXCEPTION 'Cannot downgrade retained HR preparation envelopes'; END IF;
                    END IF;
                  END LOOP;
                END
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "hr_playbook_requests");
        }
    }
}
