using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Stores the accepted immutable generic verification hook for newly approved NC releases.</summary>
internal sealed class SchemaV51NcVerificationHookMigration : IDatabaseMigration
{
    public int Version => 51;
    public string Name => "nc_verification_hook";

    public async Task ApplyAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE gcode_release_verification_hooks (
                gcode_release_id TEXT PRIMARY KEY,
                hook_version INTEGER NOT NULL CHECK (hook_version > 0),
                invocation_kind TEXT NOT NULL CHECK (invocation_kind IN ('G65','CUSTOM_GCODE')),
                invocation_number INTEGER NOT NULL CHECK (
                    (invocation_kind = 'G65' AND invocation_number BETWEEN 9000 AND 9999)
                    OR (invocation_kind = 'CUSTOM_GCODE' AND invocation_number BETWEEN 1 AND 999 AND invocation_number <> 65)),
                nc_identity_token INTEGER NOT NULL UNIQUE CHECK (nc_identity_token BETWEEN 100000 AND 999999),
                line_number INTEGER NOT NULL CHECK (line_number > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                FOREIGN KEY (gcode_release_id) REFERENCES gcode_releases(id) ON DELETE RESTRICT
            );
            CREATE TRIGGER gcode_release_verification_hooks_immutable_update
            BEFORE UPDATE ON gcode_release_verification_hooks
            BEGIN SELECT RAISE(ABORT, 'NC verification hooks are immutable'); END;
            CREATE TRIGGER gcode_release_verification_hooks_immutable_delete
            BEFORE DELETE ON gcode_release_verification_hooks
            BEGIN SELECT RAISE(ABORT, 'NC verification hooks are immutable'); END;
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
