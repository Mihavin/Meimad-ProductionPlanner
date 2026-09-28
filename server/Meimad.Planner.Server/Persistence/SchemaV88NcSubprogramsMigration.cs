using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Subprogram files of an NC release (owner decision 2026-09-28): a release may carry the files its
/// main program calls (M98 / G65). They are stored beside the main program, immutable like it, and a
/// Production Package copies them unchanged. The release also records the called programs it does
/// not include (for example probing macros kept on the machine).
/// </summary>
internal sealed class SchemaV88NcSubprogramsMigration : IDatabaseMigration
{
    public int Version => 88;

    public string Name => "nc_subprograms";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE gcode_release_subprograms (
                id TEXT PRIMARY KEY,
                gcode_release_id TEXT NOT NULL,
                position INTEGER NOT NULL CHECK (position >= 0),
                original_file_name TEXT NOT NULL CHECK (length(trim(original_file_name)) > 0),
                program_number INTEGER NULL CHECK (program_number IS NULL OR program_number > 0),
                stored_relative_path TEXT NOT NULL UNIQUE CHECK (length(trim(stored_relative_path)) > 0),
                file_size INTEGER NOT NULL CHECK (file_size > 0),
                file_hash TEXT NOT NULL CHECK (length(file_hash) = 64),
                UNIQUE (gcode_release_id, position),
                UNIQUE (gcode_release_id, original_file_name COLLATE NOCASE),
                UNIQUE (gcode_release_id, program_number),
                FOREIGN KEY (gcode_release_id) REFERENCES gcode_releases (id) ON DELETE RESTRICT
            );
            CREATE TRIGGER gcode_release_subprograms_immutable_update
            BEFORE UPDATE ON gcode_release_subprograms
            BEGIN
                SELECT RAISE(ABORT, 'G-code release subprograms are immutable');
            END;
            CREATE TRIGGER gcode_release_subprograms_immutable_delete
            BEFORE DELETE ON gcode_release_subprograms
            BEGIN
                SELECT RAISE(ABORT, 'G-code release subprograms are immutable');
            END;

            -- Program numbers the release's programs call without including them, as a JSON array.
            ALTER TABLE gcode_releases ADD COLUMN missing_subprogram_calls_json TEXT NOT NULL DEFAULT '[]';

            -- The artifact type check is part of the table definition, so the table is rebuilt to
            -- admit subprogram files.
            DROP TRIGGER production_package_artifacts_immutable_update;
            DROP TRIGGER production_package_artifacts_immutable_delete;
            CREATE TABLE production_package_artifacts_v88 (
                id TEXT PRIMARY KEY,
                production_package_id TEXT NOT NULL,
                artifact_type TEXT NOT NULL CHECK (artifact_type IN (
                    'RUNNABLE_NC','NC_SUBPROGRAM','TOOL_TABLE','OFFSET_LOADER','MANUAL_SETUP','MANIFEST',
                    'TOOL_OFFSETS','TOOL_OFFSET_PROGRAM')),
                logical_path TEXT NOT NULL,
                stored_relative_path TEXT NOT NULL UNIQUE,
                file_size INTEGER NOT NULL CHECK (file_size>0),
                file_hash TEXT NOT NULL CHECK (length(file_hash)=64),
                source_release_id TEXT,
                UNIQUE(production_package_id,logical_path),
                FOREIGN KEY (production_package_id) REFERENCES production_packages(id) ON DELETE RESTRICT
            );
            INSERT INTO production_package_artifacts_v88 (
                id, production_package_id, artifact_type, logical_path, stored_relative_path,
                file_size, file_hash, source_release_id)
            SELECT id, production_package_id, artifact_type, logical_path, stored_relative_path,
                   file_size, file_hash, source_release_id
            FROM production_package_artifacts;
            DROP TABLE production_package_artifacts;
            ALTER TABLE production_package_artifacts_v88 RENAME TO production_package_artifacts;
            CREATE TRIGGER production_package_artifacts_immutable_update BEFORE UPDATE ON production_package_artifacts
            BEGIN SELECT RAISE(ABORT,'Production Package artifacts are immutable'); END;
            CREATE TRIGGER production_package_artifacts_immutable_delete BEFORE DELETE ON production_package_artifacts
            BEGIN SELECT RAISE(ABORT,'Production Package artifacts are immutable'); END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
