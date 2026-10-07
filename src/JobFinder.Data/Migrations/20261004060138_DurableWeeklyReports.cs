using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableWeeklyReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "weekly_report_request_id",
                table: "job_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "weekly_report_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_key = table.Column<string>(type: "text", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_hash = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    result_hash = table.Column<string>(type: "text", nullable: true),
                    job_run_id = table.Column<long>(type: "bigint", nullable: true),
                    notification_id = table.Column<long>(type: "bigint", nullable: false),
                    published_html_hash = table.Column<string>(type: "text", nullable: true),
                    advice_published = table.Column<bool>(type: "boolean", nullable: false),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_weekly_report_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_weekly_report_requests_job_runs_job_run_id",
                        column: x => x.job_run_id,
                        principalTable: "job_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_weekly_report_requests_business_key",
                table: "weekly_report_requests",
                column: "business_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_weekly_report_requests_job_run_id",
                table: "weekly_report_requests",
                column: "job_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_weekly_report_requests_stage_created_at",
                table: "weekly_report_requests",
                columns: new[] { "stage", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE weekly_report_requests, job_runs IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                DECLARE storage text; has_work boolean;
                BEGIN
                  IF EXISTS (SELECT 1 FROM weekly_report_requests)
                    OR EXISTS (SELECT 1 FROM job_runs WHERE weekly_report_request_id IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM settings WHERE key IN ('report.weekly.owner', 'report.hhAdvice.owner', 'jobs.weekly-report.publication')) THEN
                    RAISE EXCEPTION 'Cannot downgrade retained weekly report roots, receipts or publication evidence';
                  END IF;
                  FOREACH storage IN ARRAY ARRAY[
                    'jobfinder_queues.wolverine_queue_preparation', 'jobfinder_queues.wolverine_queue_preparation_scheduled',
                    'jobfinder_messages.wolverine_incoming_envelopes', 'jobfinder_messages.wolverine_outgoing_envelopes',
                    'jobfinder_messages.wolverine_dead_letters'] LOOP
                    IF EXISTS (SELECT FROM pg_class WHERE oid = to_regclass(storage)) THEN
                      EXECUTE format('SELECT EXISTS (SELECT 1 FROM %s WHERE message_type LIKE ''jobfinder.preparation.prepare-weekly-report.%%'' OR message_type LIKE ''jobfinder.preparation.publish-weekly-report.%%'' OR message_type LIKE ''%%PrepareWeeklyReport%%'' OR message_type LIKE ''%%PublishWeeklyReport%%'')', storage) INTO STRICT has_work;
                      IF has_work THEN RAISE EXCEPTION 'Cannot downgrade retained weekly report envelopes'; END IF;
                    END IF;
                  END LOOP;
                END
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "weekly_report_requests");

            migrationBuilder.DropColumn(
                name: "weekly_report_request_id",
                table: "job_runs");
        }
    }
}
