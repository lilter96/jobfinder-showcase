using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace JobFinder.Data.Migrations
{
    /// <inheritdoc />
    public partial class PinnedWolverineStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Storage.WolverineStorage.CreateSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $rollback$
                DECLARE
                    storage_table record;
                    has_rows boolean;
                BEGIN
                    FOR storage_table IN
                        SELECT table_schema, table_name FROM information_schema.tables
                        WHERE table_schema IN ('jobfinder_messages', 'jobfinder_queues') AND table_type = 'BASE TABLE'
                        ORDER BY table_schema, table_name
                    LOOP
                        EXECUTE format('LOCK TABLE %I.%I IN ACCESS EXCLUSIVE MODE', storage_table.table_schema, storage_table.table_name);
                    END LOOP;
                    FOR storage_table IN
                        SELECT table_schema, table_name FROM information_schema.tables
                        WHERE table_schema IN ('jobfinder_messages', 'jobfinder_queues') AND table_type = 'BASE TABLE'
                    LOOP
                        EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I.%I)', storage_table.table_schema, storage_table.table_name) INTO has_rows;
                        IF has_rows THEN
                            RAISE EXCEPTION 'Wolverine storage rollback refused: %.% contains data; stop producers/consumers and export/reconcile it first', storage_table.table_schema, storage_table.table_name
                                USING ERRCODE = 'P0001';
                        END IF;
                    END LOOP;
                END;
                $rollback$;
                DROP SCHEMA IF EXISTS jobfinder_queues CASCADE;
                DROP SCHEMA IF EXISTS jobfinder_messages CASCADE;
                """);
        }
    }
}
