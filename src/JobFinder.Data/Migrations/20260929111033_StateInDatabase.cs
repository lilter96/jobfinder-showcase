using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class StateInDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_documents",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    content = table.Column<string>(type: "json", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_documents", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "telegram_sessions",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telegram_sessions", x => x.name);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_documents");

            migrationBuilder.DropTable(
                name: "data_protection_keys");

            migrationBuilder.DropTable(
                name: "telegram_sessions");
        }
    }
}
