using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class OwnedApplicationBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "application_block_revisions",
                columns: table => new
                {
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    before_value = table.Column<string>(type: "text", nullable: true),
                    value = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_block_revisions", x => x.revision);
                    table.ForeignKey(
                        name: "fk_application_block_revisions_delivery_attempts_attempt_id",
                        column: x => x.attempt_id,
                        principalTable: "delivery_attempts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_application_block_revisions_attempt_id",
                table: "application_block_revisions",
                column: "attempt_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE application_block_revisions, settings IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM application_block_revisions)
                    OR EXISTS (SELECT 1 FROM settings WHERE key = 'apply.blockedUntil.revision') THEN
                    RAISE EXCEPTION 'Cannot downgrade retained application block revisions or tombstones';
                  END IF;
                END
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "application_block_revisions");
        }
    }
}
