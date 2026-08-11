using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV1Migration : IDatabaseMigration
{
    public int Version => 1;

    public string Name => "initial_planning_schema";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE cases (
                id TEXT PRIMARY KEY,
                part_number TEXT NOT NULL,
                revision TEXT,
                name TEXT NOT NULL,
                customer_reference TEXT,
                technical_data_json TEXT,
                material TEXT,
                raw_stock TEXT,
                working_folder_path TEXT NOT NULL,
                preview_reference TEXT,
                current_setup_seconds INTEGER CHECK (current_setup_seconds IS NULL OR current_setup_seconds >= 0),
                current_cycle_seconds INTEGER CHECK (current_cycle_seconds IS NULL OR current_cycle_seconds >= 0),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE TABLE working_calendars (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                time_zone_id TEXT NOT NULL,
                calendar_json TEXT NOT NULL DEFAULT '{}',
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE TABLE machines (
                id TEXT PRIMARY KEY,
                number TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                machine_type TEXT NOT NULL,
                capabilities_json TEXT NOT NULL DEFAULT '[]',
                working_calendar_id TEXT NOT NULL,
                display_configuration_json TEXT NOT NULL DEFAULT '{}',
                status TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (working_calendar_id) REFERENCES working_calendars (id) ON DELETE RESTRICT
            );

            CREATE TABLE orders (
                id TEXT PRIMARY KEY,
                case_id TEXT NOT NULL,
                order_reference TEXT NOT NULL,
                customer_reference TEXT,
                quantity INTEGER NOT NULL CHECK (quantity > 0),
                work_finish_date TEXT NOT NULL,
                status TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (case_id) REFERENCES cases (id) ON DELETE RESTRICT
            );

            CREATE TABLE case_operations (
                id TEXT PRIMARY KEY,
                case_id TEXT NOT NULL,
                operation_number INTEGER NOT NULL CHECK (operation_number > 0),
                route_position INTEGER NOT NULL CHECK (route_position >= 0),
                name TEXT NOT NULL,
                required_machine_type TEXT,
                setup_seconds INTEGER CHECK (setup_seconds IS NULL OR setup_seconds >= 0),
                cycle_seconds INTEGER CHECK (cycle_seconds IS NULL OR cycle_seconds >= 0),
                dependency_type TEXT NOT NULL DEFAULT 'independent'
                    CHECK (dependency_type IN ('sequential', 'parallel_capable', 'independent', 'locked_simultaneous')),
                predecessor_case_operation_id TEXT,
                simultaneous_group_key TEXT,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (case_id) REFERENCES cases (id) ON DELETE RESTRICT,
                FOREIGN KEY (predecessor_case_operation_id) REFERENCES case_operations (id) ON DELETE RESTRICT,
                UNIQUE (case_id, operation_number),
                UNIQUE (case_id, route_position)
            );

            CREATE TABLE production_batches (
                id TEXT PRIMARY KEY,
                case_id TEXT NOT NULL,
                batch_number TEXT NOT NULL,
                status TEXT NOT NULL,
                planned_quantity INTEGER NOT NULL CHECK (planned_quantity > 0),
                route_revision INTEGER CHECK (route_revision IS NULL OR route_revision > 0),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (case_id) REFERENCES cases (id) ON DELETE RESTRICT,
                UNIQUE (case_id, batch_number)
            );

            CREATE TABLE batch_allocations (
                id TEXT PRIMARY KEY,
                production_batch_id TEXT NOT NULL,
                allocation_type TEXT NOT NULL
                    CHECK (allocation_type IN ('order', 'stock', 'scrap_allowance')),
                order_id TEXT,
                quantity INTEGER NOT NULL CHECK (quantity > 0),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (production_batch_id) REFERENCES production_batches (id) ON DELETE RESTRICT,
                FOREIGN KEY (order_id) REFERENCES orders (id) ON DELETE RESTRICT,
                CHECK (
                    (allocation_type = 'order' AND order_id IS NOT NULL)
                    OR (allocation_type IN ('stock', 'scrap_allowance') AND order_id IS NULL)
                )
            );

            CREATE TABLE batch_operations (
                id TEXT PRIMARY KEY,
                production_batch_id TEXT NOT NULL,
                source_case_operation_id TEXT NOT NULL,
                operation_number INTEGER NOT NULL CHECK (operation_number > 0),
                route_position INTEGER NOT NULL CHECK (route_position >= 0),
                name TEXT NOT NULL,
                required_machine_type TEXT,
                setup_seconds INTEGER CHECK (setup_seconds IS NULL OR setup_seconds >= 0),
                cycle_seconds INTEGER CHECK (cycle_seconds IS NULL OR cycle_seconds >= 0),
                status TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (production_batch_id) REFERENCES production_batches (id) ON DELETE RESTRICT,
                FOREIGN KEY (source_case_operation_id) REFERENCES case_operations (id) ON DELETE RESTRICT,
                UNIQUE (production_batch_id, operation_number),
                UNIQUE (production_batch_id, route_position)
            );

            CREATE TABLE machine_assignments (
                id TEXT PRIMARY KEY,
                batch_operation_id TEXT NOT NULL UNIQUE,
                machine_id TEXT NOT NULL,
                backlog_position INTEGER NOT NULL CHECK (backlog_position >= 0),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (batch_operation_id) REFERENCES batch_operations (id) ON DELETE RESTRICT,
                FOREIGN KEY (machine_id) REFERENCES machines (id) ON DELETE RESTRICT,
                UNIQUE (machine_id, backlog_position)
            );

            CREATE TABLE downtimes (
                id TEXT PRIMARY KEY,
                machine_id TEXT NOT NULL,
                starts_at TEXT NOT NULL,
                ends_at TEXT NOT NULL,
                reason TEXT NOT NULL,
                status TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (machine_id) REFERENCES machines (id) ON DELETE RESTRICT,
                CHECK (ends_at > starts_at)
            );

            CREATE TABLE edit_tokens (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                holder_client_id TEXT,
                holder_user_id TEXT,
                generation INTEGER NOT NULL DEFAULT 0 CHECK (generation >= 0),
                acquired_at TEXT,
                lease_expires_at TEXT,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                CHECK (
                    (holder_client_id IS NULL AND holder_user_id IS NULL AND acquired_at IS NULL)
                    OR (holder_client_id IS NOT NULL AND holder_user_id IS NOT NULL AND acquired_at IS NOT NULL)
                )
            );

            CREATE TABLE application_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE TABLE device_registry (
                id TEXT PRIMARY KEY,
                device_type TEXT NOT NULL CHECK (device_type IN ('eink', 'tv')),
                device_name TEXT NOT NULL UNIQUE,
                machine_id TEXT,
                credential_hash TEXT,
                access_mode TEXT NOT NULL DEFAULT 'read_only' CHECK (access_mode = 'read_only'),
                is_enabled INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
                last_seen_at TEXT,
                metadata_json TEXT NOT NULL DEFAULT '{}',
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (machine_id) REFERENCES machines (id) ON DELETE SET NULL
            );

            CREATE INDEX ix_orders_case_id ON orders (case_id);
            CREATE INDEX ix_case_operations_case_route ON case_operations (case_id, route_position);
            CREATE INDEX ix_case_operations_predecessor ON case_operations (predecessor_case_operation_id);
            CREATE INDEX ix_production_batches_case_id ON production_batches (case_id);
            CREATE INDEX ix_batch_allocations_batch_id ON batch_allocations (production_batch_id);
            CREATE INDEX ix_batch_allocations_order_id ON batch_allocations (order_id);
            CREATE INDEX ix_batch_operations_batch_route ON batch_operations (production_batch_id, route_position);
            CREATE INDEX ix_batch_operations_source ON batch_operations (source_case_operation_id);
            CREATE INDEX ix_machines_calendar_id ON machines (working_calendar_id);
            CREATE INDEX ix_machine_assignments_machine_backlog ON machine_assignments (machine_id, backlog_position);
            CREATE INDEX ix_downtimes_machine_start ON downtimes (machine_id, starts_at);
            CREATE INDEX ix_device_registry_machine_id ON device_registry (machine_id);
            CREATE INDEX ix_device_registry_type_enabled ON device_registry (device_type, is_enabled);

            INSERT INTO edit_tokens (id) VALUES (1);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
