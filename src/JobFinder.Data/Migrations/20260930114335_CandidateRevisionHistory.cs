using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class CandidateRevisionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candidate_revisions",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    content = table.Column<string>(type: "json", nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candidate_revisions", x => new { x.key, x.version });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE candidate_revisions IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                    IF EXISTS (SELECT 1 FROM candidate_revisions) THEN
                        RAISE EXCEPTION 'candidate rollback refused: revision history contains data; stop writers and export/reconcile it first'
                            USING ERRCODE = 'P0001';
                    END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "candidate_revisions");
        }
    }
}
