using System.Globalization;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.EventLogging;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteCncVerificationFoundationRepository(SqliteDatabase database)
    : ICncVerificationFoundationRepository
{
    public async Task<OffsetLoaderRelease> CreateOffsetLoaderReleaseAsync(
        string runId, CreateOffsetLoaderRelease command, int releaseToken,
        DateTimeOffset createdAt, EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await ValidateReleaseContextAsync(connection, transaction, runId, command, token);
        releaseToken = await FindAvailableReleaseTokenAsync(
            connection, transaction, command.MachineId.Trim(), releaseToken, token);
        var id = Guid.NewGuid().ToString("N");
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO offset_loader_releases (
                    id, production_run_id, machine_id, nc_release_id,
                    tool_table_release_id, verification_release_token,
                    artifact_hash, created_at, created_by, metadata_json)
                VALUES ($id,$runId,$machineId,$ncReleaseId,$toolReleaseId,
                        $releaseToken,$artifactHash,$createdAt,$createdBy,$metadata);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$runId", runId);
            insert.Parameters.AddWithValue("$machineId", command.MachineId.Trim());
            insert.Parameters.AddWithValue("$ncReleaseId", command.NcReleaseId.Trim());
            insert.Parameters.AddWithValue("$toolReleaseId", command.ToolTableReleaseId.Trim());
            insert.Parameters.AddWithValue("$releaseToken", releaseToken);
            insert.Parameters.AddWithValue("$artifactHash", Db(command.ArtifactHash?.ToLowerInvariant()));
            insert.Parameters.AddWithValue("$createdAt", Format(createdAt));
            insert.Parameters.AddWithValue("$createdBy", actor);
            insert.Parameters.AddWithValue("$metadata", command.MetadataJson);
            await insert.ExecuteNonQueryAsync(token);
        }
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                INSERT INTO production_run_current_offset_loaders (
                    production_run_id, machine_id, offset_loader_release_id,
                    selected_at, selected_by, version)
                VALUES ($runId,$machineId,$releaseId,$at,$actor,1)
                ON CONFLICT(production_run_id) DO UPDATE SET
                    machine_id=excluded.machine_id,
                    offset_loader_release_id=excluded.offset_loader_release_id,
                    selected_at=excluded.selected_at,
                    selected_by=excluded.selected_by,
                    version=production_run_current_offset_loaders.version+1;
                """;
            current.Parameters.AddWithValue("$runId", runId);
            current.Parameters.AddWithValue("$machineId", command.MachineId.Trim());
            current.Parameters.AddWithValue("$releaseId", id);
            current.Parameters.AddWithValue("$at", Format(createdAt));
            current.Parameters.AddWithValue("$actor", actor);
            await current.ExecuteNonQueryAsync(token);
        }
        await using (var invalidate = connection.CreateCommand())
        {
            invalidate.Transaction = transaction;
            invalidate.CommandText = """
                UPDATE cnc_setup_verification_sessions
                SET state='SUPERSEDED', resolved_at=$at
                WHERE machine_id=$machineId AND state IN ('PENDING','SUCCEEDED');
                """;
            invalidate.Parameters.AddWithValue("$at", Format(createdAt));
            invalidate.Parameters.AddWithValue("$machineId", command.MachineId.Trim());
            await invalidate.ExecuteNonQueryAsync(token);
        }
        await SqliteStructuredEventLogRepository.AppendAsync(
            connection, transaction,
            new("offset_loader_release_created", createdAt, actor,
                new Dictionary<string, string>
                {
                    ["productionRunId"] = runId,
                    ["machineId"] = command.MachineId.Trim(),
                    ["offsetLoaderReleaseId"] = id,
                    ["ncReleaseId"] = command.NcReleaseId.Trim()
                }, null, null, null,
                new { verificationReleaseToken = releaseToken }), token);
        await transaction.CommitAsync(token);
        return new(id, runId, command.MachineId.Trim(), command.NcReleaseId.Trim(),
            command.ToolTableReleaseId.Trim(), releaseToken,
            command.ArtifactHash?.ToLowerInvariant(), createdAt.ToUniversalTime(), actor,
            command.MetadataJson, true);
    }

    public async Task<IReadOnlyList<OffsetLoaderRelease>> ListOffsetLoaderReleasesAsync(
        string runId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT release.id, release.production_run_id, release.machine_id,
                   release.nc_release_id, release.tool_table_release_id,
                   release.verification_release_token, release.artifact_hash,
                   release.created_at, release.created_by, release.metadata_json,
                   CASE WHEN current.offset_loader_release_id = release.id THEN 1 ELSE 0 END
            FROM offset_loader_releases release
            LEFT JOIN production_run_current_offset_loaders current
              ON current.production_run_id = release.production_run_id
            WHERE release.production_run_id = $runId
            ORDER BY release.created_at DESC, release.id DESC;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        var values = new List<OffsetLoaderRelease>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values.Add(ReadRelease(reader));
        return values;
    }

    public async Task<StoredCncVerificationSettings?> GetSettingsAsync(
        string machineId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = SettingsSelect + " WHERE machine_id=$machineId;";
        command.Parameters.AddWithValue("$machineId", machineId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadSettings(reader) : null;
    }

    public async Task<StoredCncVerificationSettings> UpsertSettingsAsync(
        StoredCncVerificationSettings value, int expectedVersion,
        EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (expectedVersion == 0)
        {
            command.CommandText = """
                INSERT INTO cnc_verification_settings (
                    machine_id,dprint_transport,dprint_port,challenge_program_number,
                    verify_program_number,custom_gcode_alias,nonce_variable,response_variable,
                    verification_state_variable,release_token_variable,protected_secret,
                    expected_macro_version,response_code_digits,verification_timeout_seconds,
                    enabled,version,created_at,updated_at)
                SELECT $machineId,$transport,$port,$challenge,$verify,$alias,$nonce,$response,
                       $state,$releaseToken,$secret,$macroVersion,$digits,$timeout,$enabled,
                       1,$createdAt,$updatedAt
                WHERE EXISTS(SELECT 1 FROM machines WHERE id=$machineId);
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE cnc_verification_settings SET
                    dprint_transport=$transport,dprint_port=$port,
                    challenge_program_number=$challenge,verify_program_number=$verify,
                    custom_gcode_alias=$alias,nonce_variable=$nonce,response_variable=$response,
                    verification_state_variable=$state,release_token_variable=$releaseToken,
                    protected_secret=$secret,expected_macro_version=$macroVersion,
                    response_code_digits=$digits,verification_timeout_seconds=$timeout,
                    enabled=$enabled,version=version+1,updated_at=$updatedAt
                WHERE machine_id=$machineId AND version=$expectedVersion;
                """;
        }
        AddSettings(command, value, expectedVersion);
        var changed = await command.ExecuteNonQueryAsync(token);
        if (changed != 1)
        {
            await transaction.RollbackAsync(token);
            if (expectedVersion == 0 && !await MachineExistsAsync(connection, value.MachineId, token))
                throw new CncVerificationTargetException("machine_not_found", "Machine was not found.");
            throw new CncVerificationConcurrencyException();
        }
        await SqliteStructuredEventLogRepository.AppendAsync(
            connection, transaction,
            new("cnc_verification_configuration_updated", value.UpdatedAt, actor,
                new Dictionary<string, string> { ["machineId"] = value.MachineId },
                null, null, null,
                new
                {
                    value.DprintTransport,
                    value.DprintPort,
                    value.ChallengeProgramNumber,
                    value.VerifyProgramNumber,
                    value.CustomGcodeAlias,
                    value.NonceVariable,
                    value.ResponseVariable,
                    value.VerificationStateVariable,
                    value.ReleaseTokenVariable,
                    value.ExpectedMacroVersion,
                    value.ResponseCodeDigits,
                    value.VerificationTimeoutSeconds,
                    value.Enabled,
                    secretConfigured = true
                }), token);
        await transaction.CommitAsync(token);
        return value with { Version = expectedVersion + 1 };
    }

    public async Task<CncDprintIngestionContext?> ResolveCurrentOffsetLoaderAsync(
        string machineId, int releaseToken, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT release.production_run_id, release.machine_id, release.id,
                   release.nc_release_id, release.verification_release_token,
                   settings.expected_macro_version, hook.nc_identity_token,
                   settings.response_code_digits, settings.verification_timeout_seconds
            FROM production_run_current_offset_loaders current
            JOIN offset_loader_releases release
              ON release.id=current.offset_loader_release_id
            JOIN cnc_verification_settings settings
              ON settings.machine_id=release.machine_id AND settings.enabled=1
            JOIN gcode_release_verification_hooks hook
              ON hook.gcode_release_id=release.nc_release_id
            WHERE release.machine_id=$machineId
              AND release.verification_release_token=$releaseToken
              AND ((hook.invocation_kind='G65'
                    AND hook.invocation_number=settings.verify_program_number)
                   OR (hook.invocation_kind='CUSTOM_GCODE'
                       AND hook.invocation_number=settings.custom_gcode_alias));
            """;
        command.Parameters.AddWithValue("$machineId", machineId);
        command.Parameters.AddWithValue("$releaseToken", releaseToken);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(6), reader.GetInt32(4), reader.GetInt32(5),
                reader.GetInt32(7), reader.GetInt32(8))
            : null;
    }

    public async Task<CncPendingVerificationContext?> ResolvePendingVerificationAsync(
        string machineId,
        string sourceEventId,
        DateTimeOffset detectedAt,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = """
                SELECT session.id,session.production_run_id,session.machine_id,
                       session.offset_loader_release_id,session.nc_release_id,
                       hook.nc_identity_token,session.macro_version,
                       settings.expected_macro_version,session.expires_at,session.state
                FROM production_run_workflow_events event
                JOIN cnc_setup_verification_sessions session
                  ON session.resolution_workflow_event_id=event.id
                JOIN cnc_verification_settings settings
                  ON settings.machine_id=session.machine_id
                JOIN gcode_release_verification_hooks hook
                  ON hook.gcode_release_id=session.nc_release_id
                WHERE event.source=$source AND event.source_event_id=$sourceEventId;
                """;
            duplicate.Parameters.AddWithValue("$source", $"HAAS_DPRINT:{machineId}".ToUpperInvariant());
            duplicate.Parameters.AddWithValue("$sourceEventId", sourceEventId);
            await using var reader = await duplicate.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                var existing = new CncPendingVerificationContext(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                    reader.GetInt32(6), reader.GetInt32(7), Parse(reader.GetString(8)),
                    reader.GetString(9), true);
                await transaction.CommitAsync(token);
                return existing;
            }
        }
        await using (var expire = connection.CreateCommand())
        {
            expire.Transaction = transaction;
            expire.CommandText = """
                UPDATE cnc_setup_verification_sessions
                SET state='EXPIRED',resolved_at=$at
                WHERE machine_id=$machineId AND state='PENDING' AND expires_at<=$at;
                """;
            expire.Parameters.AddWithValue("$at", Format(detectedAt));
            expire.Parameters.AddWithValue("$machineId", machineId);
            await expire.ExecuteNonQueryAsync(token);
        }

        CncPendingVerificationContext? result = null;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT session.id,session.production_run_id,session.machine_id,
                       session.offset_loader_release_id,session.nc_release_id,
                       hook.nc_identity_token,session.macro_version,
                       settings.expected_macro_version,session.expires_at
                FROM cnc_setup_verification_sessions session
                JOIN cnc_verification_settings settings
                  ON settings.machine_id=session.machine_id
                JOIN gcode_release_verification_hooks hook
                  ON hook.gcode_release_id=session.nc_release_id
                JOIN production_run_current_offset_loaders current
                  ON current.production_run_id=session.production_run_id
                 AND current.machine_id=session.machine_id
                 AND current.offset_loader_release_id=session.offset_loader_release_id
                WHERE session.machine_id=$machineId AND session.state='PENDING'
                  AND settings.enabled=1
                ORDER BY session.created_at DESC,session.id DESC LIMIT 1;
                """;
            query.Parameters.AddWithValue("$machineId", machineId);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
                result = new(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                    reader.GetInt32(6), reader.GetInt32(7), Parse(reader.GetString(8)),
                    "PENDING", false);
        }
        if (result is null)
        {
            await using var latest = connection.CreateCommand();
            latest.Transaction = transaction;
            latest.CommandText = """
                SELECT session.id,session.production_run_id,session.machine_id,
                       session.offset_loader_release_id,session.nc_release_id,
                       hook.nc_identity_token,session.macro_version,
                       settings.expected_macro_version,session.expires_at,session.state
                FROM cnc_setup_verification_sessions session
                JOIN cnc_verification_settings settings
                  ON settings.machine_id=session.machine_id
                JOIN gcode_release_verification_hooks hook
                  ON hook.gcode_release_id=session.nc_release_id
                WHERE session.machine_id=$machineId AND session.state<>'PENDING'
                ORDER BY session.created_at DESC,session.id DESC LIMIT 1;
                """;
            latest.Parameters.AddWithValue("$machineId", machineId);
            await using var reader = await latest.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
                result = new(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                    reader.GetInt32(6), reader.GetInt32(7), Parse(reader.GetString(8)),
                    reader.GetString(9), false);
        }
        await transaction.CommitAsync(token);
        return result;
    }

    public async Task<CncRecoveryResult> InvalidateVerificationAsync(
        string productionRunId,
        string machineId,
        string reason,
        DateTimeOffset performedAt,
        EditAuthority authority,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        string? sessionId;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT id FROM cnc_setup_verification_sessions
                WHERE production_run_id=$runId AND machine_id=$machineId
                  AND state IN('PENDING','SUCCEEDED')
                ORDER BY created_at DESC,id DESC LIMIT 1;
                """;
            query.Parameters.AddWithValue("$runId", productionRunId);
            query.Parameters.AddWithValue("$machineId", machineId);
            sessionId = await query.ExecuteScalarAsync(token) as string;
        }
        if (sessionId is null)
            throw new CncVerificationTargetException(
                "verification_session_not_active",
                "No pending or successful setup-verification session is active for this Machine and Production Run.");

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE cnc_setup_verification_sessions
                SET state='SUPERSEDED',resolved_at=$at
                WHERE id=$id AND state IN('PENDING','SUCCEEDED');
                """;
            update.Parameters.AddWithValue("$at", Format(performedAt));
            update.Parameters.AddWithValue("$id", sessionId);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                throw new CncVerificationTargetException(
                    "verification_session_not_active",
                    "The setup-verification session is no longer active.");
        }
        await SqliteStructuredEventLogRepository.AppendAsync(
            connection, transaction,
            new("cnc_verification_session_invalidated", performedAt, actor,
                new Dictionary<string, string>
                {
                    ["productionRunId"] = productionRunId,
                    ["machineId"] = machineId,
                    ["verificationSessionId"] = sessionId
                }, "authorized_recovery", reason), token);
        await transaction.CommitAsync(token);
        return new(
            "INVALIDATE_VERIFICATION", productionRunId, machineId,
            sessionId, null, reason, actor, performedAt);
    }

    public async Task<CncRecoveryResult> RevokeCurrentOffsetLoaderAsync(
        string productionRunId,
        string machineId,
        string reason,
        DateTimeOffset performedAt,
        EditAuthority authority,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var actor = await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        string? releaseId;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT offset_loader_release_id
                FROM production_run_current_offset_loaders
                WHERE production_run_id=$runId AND machine_id=$machineId;
                """;
            query.Parameters.AddWithValue("$runId", productionRunId);
            query.Parameters.AddWithValue("$machineId", machineId);
            releaseId = await query.ExecuteScalarAsync(token) as string;
        }
        if (releaseId is null)
            throw new CncVerificationTargetException(
                "current_offset_loader_not_found",
                "No current Offset Loader release exists for this Machine and Production Run.");

        await using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction;
            revoke.CommandText = """
                DELETE FROM production_run_current_offset_loaders
                WHERE production_run_id=$runId AND machine_id=$machineId
                  AND offset_loader_release_id=$releaseId;
                UPDATE cnc_setup_verification_sessions
                SET state='SUPERSEDED',resolved_at=$at
                WHERE production_run_id=$runId AND machine_id=$machineId
                  AND state IN('PENDING','SUCCEEDED');
                """;
            revoke.Parameters.AddWithValue("$runId", productionRunId);
            revoke.Parameters.AddWithValue("$machineId", machineId);
            revoke.Parameters.AddWithValue("$releaseId", releaseId);
            revoke.Parameters.AddWithValue("$at", Format(performedAt));
            await revoke.ExecuteNonQueryAsync(token);
        }
        await SqliteStructuredEventLogRepository.AppendAsync(
            connection, transaction,
            new("current_offset_loader_revoked", performedAt, actor,
                new Dictionary<string, string>
                {
                    ["productionRunId"] = productionRunId,
                    ["machineId"] = machineId,
                    ["offsetLoaderReleaseId"] = releaseId
                }, "authorized_recovery", reason), token);
        await transaction.CommitAsync(token);
        return new(
            "REVOKE_CURRENT_OFFSET_LOADER", productionRunId, machineId,
            null, releaseId, reason, actor, performedAt);
    }

    private static async Task ValidateReleaseContextAsync(
        SqliteConnection connection, SqliteTransaction transaction, string runId,
        CreateOffsetLoaderRelease value, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM machine_assignments assignment
                JOIN production_run_programs program ON program.production_run_id=assignment.production_run_id
                JOIN gcode_releases release ON release.id=$ncReleaseId
                JOIN gcode_release_verification_hooks hook ON hook.gcode_release_id=release.id
                WHERE assignment.production_run_id=$runId
                  AND assignment.machine_id=$machineId
                  AND (program.selected_gcode_release_id=release.id
                       OR program.production_gcode_release_id=release.id)
                  AND release.tool_table_release_id=$toolReleaseId);
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$machineId", value.MachineId.Trim());
        command.Parameters.AddWithValue("$ncReleaseId", value.NcReleaseId.Trim());
        command.Parameters.AddWithValue("$toolReleaseId", value.ToolTableReleaseId.Trim());
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 1)
            throw new CncVerificationTargetException("offset_loader_context_invalid",
                "The Machine, Production Run, hook-eligible approved NC release, and tool measurement release do not form one current run context.");
    }

    private static async Task<int> FindAvailableReleaseTokenAsync(
        SqliteConnection connection, SqliteTransaction transaction, string machineId,
        int proposedToken, CancellationToken token)
    {
        const int minimum = 100000;
        const int maximum = 999999;
        var candidate = proposedToken;
        for (var attempts = 0; attempts <= maximum - minimum; attempts++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM offset_loader_releases
                    WHERE machine_id=$machineId AND verification_release_token=$token);
                """;
            command.Parameters.AddWithValue("$machineId", machineId);
            command.Parameters.AddWithValue("$token", candidate);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 0)
                return candidate;
            candidate = candidate == maximum ? minimum : candidate + 1;
        }

        throw new CncVerificationTargetException("offset_loader_token_space_exhausted",
            "No verification release token remains available for this Machine.");
    }

    private static async Task<string> EnsureEditAuthorityAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        EditAuthority authority, CancellationToken token)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(connection, transaction, DateTimeOffset.UtcNow, token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id,holder_user_id,generation FROM edit_tokens WHERE id=1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0))
            throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        if (reader.GetString(0) != authority.ClientId || reader.GetInt64(2) != authority.Generation)
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
        return reader.IsDBNull(1) ? authority.ClientId : reader.GetString(1);
    }

    private static async Task<bool> MachineExistsAsync(SqliteConnection connection, string machineId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM machines WHERE id=$id);";
        command.Parameters.AddWithValue("$id", machineId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
    }

    private static OffsetLoaderRelease ReadRelease(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6),
        Parse(reader.GetString(7)), reader.GetString(8), reader.GetString(9), reader.GetInt32(10) == 1);

    private const string SettingsSelect = """
        SELECT machine_id,dprint_transport,dprint_port,challenge_program_number,
               verify_program_number,custom_gcode_alias,nonce_variable,response_variable,
               verification_state_variable,release_token_variable,protected_secret,
               expected_macro_version,response_code_digits,verification_timeout_seconds,
               enabled,version,created_at,updated_at FROM cnc_verification_settings
        """;

    private static StoredCncVerificationSettings ReadSettings(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
        reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.GetInt32(6),
        reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetString(10),
        reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14) == 1,
        reader.GetInt32(15), Parse(reader.GetString(16)), Parse(reader.GetString(17)));

    private static void AddSettings(SqliteCommand command, StoredCncVerificationSettings value, int expectedVersion)
    {
        command.Parameters.AddWithValue("$machineId", value.MachineId);
        command.Parameters.AddWithValue("$transport", value.DprintTransport);
        command.Parameters.AddWithValue("$port", value.DprintPort);
        command.Parameters.AddWithValue("$challenge", value.ChallengeProgramNumber);
        command.Parameters.AddWithValue("$verify", value.VerifyProgramNumber);
        command.Parameters.AddWithValue("$alias", Db(value.CustomGcodeAlias));
        command.Parameters.AddWithValue("$nonce", value.NonceVariable);
        command.Parameters.AddWithValue("$response", value.ResponseVariable);
        command.Parameters.AddWithValue("$state", value.VerificationStateVariable);
        command.Parameters.AddWithValue("$releaseToken", value.ReleaseTokenVariable);
        command.Parameters.AddWithValue("$secret", value.ProtectedSecret);
        command.Parameters.AddWithValue("$macroVersion", value.ExpectedMacroVersion);
        command.Parameters.AddWithValue("$digits", value.ResponseCodeDigits);
        command.Parameters.AddWithValue("$timeout", value.VerificationTimeoutSeconds);
        command.Parameters.AddWithValue("$enabled", value.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", Format(value.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Format(value.UpdatedAt));
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
    }
    private static object Db(object? value) => value ?? DBNull.Value;
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
