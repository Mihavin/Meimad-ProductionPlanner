using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV65GenericResourcePlanningMigration : IDatabaseMigration
{
    public int Version => 65;
    public string Name => "generic_resource_planning";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE skills (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE CHECK (length(trim(name)) BETWEEN 1 AND 120),
                description TEXT,
                is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version>0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE employee_skills (
                employee_resource_id TEXT NOT NULL,
                skill_id TEXT NOT NULL,
                assigned_at TEXT NOT NULL,
                assigned_by TEXT NOT NULL CHECK (length(trim(assigned_by))>0),
                PRIMARY KEY (employee_resource_id,skill_id),
                FOREIGN KEY (employee_resource_id) REFERENCES employee_resources(id) ON DELETE CASCADE,
                FOREIGN KEY (skill_id) REFERENCES skills(id) ON DELETE RESTRICT
            );

            CREATE TABLE workstation_types (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE CHECK (length(trim(name)) BETWEEN 1 AND 120),
                description TEXT,
                property_schema_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(property_schema_json)),
                is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version>0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE workstations (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE CHECK (length(trim(name)) BETWEEN 1 AND 120),
                workstation_type_id TEXT NOT NULL,
                working_calendar_id TEXT NOT NULL,
                capacity INTEGER NOT NULL DEFAULT 1 CHECK (capacity>0),
                capabilities_json TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(capabilities_json)),
                properties_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(properties_json)),
                is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version>0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (workstation_type_id) REFERENCES workstation_types(id) ON DELETE RESTRICT,
                FOREIGN KEY (working_calendar_id) REFERENCES working_calendars(id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_workstations_type_active ON workstations(workstation_type_id,is_active);

            CREATE TABLE external_resources (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE CHECK (length(trim(name)) BETWEEN 1 AND 160),
                supplier_name TEXT,
                promised_lead_time_minutes INTEGER NOT NULL CHECK (promised_lead_time_minutes>=0),
                safety_buffer_minutes INTEGER NOT NULL DEFAULT 0 CHECK (safety_buffer_minutes>=0),
                lead_time_semantics TEXT NOT NULL DEFAULT 'CALENDAR_TIME'
                    CHECK (lead_time_semantics IN ('CALENDAR_TIME','WORKING_TIME')),
                working_calendar_id TEXT,
                properties_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(properties_json)),
                is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version>0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (working_calendar_id) REFERENCES working_calendars(id) ON DELETE RESTRICT
            );

            CREATE TABLE operation_resource_requirements (
                id TEXT PRIMARY KEY,
                case_operation_id TEXT NOT NULL,
                sequence_position INTEGER NOT NULL CHECK (sequence_position>=0),
                resource_class TEXT NOT NULL CHECK (resource_class IN ('MACHINE','EMPLOYEE','WORKSTATION','EXTERNAL')),
                workstation_type_id TEXT,
                external_resource_id TEXT,
                required_capability TEXT,
                required_skill_id TEXT,
                capacity_required INTEGER NOT NULL DEFAULT 1 CHECK (capacity_required>0),
                estimated_duration_seconds INTEGER NOT NULL CHECK (estimated_duration_seconds>=0),
                direction TEXT NOT NULL DEFAULT 'FORWARD' CHECK (direction IN ('BACKWARD','FORWARD')),
                simultaneous_group_key TEXT,
                predecessor_requirement_id TEXT,
                is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version>0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (case_operation_id) REFERENCES case_operations(id) ON DELETE RESTRICT,
                FOREIGN KEY (workstation_type_id) REFERENCES workstation_types(id) ON DELETE RESTRICT,
                FOREIGN KEY (external_resource_id) REFERENCES external_resources(id) ON DELETE RESTRICT,
                FOREIGN KEY (required_skill_id) REFERENCES skills(id) ON DELETE RESTRICT,
                FOREIGN KEY (predecessor_requirement_id) REFERENCES operation_resource_requirements(id) ON DELETE RESTRICT,
                CHECK ((resource_class='WORKSTATION' AND workstation_type_id IS NOT NULL)
                    OR (resource_class<>'WORKSTATION' AND workstation_type_id IS NULL)),
                CHECK ((resource_class='EMPLOYEE' AND required_skill_id IS NOT NULL)
                    OR resource_class<>'EMPLOYEE'),
                CHECK ((resource_class='EXTERNAL' AND external_resource_id IS NOT NULL)
                    OR (resource_class<>'EXTERNAL' AND external_resource_id IS NULL)),
                UNIQUE(case_operation_id,sequence_position,id)
            );

            CREATE TABLE resource_schedule_work (
                id TEXT PRIMARY KEY,
                production_run_id TEXT,
                batch_operation_id TEXT NOT NULL,
                requirement_id TEXT NOT NULL,
                dependency_work_id TEXT,
                anchor_machine_assignment_id TEXT,
                requested_starts_at TEXT,
                required_finishes_at TEXT,
                required_delivery_at TEXT,
                planned_duration_seconds INTEGER NOT NULL CHECK (planned_duration_seconds>=0),
                state TEXT NOT NULL DEFAULT 'PROVISIONAL'
                    CHECK (state IN ('PROVISIONAL','PINNED','CONFIRMED','ACTUAL','CANCELLED')),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version>0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (production_run_id) REFERENCES production_runs(id) ON DELETE RESTRICT,
                FOREIGN KEY (batch_operation_id) REFERENCES batch_operations(id) ON DELETE RESTRICT,
                FOREIGN KEY (requirement_id) REFERENCES operation_resource_requirements(id) ON DELETE RESTRICT,
                FOREIGN KEY (dependency_work_id) REFERENCES resource_schedule_work(id) ON DELETE RESTRICT,
                FOREIGN KEY (anchor_machine_assignment_id) REFERENCES machine_assignments(id) ON DELETE RESTRICT
            );

            CREATE TABLE resource_schedule_assignments (
                id TEXT PRIMARY KEY,
                schedule_work_id TEXT NOT NULL,
                resource_class TEXT NOT NULL CHECK (resource_class IN ('MACHINE','EMPLOYEE','WORKSTATION','EXTERNAL')),
                machine_id TEXT,
                employee_resource_id TEXT,
                workstation_id TEXT,
                external_resource_id TEXT,
                planned_starts_at TEXT NOT NULL,
                planned_ends_at TEXT NOT NULL,
                planned_duration_seconds INTEGER NOT NULL CHECK (planned_duration_seconds>=0),
                is_pinned INTEGER NOT NULL DEFAULT 0 CHECK (is_pinned IN (0,1)),
                actual_resource_id TEXT,
                actual_starts_at TEXT,
                actual_ends_at TEXT,
                actual_duration_seconds INTEGER,
                assigned_by TEXT NOT NULL CHECK (length(trim(assigned_by))>0),
                assignment_reason TEXT NOT NULL,
                supersedes_assignment_id TEXT,
                created_at TEXT NOT NULL,
                FOREIGN KEY (schedule_work_id) REFERENCES resource_schedule_work(id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE RESTRICT,
                FOREIGN KEY (employee_resource_id) REFERENCES employee_resources(id) ON DELETE RESTRICT,
                FOREIGN KEY (workstation_id) REFERENCES workstations(id) ON DELETE RESTRICT,
                FOREIGN KEY (external_resource_id) REFERENCES external_resources(id) ON DELETE RESTRICT,
                FOREIGN KEY (supersedes_assignment_id) REFERENCES resource_schedule_assignments(id) ON DELETE RESTRICT,
                CHECK (planned_ends_at>=planned_starts_at),
                CHECK ((actual_starts_at IS NULL AND actual_ends_at IS NULL AND actual_duration_seconds IS NULL)
                    OR (actual_starts_at IS NOT NULL AND actual_ends_at IS NOT NULL AND actual_duration_seconds>=0)),
                CHECK ((resource_class='MACHINE' AND machine_id IS NOT NULL)
                    OR (resource_class='EMPLOYEE' AND employee_resource_id IS NOT NULL)
                    OR (resource_class='WORKSTATION' AND workstation_id IS NOT NULL)
                    OR (resource_class='EXTERNAL' AND external_resource_id IS NOT NULL))
            );
            CREATE INDEX ix_resource_assignments_work_time
                ON resource_schedule_assignments(schedule_work_id,planned_starts_at,planned_ends_at);
            CREATE INDEX ix_resource_assignments_employee_time
                ON resource_schedule_assignments(employee_resource_id,planned_starts_at,planned_ends_at);
            CREATE INDEX ix_resource_assignments_workstation_time
                ON resource_schedule_assignments(workstation_id,planned_starts_at,planned_ends_at);

            CREATE TABLE external_resource_executions (
                id TEXT PRIMARY KEY,
                schedule_work_id TEXT NOT NULL UNIQUE,
                external_resource_id TEXT NOT NULL,
                planned_send_at TEXT NOT NULL,
                planned_return_at TEXT NOT NULL,
                vendor_promised_return_at TEXT,
                actual_send_at TEXT,
                actual_return_at TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (schedule_work_id) REFERENCES resource_schedule_work(id) ON DELETE RESTRICT,
                FOREIGN KEY (external_resource_id) REFERENCES external_resources(id) ON DELETE RESTRICT
            );

            CREATE TABLE machine_package_capabilities (
                machine_id TEXT PRIMARY KEY,
                allow_manual_dummy_tool_offsets INTEGER NOT NULL DEFAULT 0 CHECK (allow_manual_dummy_tool_offsets IN (0,1)),
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL CHECK (length(trim(updated_by))>0),
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE CASCADE
            );

            INSERT INTO machine_package_capabilities(
                machine_id,allow_manual_dummy_tool_offsets,updated_at,updated_by)
            SELECT id,1,strftime('%Y-%m-%dT%H:%M:%fZ','now'),'schema-v65-pilot'
            FROM machines WHERE trim(number) IN ('10','14','15');

            ALTER TABLE production_packages ADD COLUMN tool_offset_mode TEXT NOT NULL DEFAULT 'MEASURED'
                CHECK (tool_offset_mode IN ('MEASURED','MANUAL_DUMMY'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
