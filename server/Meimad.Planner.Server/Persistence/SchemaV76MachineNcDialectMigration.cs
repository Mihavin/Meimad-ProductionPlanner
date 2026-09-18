using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Adds the Machine NC dialect (Haas NGC, FANUC custom macro B, Mazak Matrix EIA, Okuma OSP)
/// and makes the v61 verification-mapping triggers dialect-aware: the persistent and response
/// variable ranges depend on the control family, while distinct programs and non-colliding
/// variables remain required for every dialect. Existing Machines stay HAAS_NGC, so existing
/// verification rows keep exactly the v61 rule.
/// </summary>
internal sealed class SchemaV76MachineNcDialectMigration : IDatabaseMigration
{
    public int Version => 76;
    public string Name => "machine_nc_dialect";

    private const string Dialect =
        "COALESCE((SELECT nc_dialect FROM machines WHERE machines.id = NEW.machine_id), 'HAAS_NGC')";

    /// <summary>The Haas M109 legacy alias is only an alias on Haas; other controls compare the literal number.</summary>
    private const string CanonicalResponse =
        "CASE WHEN " + Dialect + " = 'HAAS_NGC' AND NEW.response_variable BETWEEN 500 AND 549 " +
        "THEN NEW.response_variable + 10000 ELSE NEW.response_variable END";

    private static string RangeViolation() => $"""
        CASE {Dialect}
            WHEN 'OKUMA_OSP' THEN NOT (
                   NEW.nonce_variable BETWEEN 1 AND 200
               AND NEW.verification_state_variable BETWEEN 1 AND 200
               AND NEW.release_token_variable BETWEEN 1 AND 200
               AND NEW.event_sequence_variable BETWEEN 1 AND 200
               AND NEW.response_variable BETWEEN 1 AND 200)
            WHEN 'FANUC_MACRO_B' THEN NOT (
                   NEW.nonce_variable BETWEEN 500 AND 999
               AND NEW.verification_state_variable BETWEEN 500 AND 999
               AND NEW.release_token_variable BETWEEN 500 AND 999
               AND NEW.event_sequence_variable BETWEEN 500 AND 999
               AND NEW.response_variable BETWEEN 500 AND 999)
            WHEN 'MAZAK_MATRIX_EIA' THEN NOT (
                   NEW.nonce_variable BETWEEN 500 AND 999
               AND NEW.verification_state_variable BETWEEN 500 AND 999
               AND NEW.release_token_variable BETWEEN 500 AND 999
               AND NEW.event_sequence_variable BETWEEN 500 AND 999
               AND NEW.response_variable BETWEEN 500 AND 999)
            ELSE NOT (
                   NEW.nonce_variable BETWEEN 10000 AND 10999
               AND NEW.verification_state_variable BETWEEN 10000 AND 10999
               AND NEW.release_token_variable BETWEEN 10000 AND 10999
               AND NEW.event_sequence_variable BETWEEN 10000 AND 10999
               AND (NEW.response_variable BETWEEN 500 AND 549
                    OR NEW.response_variable BETWEEN 10500 AND 10549))
        END
        """;

    private static string Trigger(string name, string operation) => $"""
        CREATE TRIGGER {name}
        BEFORE {operation} ON cnc_verification_settings
        WHEN (NEW.finalize_program_number IS NOT NULL
              OR NEW.event_sequence_variable IS NOT NULL)
         AND (
                NEW.finalize_program_number IS NULL
             OR NEW.event_sequence_variable IS NULL
             OR NEW.finalize_program_number = NEW.challenge_program_number
             OR NEW.finalize_program_number = NEW.verify_program_number
             OR ({RangeViolation()})
             OR NEW.nonce_variable IN (
                    {CanonicalResponse},
                    NEW.verification_state_variable,
                    NEW.release_token_variable,
                    NEW.event_sequence_variable)
             OR NEW.verification_state_variable IN (
                    {CanonicalResponse},
                    NEW.release_token_variable,
                    NEW.event_sequence_variable)
             OR NEW.release_token_variable IN (
                    {CanonicalResponse},
                    NEW.event_sequence_variable)
             OR NEW.event_sequence_variable = {CanonicalResponse}
         )
        BEGIN
            SELECT RAISE(ABORT, 'CNC verification mappings are nonpersistent for the Machine NC dialect, aliased, colliding, or incomplete');
        END;
        """;

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            ALTER TABLE machines ADD COLUMN nc_dialect TEXT NOT NULL DEFAULT 'HAAS_NGC'
                CHECK (nc_dialect IN ('HAAS_NGC', 'FANUC_MACRO_B', 'MAZAK_MATRIX_EIA', 'OKUMA_OSP'));

            DROP TRIGGER cnc_verification_settings_v6_mappings_insert;
            DROP TRIGGER cnc_verification_settings_v6_mappings_update;

            -- The v60 column CHECK pinned event_sequence_variable to the Haas persistent range.
            -- Rebuild the table with dialect-neutral column CHECKs; the per-dialect rule lives in
            -- the triggers below, which read the Machine's nc_dialect.
            CREATE TABLE cnc_verification_settings_v76 (
                machine_id TEXT PRIMARY KEY,
                dprint_transport TEXT NOT NULL CHECK (dprint_transport IN ('HAAS_DPRNT_TCP')),
                dprint_port INTEGER NOT NULL CHECK (dprint_port BETWEEN 1 AND 65535),
                challenge_program_number INTEGER NOT NULL CHECK (challenge_program_number BETWEEN 9000 AND 9999),
                verify_program_number INTEGER NOT NULL CHECK (verify_program_number BETWEEN 9000 AND 9999),
                custom_gcode_alias INTEGER CHECK (custom_gcode_alias IS NULL OR custom_gcode_alias BETWEEN 1 AND 999),
                nonce_variable INTEGER NOT NULL CHECK (nonce_variable BETWEEN 1 AND 10999),
                response_variable INTEGER NOT NULL CHECK (response_variable BETWEEN 1 AND 10999),
                verification_state_variable INTEGER NOT NULL CHECK (verification_state_variable BETWEEN 1 AND 10999),
                release_token_variable INTEGER NOT NULL CHECK (release_token_variable BETWEEN 1 AND 10999),
                expected_macro_version INTEGER NOT NULL CHECK (expected_macro_version > 0),
                response_code_digits INTEGER NOT NULL CHECK (response_code_digits BETWEEN 4 AND 6),
                verification_timeout_seconds INTEGER NOT NULL CHECK (verification_timeout_seconds BETWEEN 30 AND 3600),
                enabled INTEGER NOT NULL CHECK (enabled IN (0,1)),
                version INTEGER NOT NULL CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                finalize_program_number INTEGER
                    CHECK (finalize_program_number IS NULL OR finalize_program_number BETWEEN 9000 AND 9999),
                event_sequence_variable INTEGER
                    CHECK (event_sequence_variable IS NULL OR event_sequence_variable BETWEEN 1 AND 10999),
                FOREIGN KEY (machine_id) REFERENCES machines(id) ON DELETE CASCADE,
                CHECK (challenge_program_number <> verify_program_number),
                CHECK (nonce_variable <> response_variable
                    AND nonce_variable <> verification_state_variable
                    AND nonce_variable <> release_token_variable
                    AND response_variable <> verification_state_variable
                    AND response_variable <> release_token_variable
                    AND verification_state_variable <> release_token_variable)
            );
            INSERT INTO cnc_verification_settings_v76 (
                machine_id, dprint_transport, dprint_port, challenge_program_number, verify_program_number,
                custom_gcode_alias, nonce_variable, response_variable, verification_state_variable,
                release_token_variable, expected_macro_version, response_code_digits,
                verification_timeout_seconds, enabled, version, created_at, updated_at,
                finalize_program_number, event_sequence_variable)
            SELECT
                machine_id, dprint_transport, dprint_port, challenge_program_number, verify_program_number,
                custom_gcode_alias, nonce_variable, response_variable, verification_state_variable,
                release_token_variable, expected_macro_version, response_code_digits,
                verification_timeout_seconds, enabled, version, created_at, updated_at,
                finalize_program_number, event_sequence_variable
            FROM cnc_verification_settings;
            DROP TABLE cnc_verification_settings;
            ALTER TABLE cnc_verification_settings_v76 RENAME TO cnc_verification_settings;

            {Trigger("cnc_verification_settings_v6_mappings_insert", "INSERT")}

            {Trigger("cnc_verification_settings_v6_mappings_update", "UPDATE")}
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
