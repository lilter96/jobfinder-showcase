using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class PolicyHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "policy_revisions",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    content = table.Column<string>(type: "json", nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_revisions", x => new { x.key, x.version });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "policy_revisions");
        }
    }
}
