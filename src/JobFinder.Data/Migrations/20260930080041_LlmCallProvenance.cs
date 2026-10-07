using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class LlmCallProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempt_number",
                table: "llm_calls",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "call_id",
                table: "llm_calls",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "operation_id",
                table: "llm_calls",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recipe",
                table: "llm_calls",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_model",
                table: "llm_calls",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_llm_calls_call_id_attempt_number",
                table: "llm_calls",
                columns: new[] { "call_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_llm_calls_operation_id_at",
                table: "llm_calls",
                columns: new[] { "operation_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_llm_calls_call_id_attempt_number",
                table: "llm_calls");

            migrationBuilder.DropIndex(
                name: "ix_llm_calls_operation_id_at",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "attempt_number",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "call_id",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "operation_id",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "recipe",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "requested_model",
                table: "llm_calls");
        }
    }
}
