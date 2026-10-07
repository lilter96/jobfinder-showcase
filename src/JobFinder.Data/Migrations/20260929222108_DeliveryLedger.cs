using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "operation_id",
                table: "applications",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<long>(
                name: "revision",
                table: "applications",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "delivery_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<long>(type: "bigint", nullable: false),
                    application_revision = table.Column<long>(type: "bigint", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    target = table.Column<string>(type: "text", nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    text_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    review = table.Column<string>(type: "jsonb", nullable: false),
                    tailored_resume_id = table.Column<long>(type: "bigint", nullable: true),
                    resume_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    resume_file_name = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    external_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_delivery_attempts_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_applications_operation_id",
                table: "applications",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_application_id_application_revision",
                table: "delivery_attempts",
                columns: new[] { "application_id", "application_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_status",
                table: "delivery_attempts",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_attempts");

            migrationBuilder.DropIndex(
                name: "ix_applications_operation_id",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "operation_id",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "applications");
        }
    }
}
