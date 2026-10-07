using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class GeneratedAnswers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "generated_answers",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    question_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    deps = table.Column<List<string>>(type: "text[]", nullable: false),
                    deps_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    hits = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generated_answers", x => x.key);
                });

            migrationBuilder.CreateIndex(
                name: "ix_generated_answers_last_used_at",
                table: "generated_answers",
                column: "last_used_at");

            migrationBuilder.CreateIndex(
                name: "ix_generated_answers_scope_question_id",
                table: "generated_answers",
                columns: new[] { "scope", "question_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "generated_answers");
        }
    }
}
