using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmployerReviewDependency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "letter_review_request_id",
                table: "employer_messages",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE employer_messages, letter_preparation_requests IN ACCESS EXCLUSIVE MODE;
                DO $rollback$
                BEGIN
                  IF EXISTS (SELECT 1 FROM employer_messages WHERE letter_review_request_id IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM letter_preparation_requests WHERE input_json->>'version' = '2') THEN
                    RAISE EXCEPTION 'Cannot roll back exact-text review while requests or dependencies remain';
                  END IF;
                END;
                $rollback$;
                """);
            migrationBuilder.DropColumn(
                name: "letter_review_request_id",
                table: "employer_messages");
        }
    }
}
