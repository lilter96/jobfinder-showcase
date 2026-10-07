using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmployerResumeDependency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "resume_request_id",
                table: "employer_messages",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $migration$
                BEGIN
                    IF EXISTS (SELECT 1 FROM employer_messages WHERE resume_request_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'Employer resume dependencies must be preserved before rollback';
                    END IF;
                END $migration$;
                """);
            migrationBuilder.DropColumn(
                name: "resume_request_id",
                table: "employer_messages");
        }
    }
}
