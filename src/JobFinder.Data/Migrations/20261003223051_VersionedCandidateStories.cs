using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class VersionedCandidateStories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "creation_hash",
                table: "stories",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "creation_key",
                table: "stories",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "retired_at",
                table: "stories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "revision",
                table: "stories",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "story_revisions",
                columns: table => new
                {
                    story_id = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: true),
                    after_json = table.Column<string>(type: "jsonb", nullable: false),
                    change = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_story_revisions", x => new { x.story_id, x.revision });
                });

            migrationBuilder.CreateIndex(
                name: "ix_stories_creation_key",
                table: "stories",
                column: "creation_key",
                unique: true,
                filter: "creation_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE stories, story_revisions IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM story_revisions)
                        OR EXISTS (SELECT 1 FROM stories WHERE revision <> 0 OR retired_at IS NOT NULL OR creation_key IS NOT NULL OR creation_hash IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot downgrade retained candidate story history or retirement decisions';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "story_revisions");

            migrationBuilder.DropIndex(
                name: "ix_stories_creation_key",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "creation_hash",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "creation_key",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "retired_at",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "stories");
        }
    }
}
