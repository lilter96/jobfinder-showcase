using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableOfferPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "offer_preparation_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_key = table.Column<string>(type: "text", nullable: false),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_hash = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    result_hash = table.Column<string>(type: "text", nullable: true),
                    offer_id = table.Column<long>(type: "bigint", nullable: true),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_offer_preparation_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_offer_preparation_requests_business_key",
                table: "offer_preparation_requests",
                column: "business_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_offer_preparation_requests_stage_created_at",
                table: "offer_preparation_requests",
                columns: new[] { "stage", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE offer_preparation_requests IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                DECLARE storage text; has_work boolean;
                BEGIN
                  IF EXISTS (SELECT 1 FROM offer_preparation_requests)
                    OR EXISTS (SELECT 1 FROM offers WHERE publication_id IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM settings WHERE key = 'bot.conversation' AND value LIKE '%"offerRequestId"%') THEN
                    RAISE EXCEPTION 'Cannot roll back offer preparation while roots, source or dialogue evidence remain';
                  END IF;
                  FOREACH storage IN ARRAY ARRAY[
                    'jobfinder_queues.wolverine_queue_hiring_prepare', 'jobfinder_queues.wolverine_queue_hiring_prepare_scheduled',
                    'jobfinder_queues.wolverine_queue_preparation', 'jobfinder_queues.wolverine_queue_preparation_scheduled',
                    'jobfinder_messages.wolverine_incoming_envelopes', 'jobfinder_messages.wolverine_outgoing_envelopes',
                    'jobfinder_messages.wolverine_dead_letters'] LOOP
                    IF EXISTS (SELECT FROM pg_class WHERE oid = to_regclass(storage)) THEN
                      EXECUTE format('SELECT EXISTS (SELECT 1 FROM %s WHERE message_type LIKE ''jobfinder.hiring.analyze-offer.%%'' OR message_type LIKE ''jobfinder.hiring.publish-offer.%%'' OR message_type LIKE ''%%AnalyzeOffer%%'' OR message_type LIKE ''%%PublishOffer%%'')', storage) INTO STRICT has_work;
                      IF has_work THEN RAISE EXCEPTION 'Cannot roll back offer preparation while native envelopes remain'; END IF;
                    END IF;
                  END LOOP;
                END
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "offer_preparation_requests");
        }
    }
}
