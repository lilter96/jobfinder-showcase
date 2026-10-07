using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class AtomicVacancyCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "assessment_capture_id",
                table: "vacancies",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE vacancies IN ACCESS EXCLUSIVE MODE;
                LOCK TABLE jobfinder_queues.wolverine_queue_preparation,
                  jobfinder_queues.wolverine_queue_preparation_scheduled,
                  jobfinder_messages.wolverine_incoming_envelopes,
                  jobfinder_messages.wolverine_outgoing_envelopes,
                  jobfinder_messages.wolverine_dead_letters IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM vacancies WHERE assessment_capture_id IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_preparation WHERE message_type = 'jobfinder.discovery.vacancy-capture.v1')
                    OR EXISTS (SELECT 1 FROM jobfinder_queues.wolverine_queue_preparation_scheduled WHERE message_type = 'jobfinder.discovery.vacancy-capture.v1')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_incoming_envelopes WHERE message_type = 'jobfinder.discovery.vacancy-capture.v1' AND status <> 'Handled')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_outgoing_envelopes WHERE message_type = 'jobfinder.discovery.vacancy-capture.v1')
                    OR EXISTS (SELECT 1 FROM jobfinder_messages.wolverine_dead_letters WHERE message_type = 'jobfinder.discovery.vacancy-capture.v1') THEN
                    RAISE EXCEPTION 'Cannot roll back atomic assessment capture while intents or envelopes remain';
                  END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropColumn(
                name: "assessment_capture_id",
                table: "vacancies");
        }
    }
}
