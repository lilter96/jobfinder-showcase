using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DigestRunWitness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "manual_trigger",
                table: "job_runs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "scheduled_slot",
                table: "job_runs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM settings WHERE key = 'jobs.digest.publication') THEN
                    RAISE EXCEPTION 'Cannot roll back digest run witnesses while native publications exist';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "manual_trigger",
                table: "job_runs");

            migrationBuilder.DropColumn(
                name: "scheduled_slot",
                table: "job_runs");
        }
    }
}
