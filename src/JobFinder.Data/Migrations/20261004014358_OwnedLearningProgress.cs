using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class OwnedLearningProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "learning_progress_revisions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    scope = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: false),
                    after_json = table.Column<string>(type: "jsonb", nullable: false),
                    change = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_learning_progress_revisions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_learning_progress_revisions_scope_revision",
                table: "learning_progress_revisions",
                columns: new[] { "scope", "revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE learning_progress_revisions, settings IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM learning_progress_revisions)
                    OR EXISTS (SELECT 1 FROM settings WHERE key = 'hr.learning' OR key LIKE 'interview.%.learning') THEN
                    RAISE EXCEPTION 'Cannot downgrade retained learning progress history or ownership markers';
                  END IF;
                END
                $rollback$;
                """);
            migrationBuilder.DropTable(
                name: "learning_progress_revisions");
        }
    }
}
