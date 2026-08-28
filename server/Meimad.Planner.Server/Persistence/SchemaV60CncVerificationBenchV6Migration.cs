using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Adds explicit controller mappings required by the reboot-safe verification bench candidate.</summary>
internal sealed class SchemaV60CncVerificationBenchV6Migration : IDatabaseMigration
{
    public int Version => 60;
    public string Name => "cnc_verification_bench_v6_mappings";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE cnc_verification_settings
            ADD COLUMN finalize_program_number INTEGER
                CHECK (finalize_program_number IS NULL
                    OR finalize_program_number BETWEEN 9000 AND 9999);

            ALTER TABLE cnc_verification_settings
            ADD COLUMN event_sequence_variable INTEGER
                CHECK (event_sequence_variable IS NULL
                    OR event_sequence_variable BETWEEN 10000 AND 10999);

            CREATE TRIGGER cnc_verification_settings_v6_mappings_insert
            BEFORE INSERT ON cnc_verification_settings
            WHEN NEW.finalize_program_number IS NOT NULL
             AND (
                    NEW.finalize_program_number = NEW.challenge_program_number
                 OR NEW.finalize_program_number = NEW.verify_program_number
                 OR NEW.event_sequence_variable IS NULL
                 OR NEW.event_sequence_variable IN (
                        NEW.nonce_variable,
                        NEW.response_variable,
                        NEW.verification_state_variable,
                        NEW.release_token_variable)
             )
            BEGIN
                SELECT RAISE(ABORT, 'CNC verification v6 mappings collide or are incomplete');
            END;

            CREATE TRIGGER cnc_verification_settings_v6_mappings_update
            BEFORE UPDATE ON cnc_verification_settings
            WHEN NEW.finalize_program_number IS NOT NULL
             AND (
                    NEW.finalize_program_number = NEW.challenge_program_number
                 OR NEW.finalize_program_number = NEW.verify_program_number
                 OR NEW.event_sequence_variable IS NULL
                 OR NEW.event_sequence_variable IN (
                        NEW.nonce_variable,
                        NEW.response_variable,
                        NEW.verification_state_variable,
                        NEW.release_token_variable)
             )
            BEGIN
                SELECT RAISE(ABORT, 'CNC verification v6 mappings collide or are incomplete');
            END;
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
