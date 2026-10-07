using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class CompanyPreferenceRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "preferences_revision",
                table: "companies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE companies IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM companies WHERE preferences_revision <> 0) THEN
                        RAISE EXCEPTION 'Cannot downgrade retained company preference revisions';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "preferences_revision",
                table: "companies");
        }
    }
}
