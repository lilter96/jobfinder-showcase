using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableVacancyPreview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_vacancy_assessment_requests_vacancy_id",
                table: "vacancy_assessment_requests");

            migrationBuilder.AddColumn<string>(
                name: "preview_key",
                table: "vacancy_assessment_requests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "score_hash",
                table: "vacancy_assessment_requests",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_vacancy_assessment_requests_preview_key",
                table: "vacancy_assessment_requests",
                column: "preview_key",
                unique: true,
                filter: "preview_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_vacancy_assessment_requests_vacancy_id",
                table: "vacancy_assessment_requests",
                column: "vacancy_id",
                unique: true,
                filter: "vacancy_id > 0 AND stage IN ('Scoring', 'Lettering', 'Finishing')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE vacancy_assessment_requests IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                DECLARE storage text; has_work boolean;
                BEGIN
                  IF EXISTS (SELECT 1 FROM vacancy_assessment_requests WHERE preview_key IS NOT NULL OR score_hash IS NOT NULL
                      OR input_json ->> 'version' = '3' OR input_json ? 'preview' OR vacancy_id = 0) THEN
                    RAISE EXCEPTION 'Cannot downgrade retained vacancy previews';
                  END IF;
                  FOREACH storage IN ARRAY ARRAY['jobfinder_queues.wolverine_queue_scoring',
                    'jobfinder_queues.wolverine_queue_scoring_scheduled', 'jobfinder_messages.wolverine_incoming_envelopes',
                    'jobfinder_messages.wolverine_outgoing_envelopes', 'jobfinder_messages.wolverine_dead_letters'] LOOP
                    IF EXISTS (SELECT FROM pg_class WHERE oid = to_regclass(storage)) THEN
                      EXECUTE format('SELECT EXISTS (SELECT 1 FROM %s WHERE message_type LIKE ''jobfinder.discovery.vacancy-preview-score.%%'' OR message_type LIKE ''jobfinder.discovery.vacancy-preview-finish.%%'' OR message_type LIKE ''%%ScoreVacancyPreview%%'' OR message_type LIKE ''%%FinishVacancyPreview%%'')', storage) INTO STRICT has_work;
                      IF has_work THEN RAISE EXCEPTION 'Cannot downgrade retained vacancy previews envelopes'; END IF;
                    END IF;
                  END LOOP;
                END
                $rollback$;
                """);
            migrationBuilder.DropIndex(
                name: "ix_vacancy_assessment_requests_preview_key",
                table: "vacancy_assessment_requests");

            migrationBuilder.DropIndex(
                name: "ix_vacancy_assessment_requests_vacancy_id",
                table: "vacancy_assessment_requests");

            migrationBuilder.DropColumn(
                name: "preview_key",
                table: "vacancy_assessment_requests");

            migrationBuilder.DropColumn(
                name: "score_hash",
                table: "vacancy_assessment_requests");

            migrationBuilder.CreateIndex(
                name: "ix_vacancy_assessment_requests_vacancy_id",
                table: "vacancy_assessment_requests",
                column: "vacancy_id",
                unique: true,
                filter: "stage IN ('Scoring', 'Lettering', 'Finishing')");
        }
    }
}
