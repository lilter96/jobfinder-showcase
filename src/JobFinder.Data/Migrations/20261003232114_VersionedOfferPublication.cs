using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class VersionedOfferPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "input_json",
                table: "offers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "publication_hash",
                table: "offers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "publication_id",
                table: "offers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_offers_publication_id",
                table: "offers",
                column: "publication_id",
                unique: true,
                filter: "publication_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE offers IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                    IF EXISTS (SELECT 1 FROM offers WHERE publication_id IS NOT NULL OR publication_hash IS NOT NULL OR input_json IS NOT NULL) THEN
                        RAISE EXCEPTION 'offer rollback refused: retain/export captured publication history before downgrade'
                            USING ERRCODE = 'P0001';
                    END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropIndex(
                name: "ix_offers_publication_id",
                table: "offers");

            migrationBuilder.DropColumn(
                name: "input_json",
                table: "offers");

            migrationBuilder.DropColumn(
                name: "publication_hash",
                table: "offers");

            migrationBuilder.DropColumn(
                name: "publication_id",
                table: "offers");
        }
    }
}
