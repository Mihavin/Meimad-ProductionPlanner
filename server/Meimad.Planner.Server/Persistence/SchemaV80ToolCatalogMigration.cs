using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// The tool catalog and the enlarged tool type list. `catalog_tools` holds the factory's tool
/// definitions with a stable internal number/code (`MT-00001`, assigned once and never reused),
/// type, hand, dimensions, attributes and an active flag; `catalog_tool_external_ids` holds their
/// ids in other systems (supplier, ERP, CAM, presetter, ...). The catalog describes tools; it is
/// not an inventory and stores no quantities or locations. `tool_preparation_tools` is rebuilt so
/// its type check admits the turning, grooving, threading and hole-making families, and gains
/// the holder hand and an optional restrictive link to the catalog tool the Tool Room picked;
/// `tool_preparation_components` is rebuilt with it so its foreign key follows.
/// </summary>
internal sealed class SchemaV80ToolCatalogMigration : IDatabaseMigration
{
    internal const string ToolTypeList =
        "'END_MILL', 'BALL_END_MILL', 'BULL_NOSE_END_MILL', 'CHAMFER_MILL', 'FACE_MILL', 'SLOT_MILL', 'T_SLOT_MILL', " +
        "'THREAD_MILL', 'DOVETAIL_MILL', 'LOLLIPOP_MILL', 'ENGRAVER', " +
        "'DRILL', 'SPOT_DRILL', 'CENTER_DRILL', 'TAP', 'REAMER', 'BORING_HEAD', 'COUNTERSINK', 'COUNTERBORE', " +
        "'TURNING_TOOL', 'BORING_BAR', 'EXTERNAL_GROOVING', 'INTERNAL_GROOVING', 'FACE_GROOVING', 'PARTING', " +
        "'EXTERNAL_THREADING', 'INTERNAL_THREADING', 'PROBE', 'OTHER'";

    public int Version => 80;
    public string Name => "tool_catalog";

    // The rebuilt prepared-tool table is referenced by its components: dropping the old table
    // must not cascade into them.
    public bool DisablesForeignKeyEnforcement => true;

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            CREATE TABLE catalog_tools (
                id TEXT PRIMARY KEY,
                internal_number INTEGER NOT NULL UNIQUE CHECK (internal_number > 0),
                internal_code TEXT NOT NULL UNIQUE CHECK (length(internal_code) BETWEEN 1 AND 40),
                name TEXT NOT NULL CHECK (length(trim(name)) BETWEEN 1 AND 200),
                tool_type TEXT NOT NULL CHECK (tool_type IN ({ToolTypeList})),
                hand TEXT NULL CHECK (hand IS NULL OR hand IN ('RIGHT', 'LEFT', 'NEUTRAL')),
                description TEXT NULL CHECK (description IS NULL OR length(description) <= 2000),
                shape_json TEXT NOT NULL CHECK (json_valid(shape_json)),
                attributes_json TEXT NOT NULL CHECK (json_valid(attributes_json)),
                is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
                version INTEGER NOT NULL CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL CHECK (length(trim(updated_by)) > 0)
            );

            CREATE INDEX ix_catalog_tools_name ON catalog_tools (name COLLATE NOCASE);

            CREATE TABLE catalog_tool_external_ids (
                id TEXT PRIMARY KEY,
                catalog_tool_id TEXT NOT NULL
                    REFERENCES catalog_tools(id) ON DELETE CASCADE,
                system TEXT NOT NULL CHECK (length(trim(system)) BETWEEN 1 AND 60),
                value TEXT NOT NULL CHECK (length(trim(value)) BETWEEN 1 AND 200),
                UNIQUE (catalog_tool_id, system, value)
            );

            CREATE INDEX ix_catalog_tool_external_ids_value ON catalog_tool_external_ids (value COLLATE NOCASE);

            DROP TRIGGER tool_preparation_tools_immutable_update;
            DROP TRIGGER tool_preparation_components_immutable_update;

            CREATE TABLE tool_preparation_tools_v80 (
                id TEXT PRIMARY KEY,
                tool_preparation_id TEXT NOT NULL
                    REFERENCES tool_preparations(id) ON DELETE CASCADE,
                row_number INTEGER NOT NULL CHECK (row_number > 0),
                tool_identifier TEXT NOT NULL CHECK (length(trim(tool_identifier)) BETWEEN 1 AND 80),
                offset_number INTEGER NULL CHECK (offset_number IS NULL OR offset_number BETWEEN 1 AND 9999),
                measured_length REAL NULL CHECK (measured_length IS NULL OR measured_length >= 0),
                measured_diameter REAL NULL CHECK (measured_diameter IS NULL OR measured_diameter >= 0),
                shape_type TEXT NOT NULL CHECK (shape_type IN ({ToolTypeList})),
                shape_json TEXT NOT NULL CHECK (json_valid(shape_json)),
                notes TEXT NULL CHECK (notes IS NULL OR length(notes) <= 1000),
                hand TEXT NULL CHECK (hand IS NULL OR hand IN ('RIGHT', 'LEFT', 'NEUTRAL')),
                catalog_tool_id TEXT NULL
                    REFERENCES catalog_tools(id) ON DELETE RESTRICT,
                UNIQUE (tool_preparation_id, row_number),
                UNIQUE (tool_preparation_id, tool_identifier)
            );

            INSERT INTO tool_preparation_tools_v80 (
                id, tool_preparation_id, row_number, tool_identifier, offset_number,
                measured_length, measured_diameter, shape_type, shape_json, notes)
            SELECT id, tool_preparation_id, row_number, tool_identifier, offset_number,
                   measured_length, measured_diameter, shape_type, shape_json, notes
            FROM tool_preparation_tools;

            CREATE TABLE tool_preparation_components_v80 (
                id TEXT PRIMARY KEY,
                tool_preparation_tool_id TEXT NOT NULL
                    REFERENCES tool_preparation_tools_v80(id) ON DELETE CASCADE,
                sequence INTEGER NOT NULL CHECK (sequence > 0),
                component_type TEXT NOT NULL CHECK (component_type IN (
                    'HOLDER', 'EXTENSION', 'COLLET', 'ARBOR', 'SHANK', 'CUTTER', 'INSERT', 'OTHER')),
                name TEXT NOT NULL CHECK (length(trim(name)) BETWEEN 1 AND 120),
                catalog_number TEXT NULL CHECK (catalog_number IS NULL OR length(catalog_number) <= 120),
                length REAL NULL CHECK (length IS NULL OR length >= 0),
                diameter REAL NULL CHECK (diameter IS NULL OR diameter >= 0),
                notes TEXT NULL CHECK (notes IS NULL OR length(notes) <= 500),
                UNIQUE (tool_preparation_tool_id, sequence)
            );

            INSERT INTO tool_preparation_components_v80 (
                id, tool_preparation_tool_id, sequence, component_type, name, catalog_number, length, diameter, notes)
            SELECT id, tool_preparation_tool_id, sequence, component_type, name, catalog_number, length, diameter, notes
            FROM tool_preparation_components;

            DROP TABLE tool_preparation_components;
            DROP TABLE tool_preparation_tools;
            ALTER TABLE tool_preparation_tools_v80 RENAME TO tool_preparation_tools;
            ALTER TABLE tool_preparation_components_v80 RENAME TO tool_preparation_components;

            CREATE INDEX ix_tool_preparation_tools_catalog_tool
                ON tool_preparation_tools (catalog_tool_id);

            CREATE TRIGGER tool_preparation_tools_immutable_update
            BEFORE UPDATE ON tool_preparation_tools
            BEGIN
                SELECT RAISE(ABORT, 'tool_preparation_tools are immutable');
            END;

            CREATE TRIGGER tool_preparation_components_immutable_update
            BEFORE UPDATE ON tool_preparation_components
            BEGIN
                SELECT RAISE(ABORT, 'tool_preparation_components are immutable');
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
