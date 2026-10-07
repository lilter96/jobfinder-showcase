using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class RetiredCompanyContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "retired_at",
                table: "company_contacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "revision",
                table: "company_contacts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM company_contacts WHERE revision <> 0 OR retired_at IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot downgrade retained contact revisions or retirement decisions';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "retired_at",
                table: "company_contacts");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "company_contacts");
        }
    }
}
