using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class LetterEditRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "letter_revision",
                table: "vacancies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE vacancies IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM vacancies WHERE letter_revision <> 0) THEN
                    RAISE EXCEPTION 'Cannot roll back letter revision while edited source witnesses remain';
                  END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropColumn(
                name: "letter_revision",
                table: "vacancies");
        }
    }
}
