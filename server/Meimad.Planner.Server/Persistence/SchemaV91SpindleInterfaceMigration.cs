using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// The spindle side of a milling tool (owner decisions 2026-09-29): a Setup library of spindle
/// adaptors (taper length, gauge-line diameter, taper small-end diameter and the tool-changer
/// flange TCD × TCL below the gauge line) and pull studs (length above the taper end, knob, neck
/// and pilot diameters, thread and angle); a default adaptor and pull stud per Machine; and a
/// per-tool override on each saved Tool Room tool. The library starts with BT40 and the HAAS BT40
/// 45° M16 pull stud.
/// </summary>
internal sealed class SchemaV91SpindleInterfaceMigration : IDatabaseMigration
{
    public int Version => 91;

    public string Name => "spindle_interface_library";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE spindle_adaptors (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (length(trim(name)) BETWEEN 1 AND 80),
                taper_length REAL NOT NULL CHECK (taper_length > 0 AND taper_length <= 1000),
                gauge_diameter REAL NOT NULL CHECK (gauge_diameter > 0 AND gauge_diameter <= 1000),
                small_end_diameter REAL NOT NULL CHECK (small_end_diameter > 0 AND small_end_diameter <= gauge_diameter),
                tool_changer_diameter REAL NOT NULL CHECK (tool_changer_diameter > 0 AND tool_changer_diameter <= 1000),
                tool_changer_length REAL NOT NULL CHECK (tool_changer_length >= 0 AND tool_changer_length <= 1000),
                notes TEXT CHECK (notes IS NULL OR length(notes) <= 500),
                is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL
            );

            CREATE TABLE pull_studs (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (length(trim(name)) BETWEEN 1 AND 80),
                thread TEXT CHECK (thread IS NULL OR length(thread) <= 40),
                angle REAL CHECK (angle IS NULL OR (angle >= 0 AND angle <= 180)),
                overall_length REAL CHECK (overall_length IS NULL OR (overall_length > 0 AND overall_length <= 1000)),
                exposed_length REAL NOT NULL CHECK (exposed_length > 0 AND exposed_length <= 1000),
                knob_diameter REAL NOT NULL CHECK (knob_diameter > 0 AND knob_diameter <= 1000),
                neck_diameter REAL CHECK (neck_diameter IS NULL OR (neck_diameter > 0 AND neck_diameter <= 1000)),
                pilot_diameter REAL CHECK (pilot_diameter IS NULL OR (pilot_diameter > 0 AND pilot_diameter <= 1000)),
                notes TEXT CHECK (notes IS NULL OR length(notes) <= 500),
                is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL
            );

            CREATE TABLE machine_spindle_interfaces (
                machine_id TEXT PRIMARY KEY REFERENCES machines(id) ON DELETE CASCADE,
                spindle_adaptor_id TEXT REFERENCES spindle_adaptors(id) ON DELETE RESTRICT,
                pull_stud_id TEXT REFERENCES pull_studs(id) ON DELETE RESTRICT,
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                updated_at TEXT NOT NULL,
                updated_by TEXT NOT NULL
            );

            ALTER TABLE tool_preparation_tools ADD COLUMN spindle_adaptor_id TEXT NULL
                REFERENCES spindle_adaptors(id) ON DELETE RESTRICT;
            ALTER TABLE tool_preparation_tools ADD COLUMN pull_stud_id TEXT NULL
                REFERENCES pull_studs(id) ON DELETE RESTRICT;

            INSERT INTO spindle_adaptors (
                id, name, taper_length, gauge_diameter, small_end_diameter,
                tool_changer_diameter, tool_changer_length, notes, updated_at, updated_by)
            VALUES ('bt40', 'BT40', 65.4, 44.45, 25.375, 63, 25,
                    '7:24 taper (MAS-403). Tool-changer flange simplified to a cylinder.',
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'system');
            INSERT INTO pull_studs (
                id, name, thread, angle, overall_length, exposed_length, knob_diameter,
                neck_diameter, pilot_diameter, notes, updated_at, updated_by)
            VALUES ('haas-bt40-45-m16', 'HAAS BT40 45° M16', 'M16x2.0', 45, 59.94, 27.94, 14.96, 9.96, 16.99,
                    'Retention knob for Haas BT40 spindles.',
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'system');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
