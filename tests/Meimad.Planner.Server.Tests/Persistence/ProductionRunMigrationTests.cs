using Meimad.Planner.Server.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.Persistence;

public sealed class ProductionRunMigrationTests
{
    [Fact]
    public async Task V46_wraps_assigned_operations_and_preserves_assignment_identity_order_mode_and_times()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                DROP TABLE production_run_cycle_events;
                DELETE FROM schema_migrations WHERE version = 47;
                DROP TRIGGER machine_assignment_run_contains_legacy_operation;
                DROP TRIGGER machine_assignment_run_required_update;
                DROP TRIGGER machine_assignment_wraps_legacy_operation;
                DROP TRIGGER production_run_structure_locked_output_delete;
                DROP TRIGGER production_run_structure_locked_output_update;
                DROP TRIGGER production_run_structure_locked_program_delete;
                DROP TRIGGER production_run_structure_locked_program_update;
                DROP TRIGGER production_run_structure_locked_program_insert;
                DROP TRIGGER production_run_output_no_overallocation_insert;
                DROP TRIGGER production_run_output_target_matches_cycles_insert;
                DROP TRIGGER machine_assignment_selected_release_matches_operation_insert;
                DROP TRIGGER machine_assignment_selected_release_matches_operation_update;
                DROP INDEX ix_machine_assignments_machine_backlog;
                DROP INDEX ix_machine_assignments_selected_gcode;
                DROP INDEX ix_machine_assignments_batch_operation_compatibility;
                PRAGMA legacy_alter_table = ON;
                ALTER TABLE machine_assignments RENAME TO machine_assignments_v46;
                CREATE TABLE machine_assignments (
                    id TEXT PRIMARY KEY,
                    batch_operation_id TEXT NOT NULL UNIQUE,
                    machine_id TEXT NOT NULL,
                    backlog_position INTEGER NOT NULL CHECK (backlog_position >= 0),
                    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    planning_mode TEXT NOT NULL DEFAULT 'manual'
                        CHECK (planning_mode IN ('forward','backward','manual')),
                    selected_gcode_release_id TEXT REFERENCES gcode_releases(id) ON DELETE RESTRICT,
                    FOREIGN KEY (batch_operation_id) REFERENCES batch_operations(id) ON DELETE RESTRICT,
                    FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                    UNIQUE (machine_id, backlog_position));
                DROP TABLE machine_assignments_v46;
                PRAGMA legacy_alter_table = OFF;
                CREATE INDEX ix_machine_assignments_machine_backlog
                    ON machine_assignments(machine_id, backlog_position);
                CREATE INDEX ix_machine_assignments_selected_gcode
                    ON machine_assignments(selected_gcode_release_id);
                CREATE TRIGGER machine_assignment_selected_release_matches_operation_insert
                BEFORE INSERT ON machine_assignments
                WHEN NEW.selected_gcode_release_id IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM gcode_releases release JOIN batch_operations operation
                      ON operation.id=NEW.batch_operation_id
                     AND operation.source_case_operation_id=release.case_operation_id
                    WHERE release.id=NEW.selected_gcode_release_id)
                BEGIN SELECT RAISE(ABORT, 'selected G-code release must belong to the assigned Batch Operation source'); END;
                CREATE TRIGGER machine_assignment_selected_release_matches_operation_update
                BEFORE UPDATE OF selected_gcode_release_id,batch_operation_id ON machine_assignments
                WHEN NEW.selected_gcode_release_id IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM gcode_releases release JOIN batch_operations operation
                      ON operation.id=NEW.batch_operation_id
                     AND operation.source_case_operation_id=release.case_operation_id
                    WHERE release.id=NEW.selected_gcode_release_id)
                BEGIN SELECT RAISE(ABORT, 'selected G-code release must belong to the assigned Batch Operation source'); END;
                DROP TABLE production_run_outputs;
                DROP TABLE production_run_programs;
                DROP TABLE production_runs;
                DELETE FROM schema_migrations WHERE version = 46;
                PRAGMA user_version = 45;

                INSERT INTO working_calendars (id, name, time_zone_id)
                VALUES ('run-calendar', 'Run calendar', 'UTC');
                INSERT INTO machines (
                    id, number, name, machine_type, working_calendar_id, status,
                    is_active, execution_mode, machine_time_factor)
                VALUES ('run-machine', 'RUN-M1', 'Run Machine', 'mill', 'run-calendar',
                        'active', 1, 'MANUAL', 1.0);
                INSERT INTO cases (id, part_number, name, working_folder_path)
                VALUES ('run-case', 'RUN-PART', 'Run Part', 'C:\Cases\RunPart');
                INSERT INTO case_operations (
                    id, case_id, operation_number, route_position, name, setup_seconds, cycle_seconds)
                VALUES
                    ('run-case-op-a', 'run-case', 10, 0, 'A', 60, 10),
                    ('run-case-op-b', 'run-case', 20, 1, 'B', 90, 20),
                    ('run-case-op-unassigned', 'run-case', 30, 2, 'Unassigned', 30, 5);
                INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
                VALUES
                    ('run-batch-a', 'run-case', 'RUN-A', 'in_production', 12),
                    ('run-batch-b', 'run-case', 'RUN-B', 'waiting', 7),
                    ('run-batch-u', 'run-case', 'RUN-U', 'waiting', 5);
                INSERT INTO batch_operations (
                    id, production_batch_id, source_case_operation_id,
                    operation_number, route_position, name, setup_seconds,
                    cycle_seconds, status, actual_start)
                VALUES
                    ('run-op-a', 'run-batch-a', 'run-case-op-a', 10, 0, 'A', 60, 10,
                     'in_progress', '2026-08-20T06:00:00Z'),
                    ('run-op-b', 'run-batch-b', 'run-case-op-b', 20, 0, 'B', 90, 20,
                     'not_started', NULL),
                    ('run-op-u', 'run-batch-u', 'run-case-op-unassigned', 30, 0, 'U', 30, 5,
                     'not_started', NULL);
                INSERT INTO machine_assignments (
                    id, batch_operation_id, machine_id, backlog_position, planning_mode,
                    version, created_at, updated_at)
                VALUES
                    ('run-assignment-a', 'run-op-a', 'run-machine', 0, 'forward', 4,
                     '2026-08-19T00:00:00Z', '2026-08-20T06:00:00Z'),
                    ('run-assignment-b', 'run-op-b', 'run-machine', 1, 'backward', 7,
                     '2026-08-19T01:00:00Z', '2026-08-20T01:00:00Z');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await new DatabaseMigrator(
            fixture.Database, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();

        await using var reopened = await fixture.Database.OpenConnectionAsync();
        await using var assertion = reopened.CreateCommand();
        assertion.CommandText = """
            SELECT assignment.id, assignment.production_run_id, assignment.backlog_position,
                   assignment.planning_mode, assignment.version, assignment.created_at,
                   assignment.updated_at, run.status, run.shared_setup_seconds,
                   program.target_cycle_count, output.target_quantity,
                   output.quantity_per_cycle, output.batch_operation_id
            FROM machine_assignments assignment
            JOIN production_runs run ON run.id = assignment.production_run_id
            JOIN production_run_programs program ON program.production_run_id = run.id
            JOIN production_run_outputs output ON output.production_run_program_id = program.id
            ORDER BY assignment.backlog_position;
            """;
        await using (var reader = await assertion.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("run-assignment-a", reader.GetString(0));
            Assert.Equal("run:assignment:run-assignment-a", reader.GetString(1));
            Assert.Equal(0, reader.GetInt32(2));
            Assert.Equal("forward", reader.GetString(3));
            Assert.Equal(4, reader.GetInt32(4));
            Assert.Equal("2026-08-19T00:00:00Z", reader.GetString(5));
            Assert.Equal("2026-08-20T06:00:00Z", reader.GetString(6));
            Assert.Equal("IN_PROGRESS", reader.GetString(7));
            Assert.Equal(60, reader.GetInt32(8));
            Assert.Equal(12, reader.GetInt32(9));
            Assert.Equal(12, reader.GetInt32(10));
            Assert.Equal(1, reader.GetInt32(11));
            Assert.Equal("run-op-a", reader.GetString(12));

            Assert.True(await reader.ReadAsync());
            Assert.Equal("run-assignment-b", reader.GetString(0));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.Equal("backward", reader.GetString(3));
            Assert.Equal(7, reader.GetInt32(4));
            Assert.Equal("PLANNED", reader.GetString(7));
            Assert.False(await reader.ReadAsync());
        }

        assertion.CommandText = "SELECT COUNT(*) FROM production_runs WHERE legacy_batch_operation_id = 'run-op-u';";
        Assert.Equal(0L, (long)(await assertion.ExecuteScalarAsync())!);
        assertion.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(await assertion.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Compatibility_assignment_creates_one_reusable_single_output_run()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id)
            VALUES ('compat-calendar', 'Compat calendar', 'UTC');
            INSERT INTO machines (
                id, number, name, machine_type, working_calendar_id, status,
                is_active, execution_mode, machine_time_factor)
            VALUES ('compat-machine', 'COMPAT-M1', 'Compat Machine', 'mill',
                    'compat-calendar', 'active', 1, 'MANUAL', 1.0);
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('compat-case', 'COMPAT', 'Compat', 'C:\Cases\Compat');
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
            VALUES ('compat-case-op', 'compat-case', 10, 0, 'Mill');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
            VALUES ('compat-batch', 'compat-case', 'COMPAT-B', 'waiting', 8);
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, status)
            VALUES ('compat-op', 'compat-batch', 'compat-case-op', 10, 0, 'Mill', 'not_started');
            INSERT INTO machine_assignments (
                id, batch_operation_id, machine_id, backlog_position, planning_mode)
            VALUES ('compat-assignment-1', 'compat-op', 'compat-machine', 0, 'manual');
            """;
        await command.ExecuteNonQueryAsync();

        command.CommandText = """
            SELECT assignment.production_run_id,
                   (SELECT COUNT(*) FROM production_runs WHERE legacy_batch_operation_id = 'compat-op'),
                   (SELECT COUNT(*) FROM production_run_programs program
                    JOIN production_runs run ON run.id = program.production_run_id
                    WHERE run.legacy_batch_operation_id = 'compat-op'),
                   (SELECT COUNT(*) FROM production_run_outputs output
                    WHERE output.batch_operation_id = 'compat-op')
            FROM machine_assignments assignment WHERE assignment.id = 'compat-assignment-1';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("run:batch-operation:compat-op", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
    }
}
