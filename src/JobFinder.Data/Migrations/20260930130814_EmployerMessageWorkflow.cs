using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmployerMessageWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_delivery_attempts_application_id_application_revision",
                table: "delivery_attempts");

            migrationBuilder.AddColumn<bool>(
                name: "fresh",
                table: "outreaches",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "revision",
                table: "outreaches",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AlterColumn<long>(
                name: "application_id",
                table: "delivery_attempts",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<Guid>(
                name: "message_id",
                table: "delivery_attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "revision",
                table: "application_events",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "employer_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_key = table.Column<string>(type: "text", nullable: false),
                    vacancy_id = table.Column<long>(type: "bigint", nullable: false),
                    application_id = table.Column<long>(type: "bigint", nullable: true),
                    input_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    claim_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dispatch_envelope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    review = table.Column<string>(type: "jsonb", nullable: true),
                    tailored_resume_id = table.Column<long>(type: "bigint", nullable: true),
                    resume_hash = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_employer_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_application_id_application_revision",
                table: "delivery_attempts",
                columns: new[] { "application_id", "application_revision" },
                unique: true,
                filter: "message_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_message_id",
                table: "delivery_attempts",
                column: "message_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_employer_messages_business_key",
                table: "employer_messages",
                column: "business_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_employer_messages_vacancy_id_status",
                table: "employer_messages",
                columns: new[] { "vacancy_id", "status" });

            migrationBuilder.AddForeignKey(
                name: "fk_delivery_attempts_employer_messages_message_id",
                table: "delivery_attempts",
                column: "message_id",
                principalTable: "employer_messages",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $migration$
                BEGIN
                    IF EXISTS (SELECT 1 FROM employer_messages) THEN
                        RAISE EXCEPTION 'Employer message history must be preserved before rollback';
                    END IF;
                END $migration$;
                """);
            migrationBuilder.DropForeignKey(
                name: "fk_delivery_attempts_employer_messages_message_id",
                table: "delivery_attempts");

            migrationBuilder.DropTable(
                name: "employer_messages");

            migrationBuilder.DropIndex(
                name: "ix_delivery_attempts_application_id_application_revision",
                table: "delivery_attempts");

            migrationBuilder.DropIndex(
                name: "ix_delivery_attempts_message_id",
                table: "delivery_attempts");

            migrationBuilder.DropColumn(
                name: "fresh",
                table: "outreaches");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "outreaches");

            migrationBuilder.DropColumn(
                name: "message_id",
                table: "delivery_attempts");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "application_events");

            migrationBuilder.AlterColumn<long>(
                name: "application_id",
                table: "delivery_attempts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_application_id_application_revision",
                table: "delivery_attempts",
                columns: new[] { "application_id", "application_revision" },
                unique: true);
        }
    }
}
