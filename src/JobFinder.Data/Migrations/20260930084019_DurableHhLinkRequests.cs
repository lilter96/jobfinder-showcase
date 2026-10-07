using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableHhLinkRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hh_link_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hh_id = table.Column<long>(type: "bigint", nullable: false),
                    contacts = table.Column<string>(type: "jsonb", nullable: false),
                    stage = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    vacancy_id = table.Column<long>(type: "bigint", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: true),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hh_link_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hh_link_requests_hh_id",
                table: "hh_link_requests",
                column: "hh_id",
                unique: true,
                filter: "stage IN ('Reading', 'Linking')");

            migrationBuilder.CreateIndex(
                name: "ix_hh_link_requests_stage_created_at",
                table: "hh_link_requests",
                columns: new[] { "stage", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE hh_link_requests IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                    IF EXISTS (SELECT 1 FROM hh_link_requests) THEN
                        RAISE EXCEPTION 'hh-link rollback refused: requests contain data; stop producers/consumers and export/reconcile them first'
                            USING ERRCODE = 'P0001';
                    END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "hh_link_requests");
        }
    }
}
