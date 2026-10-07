using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class HhResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hh_responses",
                columns: table => new
                {
                    hh_vacancy_id = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    vacancy_name = table.Column<string>(type: "text", nullable: true),
                    company_name = table.Column<string>(type: "text", nullable: true),
                    fingerprint = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    viewed_by_employer = table.Column<bool>(type: "boolean", nullable: false),
                    has_unread = table.Column<bool>(type: "boolean", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: true),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hh_responses", x => x.hh_vacancy_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hh_responses_fingerprint",
                table: "hh_responses",
                column: "fingerprint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hh_responses");
        }
    }
}
