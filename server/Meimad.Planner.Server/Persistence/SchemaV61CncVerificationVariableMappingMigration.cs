using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>Rejects nonpersistent handshake mappings and legacy/global alias collisions.</summary>
internal sealed class SchemaV61CncVerificationVariableMappingMigration : IDatabaseMigration
{
    public int Version => 61;
    public string Name => "cnc_verification_persistent_variable_mappings";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP TRIGGER cnc_verification_settings_v6_mappings_insert;
            DROP TRIGGER cnc_verification_settings_v6_mappings_update;

            UPDATE cnc_verification_settings
            SET enabled=0
            WHERE (finalize_program_number IS NOT NULL
                   OR event_sequence_variable IS NOT NULL)
              AND (
                     finalize_program_number IS NULL
                  OR event_sequence_variable IS NULL
                  OR finalize_program_number = challenge_program_number
                  OR finalize_program_number = verify_program_number
                  OR nonce_variable NOT BETWEEN 10000 AND 10999
                  OR verification_state_variable NOT BETWEEN 10000 AND 10999
                  OR release_token_variable NOT BETWEEN 10000 AND 10999
                  OR event_sequence_variable NOT BETWEEN 10000 AND 10999
                  OR NOT (
                         response_variable BETWEEN 500 AND 549
                      OR response_variable BETWEEN 10500 AND 10549)
                  OR nonce_variable IN (
                         CASE WHEN response_variable BETWEEN 500 AND 549
                              THEN response_variable + 10000
                              ELSE response_variable END,
                         verification_state_variable,
                         release_token_variable,
                         event_sequence_variable)
                  OR verification_state_variable IN (
                         CASE WHEN response_variable BETWEEN 500 AND 549
                              THEN response_variable + 10000
                              ELSE response_variable END,
                         release_token_variable,
                         event_sequence_variable)
                  OR release_token_variable IN (
                         CASE WHEN response_variable BETWEEN 500 AND 549
                              THEN response_variable + 10000
                              ELSE response_variable END,
                         event_sequence_variable)
                  OR event_sequence_variable =
                         CASE WHEN response_variable BETWEEN 500 AND 549
                              THEN response_variable + 10000
                              ELSE response_variable END
              );

            CREATE TRIGGER cnc_verification_settings_v6_mappings_insert
            BEFORE INSERT ON cnc_verification_settings
            WHEN (NEW.finalize_program_number IS NOT NULL
                  OR NEW.event_sequence_variable IS NOT NULL)
             AND (
                    NEW.finalize_program_number IS NULL
                 OR NEW.event_sequence_variable IS NULL
                 OR NEW.finalize_program_number = NEW.challenge_program_number
                 OR NEW.finalize_program_number = NEW.verify_program_number
                 OR NEW.nonce_variable NOT BETWEEN 10000 AND 10999
                 OR NEW.verification_state_variable NOT BETWEEN 10000 AND 10999
                 OR NEW.release_token_variable NOT BETWEEN 10000 AND 10999
                 OR NEW.event_sequence_variable NOT BETWEEN 10000 AND 10999
                 OR NOT (
                        NEW.response_variable BETWEEN 500 AND 549
                     OR NEW.response_variable BETWEEN 10500 AND 10549)
                 OR NEW.nonce_variable IN (
                        CASE WHEN NEW.response_variable BETWEEN 500 AND 549
                             THEN NEW.response_variable + 10000
                             ELSE NEW.response_variable END,
                        NEW.verification_state_variable,
                        NEW.release_token_variable,
                        NEW.event_sequence_variable)
                 OR NEW.verification_state_variable IN (
                        CASE WHEN NEW.response_variable BETWEEN 500 AND 549
                             THEN NEW.response_variable + 10000
                             ELSE NEW.response_variable END,
                        NEW.release_token_variable,
                        NEW.event_sequence_variable)
                 OR NEW.release_token_variable IN (
                        CASE WHEN NEW.response_variable BETWEEN 500 AND 549
                             THEN NEW.response_variable + 10000
                             ELSE NEW.response_variable END,
                        NEW.event_sequence_variable)
                 OR NEW.event_sequence_variable =
                        CASE WHEN NEW.response_variable BETWEEN 500 AND 549
                             THEN NEW.response_variable + 10000
                             ELSE NEW.response_variable END
             )
            BEGIN
                SELECT RAISE(ABORT, 'CNC verification mappings are nonpersistent, aliased, colliding, or incomplete');
            END;

            CREATE TRIGGER cnc_verification_settings_v6_mappings_update
            BEFORE UPDATE ON cnc_verification_settings
            WHEN (NEW.finalize_program_number IS NOT NULL
                  OR NEW.event_sequence_variable IS NOT NULL)
             AND (
                    NEW.finalize_program_number IS NULL
                 OR NEW.event_sequence_variable IS NULL
                 OR NEW.finalize_program_number = NEW.challenge_program_number
                 OR NEW.finalize_program_number = NEW.verify_program_number
                 OR NEW.nonce_variable NOT BETWEEN 10000 AND 10999
                 OR NEW.verification_state_variable NOT BETWEEN 10000 AND 10999
                 OR NEW.release_token_variable NOT BETWEEN 10000 AND 10999
                 OR NEW.event_sequence_variable NOT BETWEEN 10000 AND 10999
                 OR NOT (
                        NEW.response_variable BETWEEN 500 AND 549
                     OR NEW.response_variable BETWEEN 10500 AND 10549)
                 OR NEW.nonce_variable IN (
                        CASE WHEN NEW.response_variable BETWEEN 500 AND 549
                             THEN NEW.response_variable + 10000
                             ELSE NEW.response_variable END,
                        NEW.verification_state_variable,
                        NEW.release_token_variable,
                        NEW.event_sequence_variable)
                 OR NEW.verification_state_variable IN (
                        CASE WHEN NEW.response_variable BETWEEN 500 AND 549
                             THEN NEW.response_variable + 10000
                             ELSE NEW.response_variable END,
                        NEW.release_token_variable,
                        NEW.event_sequence_variable)
                 OR NEW.release_token_variable IN (
                        CASE WHEN NEW.response_variable BETWEEN 500 AND 549
                             THEN NEW.response_variable + 10000
                             ELSE NEW.response_variable END,
                        NEW.event_sequence_variable)
                 OR NEW.event_sequence_variable =
                        CASE WHEN NEW.response_variable BETWEEN 500 AND 549
                             THEN NEW.response_variable + 10000
                             ELSE NEW.response_variable END
             )
            BEGIN
                SELECT RAISE(ABORT, 'CNC verification mappings are nonpersistent, aliased, colliding, or incomplete');
            END;
            """;
        await command.ExecuteNonQueryAsync(token);
    }
}
