using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.Concurrency;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.MachineEvidence;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Measured work offsets (schema v116). Every write is one immediate transaction: an ingest checks
/// duplicates and sessions, matches, inserts and supersedes the previous current record together; a
/// review checks the candidate's version and the current record before it changes either.
/// </summary>
internal sealed class SqliteWorkOffsetRepository(SqliteDatabase database) : IWorkOffsetRepository
{
    private const string SelectRecord = """
        SELECT o.id, o.machine_id, m.number, m.name, o.case_operation_id, co.operation_number, co.name,
               o.program_release_id, h.nc_identity_token, pr.revision_number, p.name, r.post_specific_revision,
               CASE WHEN r.id IS NULL THEN NULL
                    WHEN pr.is_active = 1 AND NOT EXISTS (
                        SELECT 1 FROM gcode_releases newer
                        WHERE newer.process_revision_id = r.process_revision_id
                          AND newer.postprocessor_id = r.postprocessor_id
                          AND newer.post_specific_revision > r.post_specific_revision) THEN 1
                    ELSE 0 END,
               o.work_offset, o.x_mm, o.y_mm, o.z_mm, o.a_deg, o.b_deg, o.c_deg, o.units,
               o.source, o.source_session, o.source_sequence,
               o.claimed_machine, o.claimed_program, o.claimed_operation, o.claimed_release,
               o.raw_message, o.raw_message_hash, o.measured_at, o.received_at, o.received_by,
               o.status, o.status_reason, o.initial_status, o.supersedes_id,
               o.reviewed_by, o.reviewed_at, o.review_comment, o.version
        FROM machine_work_offsets o
        LEFT JOIN machines m ON m.id = o.machine_id
        LEFT JOIN case_operations co ON co.id = o.case_operation_id
        LEFT JOIN gcode_releases r ON r.id = o.program_release_id
        LEFT JOIN process_revisions pr ON pr.id = r.process_revision_id
        LEFT JOIN postprocessors p ON p.id = r.postprocessor_id
        LEFT JOIN gcode_release_verification_hooks h ON h.gcode_release_id = r.id
        """;

    private const string NewestFirst = " ORDER BY o.measured_at DESC, o.received_at DESC, o.rowid DESC";

    public async Task<IWorkOffsetIngestUnit> BeginIngestAsync(CancellationToken token)
    {
        var connection = await database.OpenConnectionAsync(token);
        try
        {
            var transaction = connection.BeginTransaction(deferred: false);
            return new IngestUnit(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<WorkOffsetRecord?> GetAsync(string workOffsetId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        return await ReadOneAsync(connection, null, " WHERE o.id = $id", token, ("$id", workOffsetId));
    }

    public async Task<WorkOffsetOperationView?> ReadOperationAsync(
        string machineId, string caseOperationId, string? workOffset, int limit, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        if (!await ExistsAsync(connection, null, "machines", machineId, token)
            || !await ExistsAsync(connection, null, "case_operations", caseOperationId, token)) return null;
        var filter = " WHERE o.machine_id = $machine AND o.case_operation_id = $operation"
                     + (workOffset is null ? string.Empty : " AND o.work_offset = $offset");
        var parameters = new List<(string, object?)> { ("$machine", machineId), ("$operation", caseOperationId) };
        if (workOffset is not null) parameters.Add(("$offset", workOffset));
        var current = await ReadManyAsync(connection, null, filter + " AND o.status = 'VALID'" + NewestFirst, token, parameters.ToArray());
        parameters.Add(("$limit", limit));
        var history = await ReadManyAsync(connection, null, filter + NewestFirst + " LIMIT $limit", token, parameters.ToArray());
        return new WorkOffsetOperationView(machineId, caseOperationId, workOffset, current, history);
    }

    public async Task<IReadOnlyList<WorkOffsetRecord>> ListAsync(WorkOffsetListQuery query, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        var conditions = new List<string>();
        var parameters = new List<(string, object?)>();
        if (query.Statuses.Count > 0)
        {
            var names = query.Statuses.Select((status, index) => "$status" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
            conditions.Add($"o.status IN ({string.Join(", ", names)})");
            parameters.AddRange(query.Statuses.Select((status, index) => (names[index], (object?)status)));
        }
        if (query.MachineId is not null)
        {
            conditions.Add("o.machine_id = $machine");
            parameters.Add(("$machine", query.MachineId));
        }
        if (query.CaseOperationId is not null)
        {
            conditions.Add("o.case_operation_id = $operation");
            parameters.Add(("$operation", query.CaseOperationId));
        }
        parameters.Add(("$limit", query.Limit));
        var where = conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions);
        return await ReadManyAsync(connection, null,
            where + " ORDER BY o.received_at DESC, o.rowid DESC LIMIT $limit", token, parameters.ToArray());
    }

    public async Task<WorkOffsetRecord> ReviewAsync(WorkOffsetReviewCommand command, DateTimeOffset now, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var candidate = await ReadOneAsync(connection, transaction, " WHERE o.id = $id", token, ("$id", command.WorkOffsetId))
            ?? throw new WorkOffsetNotFoundException("The measured work offset does not exist.");
        if (candidate.Version != command.ExpectedVersion || candidate.Status != WorkOffsetStatuses.ReviewRequired)
        {
            throw new EditConflictException(
                "measured work offset",
                candidate.Status == WorkOffsetStatuses.ReviewRequired
                    ? "The measured work offset changed after you loaded it."
                    : $"The measured work offset is no longer waiting for review: it is {candidate.Status}.",
                "Refresh the work offsets, check the measurement's status, then decide again if it still waits for review.",
                candidate.ReviewedBy ?? candidate.ReceivedBy,
                candidate.ReviewedAt ?? candidate.ReceivedAt);
        }

        WorkOffsetRecord? current = null;
        if (command.Accept)
        {
            current = await ReadOneAsync(connection, transaction,
                " WHERE o.machine_id = $machine AND o.case_operation_id = $operation AND o.program_release_id = $release"
                + " AND o.work_offset = $offset AND o.status = 'VALID'",
                token,
                ("$machine", candidate.MachineId), ("$operation", candidate.CaseOperationId),
                ("$release", candidate.NcReleaseId), ("$offset", candidate.WorkOffset));
            if (command.CheckCurrent && current?.WorkOffsetId != NullIfEmpty(command.ExpectedCurrentId))
            {
                throw new EditConflictException(
                    "measured work offset",
                    current is null
                        ? "The measurement you compared with is no longer current."
                        : "Another measurement became current for this work offset after you loaded it.",
                    "Refresh the work offsets and compare the candidate with the current measurement again before you accept it.",
                    current?.ReviewedBy ?? current?.ReceivedBy,
                    current?.ReviewedAt ?? current?.ReceivedAt);
            }
            if (current is not null)
                await UpdateStatusAsync(connection, transaction, current.WorkOffsetId, current.Version,
                    WorkOffsetStatuses.Superseded, null, null, null, null, token);
        }

        await UpdateStatusAsync(connection, transaction, candidate.WorkOffsetId, candidate.Version,
            command.Accept ? WorkOffsetStatuses.Valid : WorkOffsetStatuses.Rejected,
            command.Accept ? current?.WorkOffsetId : null, command.UserName, now, command.Comment, token);
        await SqliteStructuredEventLogRepository.AppendAsync(connection, transaction, new StructuredEventWrite(
            command.Accept ? "machine_work_offset_accepted" : "machine_work_offset_rejected",
            now,
            command.UserName,
            new Dictionary<string, string>
            {
                ["workOffsetId"] = candidate.WorkOffsetId,
                ["machineId"] = candidate.MachineId ?? string.Empty,
                ["caseOperationId"] = candidate.CaseOperationId ?? string.Empty,
                ["gcodeReleaseId"] = candidate.NcReleaseId ?? string.Empty
            },
            candidate.StatusReason,
            command.Comment,
            new { status = candidate.Status, supersedes = current?.WorkOffsetId },
            new { status = command.Accept ? WorkOffsetStatuses.Valid : WorkOffsetStatuses.Rejected, candidate.WorkOffset }),
            token);
        var result = await ReadOneAsync(connection, transaction, " WHERE o.id = $id", token, ("$id", candidate.WorkOffsetId));
        await transaction.CommitAsync(token);
        return result!;
    }

    public async Task<string?> GetDprntSourceAsync(string machineId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT configuration_json FROM machine_connections WHERE machine_id = $machine LIMIT 1;";
        command.Parameters.AddWithValue("$machine", machineId);
        if (await command.ExecuteScalarAsync(token) is not string json) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var dprnt = Property(document.RootElement, "dprnt");
            if (dprnt is not { ValueKind: JsonValueKind.Object } value) return "TCP";
            var source = Property(value, "source");
            return source is { ValueKind: JsonValueKind.String } text && !string.IsNullOrWhiteSpace(text.GetString())
                ? text.GetString()!.Trim().ToUpperInvariant()
                : "TCP";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<WorkOffsetMacroContext?> GetMacroContextAsync(
        string machineId, string caseOperationId, string? releaseId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT number, name, nc_dialect FROM machines WHERE id = $id;";
        command.Parameters.AddWithValue("$id", machineId);
        string number, name, dialect;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) return null;
            number = reader.GetString(0);
            name = reader.GetString(1);
            dialect = reader.GetString(2);
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT operation_number FROM case_operations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", caseOperationId);
        if (await command.ExecuteScalarAsync(token) is not long operationNumber) return null;
        int? token6 = null;
        if (releaseId is not null)
        {
            command.Parameters.Clear();
            command.CommandText = """
                SELECT r.case_operation_id, h.nc_identity_token
                FROM gcode_releases r LEFT JOIN gcode_release_verification_hooks h ON h.gcode_release_id = r.id
                WHERE r.id = $id;
                """;
            command.Parameters.AddWithValue("$id", releaseId);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || reader.GetString(0) != caseOperationId)
                throw new WorkOffsetValidationException("release_not_of_operation",
                    "The NC release is not a release of this Case Operation.");
            token6 = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        }
        return new WorkOffsetMacroContext(machineId, number, name, dialect, caseOperationId,
            (int)operationNumber, releaseId, token6, null);
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static async Task UpdateStatusAsync(
        SqliteConnection connection, SqliteTransaction transaction, string id, int version, string status,
        string? supersedesId, string? reviewedBy, DateTimeOffset? reviewedAt, string? comment, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE machine_work_offsets
            SET status = $status,
                supersedes_id = COALESCE(supersedes_id, $supersedes),
                reviewed_by = COALESCE($reviewedBy, reviewed_by),
                reviewed_at = COALESCE($reviewedAt, reviewed_at),
                review_comment = COALESCE($comment, review_comment),
                version = version + 1
            WHERE id = $id AND version = $version;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$supersedes", (object?)supersedesId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reviewedBy", (object?)reviewedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$reviewedAt", reviewedAt is { } at ? Format(at) : DBNull.Value);
        command.Parameters.AddWithValue("$comment", (object?)NullIfEmpty(comment) ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new EditConflictException("measured work offset",
                "The measured work offset changed while it was being updated.",
                "Refresh the work offsets and try again.");
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string table, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT 1 FROM {table} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<WorkOffsetRecord?> ReadOneAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string where, CancellationToken token,
        params (string Name, object? Value)[] parameters) =>
        (await ReadManyAsync(connection, transaction, where + " LIMIT 1", token, parameters)).FirstOrDefault();

    private static async Task<IReadOnlyList<WorkOffsetRecord>> ReadManyAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string tail, CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectRecord + tail + ";";
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var records = new List<WorkOffsetRecord>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) records.Add(Map(reader));
        return records;
    }

    private static WorkOffsetRecord Map(SqliteDataReader reader)
    {
        string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        double? Real(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
        int? Integer(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
        DateTimeOffset? Instant(int ordinal) => reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));

        var processRevision = Integer(9);
        var postprocessor = Text(10);
        var postRevision = Integer(11);
        var identity = Integer(8);
        string? label = null;
        if (processRevision is not null && postRevision is not null)
        {
            label = string.Create(CultureInfo.InvariantCulture, $"process r{processRevision} · {postprocessor} r{postRevision}");
            if (identity is not null) label += string.Create(CultureInfo.InvariantCulture, $" · NC {identity}");
        }
        return new WorkOffsetRecord(
            reader.GetString(0),
            Text(1), Text(2), Text(3),
            Text(4), Integer(5), Text(6),
            Text(7), identity, label,
            reader.IsDBNull(12) ? null : reader.GetInt64(12) == 1,
            reader.GetString(13),
            new WorkOffsetPosition(Real(14), Real(15), Real(16)),
            new WorkOffsetRotation(Real(17), Real(18), Real(19)),
            Text(20),
            reader.GetString(21),
            reader.GetString(22),
            reader.IsDBNull(23) ? null : reader.GetInt64(23),
            new WorkOffsetClaim(Text(24), Text(25), Text(26), Text(27)),
            reader.GetString(28),
            reader.GetString(29),
            Parse(reader.GetString(30)),
            Parse(reader.GetString(31)),
            reader.GetString(32),
            reader.GetString(33),
            Text(34),
            reader.GetString(35),
            Text(36),
            Text(37),
            Instant(38),
            Text(39),
            reader.GetInt32(40));
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed class IngestUnit(SqliteConnection connection, SqliteTransaction transaction) : IWorkOffsetIngestUnit
    {
        private bool committed;

        public Task<WorkOffsetRecord?> FindByHashAsync(string rawMessageHash, CancellationToken token) =>
            ReadOneAsync(connection, transaction, " WHERE o.raw_message_hash = $hash", token, ("$hash", rawMessageHash));

        public Task<WorkOffsetRecord?> FindBySessionAsync(string sessionKey, CancellationToken token) =>
            ReadOneAsync(connection, transaction, " WHERE o.session_key = $key ORDER BY o.received_at, o.rowid", token,
                ("$key", sessionKey));

        public async Task<IReadOnlyList<WorkOffsetMachineCandidate>> MachinesAsync(CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT id, number, name, is_active, nc_dialect FROM machines ORDER BY id;";
            var machines = new List<WorkOffsetMachineCandidate>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                machines.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt64(3) == 1, reader.GetString(4)));
            return machines;
        }

        public async Task<IReadOnlyList<WorkOffsetReleaseCandidate>> FindReleasesAsync(string reference, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT r.id, r.case_operation_id, co.operation_number, h.nc_identity_token, r.file_hash,
                       CASE WHEN pr.is_active = 1 AND NOT EXISTS (
                            SELECT 1 FROM gcode_releases newer
                            WHERE newer.process_revision_id = r.process_revision_id
                              AND newer.postprocessor_id = r.postprocessor_id
                              AND newer.post_specific_revision > r.post_specific_revision) THEN 1 ELSE 0 END
                FROM gcode_releases r
                JOIN case_operations co ON co.id = r.case_operation_id
                JOIN process_revisions pr ON pr.id = r.process_revision_id
                LEFT JOIN gcode_release_verification_hooks h ON h.gcode_release_id = r.id
                WHERE ($token IS NOT NULL AND h.nc_identity_token = $token)
                   OR lower(r.id) = lower($reference)
                   OR ($hashPrefix IS NOT NULL AND r.file_hash LIKE $hashPrefix || '%')
                ORDER BY r.id;
                """;
            var isToken = reference.Length == 6 && reference.All(char.IsAsciiDigit);
            var isHash = reference.Length is >= 12 and <= 64 && reference.All(char.IsAsciiHexDigit);
            command.Parameters.AddWithValue("$token", isToken ? int.Parse(reference, CultureInfo.InvariantCulture) : DBNull.Value);
            command.Parameters.AddWithValue("$reference", reference);
            command.Parameters.AddWithValue("$hashPrefix", isHash ? reference.ToLowerInvariant() : DBNull.Value);
            var releases = new List<WorkOffsetReleaseCandidate>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                releases.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetString(4), reader.GetInt64(5) == 1));
            return releases;
        }

        public Task<WorkOffsetRecord?> CurrentAsync(
            string machineId, string caseOperationId, string releaseId, string workOffset, CancellationToken token) =>
            ReadOneAsync(connection, transaction,
                " WHERE o.machine_id = $machine AND o.case_operation_id = $operation AND o.program_release_id = $release"
                + " AND o.work_offset = $offset AND o.status = 'VALID'",
                token, ("$machine", machineId), ("$operation", caseOperationId), ("$release", releaseId), ("$offset", workOffset));

        public async Task<WorkOffsetRecord> InsertAsync(WorkOffsetRow row, CancellationToken token)
        {
            if (row.Status == WorkOffsetStatuses.Valid && row.SupersedesId is not null)
            {
                var previous = await ReadOneAsync(connection, transaction, " WHERE o.id = $id", token, ("$id", row.SupersedesId));
                if (previous is not null)
                    await UpdateStatusAsync(connection, transaction, previous.WorkOffsetId, previous.Version,
                        WorkOffsetStatuses.Superseded, null, null, null, null, token);
            }
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO machine_work_offsets (
                        id, machine_id, case_operation_id, program_release_id, work_offset,
                        x_mm, y_mm, z_mm, a_deg, b_deg, c_deg, units, source, source_session, session_key,
                        source_sequence, claimed_machine, claimed_program, claimed_operation, claimed_release,
                        raw_message, raw_message_hash, measured_at, received_at, received_by,
                        initial_status, status, status_reason, supersedes_id, version)
                    VALUES (
                        $id, $machine, $operation, $release, $offset,
                        $x, $y, $z, $a, $b, $c, $units, $source, $session, $sessionKey,
                        $sequence, $claimedMachine, $claimedProgram, $claimedOperation, $claimedRelease,
                        $raw, $hash, $measuredAt, $receivedAt, $receivedBy,
                        $status, $status, $reason, $supersedes, 1);
                    """;
                void Add(string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                Add("$id", row.Id);
                Add("$machine", row.MachineId);
                Add("$operation", row.CaseOperationId);
                Add("$release", row.ReleaseId);
                Add("$offset", row.WorkOffset);
                Add("$x", row.X); Add("$y", row.Y); Add("$z", row.Z);
                Add("$a", row.A); Add("$b", row.B); Add("$c", row.C);
                Add("$units", row.Units);
                Add("$source", row.Source);
                Add("$session", row.SourceSession);
                Add("$sessionKey", row.SessionKey);
                Add("$sequence", row.SourceSequence);
                Add("$claimedMachine", row.Claimed.Machine);
                Add("$claimedProgram", row.Claimed.Program);
                Add("$claimedOperation", row.Claimed.Operation);
                Add("$claimedRelease", row.Claimed.Release);
                Add("$raw", row.RawMessage);
                Add("$hash", row.RawMessageHash);
                Add("$measuredAt", Format(row.MeasuredAt));
                Add("$receivedAt", Format(row.ReceivedAt));
                Add("$receivedBy", row.ReceivedBy);
                Add("$status", row.Status);
                Add("$reason", row.StatusReason);
                Add("$supersedes", row.Status == WorkOffsetStatuses.Valid ? row.SupersedesId : null);
                await command.ExecuteNonQueryAsync(token);
            }
            return (await ReadOneAsync(connection, transaction, " WHERE o.id = $id", token, ("$id", row.Id)))!;
        }

        public async Task CommitAsync(CancellationToken token)
        {
            await transaction.CommitAsync(token);
            committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!committed)
            {
                try { await transaction.RollbackAsync(); }
                catch (InvalidOperationException) { }
                catch (SqliteException) { }
            }
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
