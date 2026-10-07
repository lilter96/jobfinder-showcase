using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class OwnedScrapeLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE settings IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM settings WHERE key LIKE 'scrape.state.%' OR key LIKE 'scrape.owner.%') THEN
                    RAISE EXCEPTION 'Cannot downgrade retained owned scrape ledger state';
                  END IF;
                END
                $rollback$;
                """);
        }
    }
}
