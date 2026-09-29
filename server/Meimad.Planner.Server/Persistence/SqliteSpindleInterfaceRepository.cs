using System.Globalization;
using Meimad.Planner.Server.Domain.ToolPreparations;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>The Setup library of spindle adaptors and pull studs and each Machine's default (schema v91).</summary>
internal sealed class SqliteSpindleInterfaceRepository(SqliteDatabase database)
{
    internal async Task<SpindleLibrary> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var library = await ReadAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return library;
    }

    internal static async Task<SpindleLibrary> ReadAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var adaptors = new List<SpindleAdaptor>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, name, taper_length, gauge_diameter, small_end_diameter, tool_changer_diameter,
                       tool_changer_length, notes, is_active, version, updated_at, updated_by
                FROM spindle_adaptors ORDER BY name COLLATE NOCASE;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                adaptors.Add(new SpindleAdaptor(
                    reader.GetString(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4),
                    reader.GetDouble(5), reader.GetDouble(6), Text(reader, 7), reader.GetInt32(8) == 1, reader.GetInt32(9),
                    Parse(reader.GetString(10)), reader.GetString(11)));
            }
        }
        var studs = new List<PullStud>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, name, thread, angle, overall_length, exposed_length, knob_diameter, neck_diameter,
                       pilot_diameter, notes, is_active, version, updated_at, updated_by
                FROM pull_studs ORDER BY name COLLATE NOCASE;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                studs.Add(new PullStud(
                    reader.GetString(0), reader.GetString(1), Text(reader, 2), Number(reader, 3), Number(reader, 4),
                    reader.GetDouble(5), reader.GetDouble(6), Number(reader, 7), Number(reader, 8), Text(reader, 9),
                    reader.GetInt32(10) == 1, reader.GetInt32(11), Parse(reader.GetString(12)), reader.GetString(13)));
            }
        }
        var machines = new List<MachineSpindleInterface>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT machine_id, spindle_adaptor_id, pull_stud_id, version FROM machine_spindle_interfaces ORDER BY machine_id;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                machines.Add(new MachineSpindleInterface(reader.GetString(0), Text(reader, 1), Text(reader, 2), reader.GetInt32(3)));
            }
        }
        return new SpindleLibrary(adaptors, studs, machines);
    }

    internal async Task<SpindleAdaptor> SaveAdaptorAsync(
        string? id, int expectedVersion, SpindleAdaptorValues values, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var adaptorId = id ?? Guid.NewGuid().ToString("N");
        await WriteAsync(id, expectedVersion, "spindle_adaptors", "Spindle adaptor", values.Name!, """
            INSERT INTO spindle_adaptors (id, name, taper_length, gauge_diameter, small_end_diameter,
                tool_changer_diameter, tool_changer_length, notes, is_active, version, updated_at, updated_by)
            VALUES ($id, $name, $taper, $gauge, $small, $tcd, $tcl, $notes, $active, 1, $at, $by);
            """, """
            UPDATE spindle_adaptors SET name = $name, taper_length = $taper, gauge_diameter = $gauge,
                small_end_diameter = $small, tool_changer_diameter = $tcd, tool_changer_length = $tcl,
                notes = $notes, is_active = $active, version = version + 1, updated_at = $at, updated_by = $by
            WHERE id = $id AND version = $expected;
            """, command =>
            {
                command.Parameters.AddWithValue("$id", adaptorId);
                command.Parameters.AddWithValue("$taper", values.TaperLength);
                command.Parameters.AddWithValue("$gauge", values.GaugeDiameter);
                command.Parameters.AddWithValue("$small", values.SmallEndDiameter!.Value);
                command.Parameters.AddWithValue("$tcd", values.ToolChangerDiameter);
                command.Parameters.AddWithValue("$tcl", values.ToolChangerLength);
                command.Parameters.AddWithValue("$notes", (object?)values.Notes ?? DBNull.Value);
                command.Parameters.AddWithValue("$active", values.IsActive ? 1 : 0);
            }, actor, now, cancellationToken);
        return (await ReadAsync(cancellationToken)).Adaptors.Single(adaptor => adaptor.SpindleAdaptorId == adaptorId);
    }

    internal async Task<PullStud> SavePullStudAsync(
        string? id, int expectedVersion, PullStudValues values, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var studId = id ?? Guid.NewGuid().ToString("N");
        await WriteAsync(id, expectedVersion, "pull_studs", "Pull stud", values.Name!, """
            INSERT INTO pull_studs (id, name, thread, angle, overall_length, exposed_length, knob_diameter,
                neck_diameter, pilot_diameter, notes, is_active, version, updated_at, updated_by)
            VALUES ($id, $name, $thread, $angle, $overall, $exposed, $knob, $neck, $pilot, $notes, $active, 1, $at, $by);
            """, """
            UPDATE pull_studs SET name = $name, thread = $thread, angle = $angle, overall_length = $overall,
                exposed_length = $exposed, knob_diameter = $knob, neck_diameter = $neck, pilot_diameter = $pilot,
                notes = $notes, is_active = $active, version = version + 1, updated_at = $at, updated_by = $by
            WHERE id = $id AND version = $expected;
            """, command =>
            {
                command.Parameters.AddWithValue("$id", studId);
                command.Parameters.AddWithValue("$thread", (object?)values.Thread ?? DBNull.Value);
                command.Parameters.AddWithValue("$angle", (object?)values.Angle ?? DBNull.Value);
                command.Parameters.AddWithValue("$overall", (object?)values.OverallLength ?? DBNull.Value);
                command.Parameters.AddWithValue("$exposed", values.ExposedLength);
                command.Parameters.AddWithValue("$knob", values.KnobDiameter);
                command.Parameters.AddWithValue("$neck", (object?)values.NeckDiameter ?? DBNull.Value);
                command.Parameters.AddWithValue("$pilot", (object?)values.PilotDiameter ?? DBNull.Value);
                command.Parameters.AddWithValue("$notes", (object?)values.Notes ?? DBNull.Value);
                command.Parameters.AddWithValue("$active", values.IsActive ? 1 : 0);
            }, actor, now, cancellationToken);
        return (await ReadAsync(cancellationToken)).PullStuds.Single(stud => stud.PullStudId == studId);
    }

    /// <summary>Deletes an entry that no Machine and no saved Tool Room tool uses; a used one can be made inactive instead.</summary>
    internal async Task DeleteAsync(string table, string id, int expectedVersion, CancellationToken cancellationToken)
    {
        var column = table == "spindle_adaptors" ? "spindle_adaptor_id" : "pull_stud_id";
        var label = table == "spindle_adaptors" ? "Spindle adaptor" : "Pull stud";
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT (SELECT version FROM {table} WHERE id = $id),
                   EXISTS (SELECT 1 FROM machine_spindle_interfaces WHERE {column} = $id)
                   OR EXISTS (SELECT 1 FROM tool_preparation_tools WHERE {column} = $id);
            """;
        command.Parameters.AddWithValue("$id", id);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            if (reader.IsDBNull(0)) throw new SpindleInterfaceNotFoundException($"{label} '{id}' does not exist.");
            if (reader.GetInt32(0) != expectedVersion)
                throw new SpindleInterfaceConflictException("spindle_interface_version_conflict", $"{label} was changed by someone else; reload it.");
            if (reader.GetInt64(1) == 1)
                throw new SpindleInterfaceConflictException("spindle_interface_in_use",
                    $"{label} is used by a Machine or a saved Tool Room tool; make it inactive instead.");
        }
        command.CommandText = $"DELETE FROM {table} WHERE id = $id;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task<MachineSpindleInterface> SaveMachineAsync(
        string machineId, string? adaptorId, string? pullStudId, int expectedVersion, string actor, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM machines WHERE id = $machine),
                   COALESCE((SELECT version FROM machine_spindle_interfaces WHERE machine_id = $machine), 0),
                   $adaptor IS NULL OR EXISTS (SELECT 1 FROM spindle_adaptors WHERE id = $adaptor),
                   $stud IS NULL OR EXISTS (SELECT 1 FROM pull_studs WHERE id = $stud);
            """;
        command.Parameters.AddWithValue("$machine", machineId);
        command.Parameters.AddWithValue("$adaptor", (object?)adaptorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$stud", (object?)pullStudId ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", Format(now));
        command.Parameters.AddWithValue("$by", actor);
        int current;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt64(0) == 0) throw new SpindleInterfaceNotFoundException($"Machine '{machineId}' does not exist.");
            current = reader.GetInt32(1);
            if (reader.GetInt64(2) == 0)
                throw new SpindleInterfaceValidationException("spindle_adaptor_unknown", $"Spindle adaptor '{adaptorId}' does not exist.", "spindleAdaptorId");
            if (reader.GetInt64(3) == 0)
                throw new SpindleInterfaceValidationException("pull_stud_unknown", $"Pull stud '{pullStudId}' does not exist.", "pullStudId");
        }
        if (current != expectedVersion)
            throw new SpindleInterfaceConflictException("spindle_interface_version_conflict",
                "The Machine's spindle adaptor or pull stud was changed by someone else; reload it.");
        command.CommandText = """
            INSERT INTO machine_spindle_interfaces (machine_id, spindle_adaptor_id, pull_stud_id, version, updated_at, updated_by)
            VALUES ($machine, $adaptor, $stud, 1, $at, $by)
            ON CONFLICT (machine_id) DO UPDATE SET
                spindle_adaptor_id = excluded.spindle_adaptor_id, pull_stud_id = excluded.pull_stud_id,
                version = machine_spindle_interfaces.version + 1, updated_at = excluded.updated_at, updated_by = excluded.updated_by;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MachineSpindleInterface(machineId, adaptorId, pullStudId, current + 1);
    }

    /// <summary>The first spindle adaptor or pull stud id a Tool Room save names that does not exist.</summary>
    internal static async Task<string?> FirstUnknownAsync(
        SqliteConnection connection, SqliteTransaction transaction, string table, IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT EXISTS (SELECT 1 FROM {table} WHERE id = $id);";
            command.Parameters.AddWithValue("$id", id);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0) return id;
        }
        return null;
    }

    private async Task WriteAsync(
        string? id, int expectedVersion, string table, string label, string name, string insertSql, string updateSql,
        Action<SqliteCommand> bind, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS (SELECT 1 FROM {table} WHERE name = $name COLLATE NOCASE AND id IS NOT $id);";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1)
            throw new SpindleInterfaceConflictException("spindle_interface_name_taken", $"{label} '{name}' already exists.");
        command.Parameters.Clear();
        command.CommandText = id is null ? insertSql : updateSql;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        command.Parameters.AddWithValue("$at", Format(now));
        command.Parameters.AddWithValue("$by", actor);
        bind(command);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            command.Parameters.Clear();
            command.CommandText = $"SELECT EXISTS (SELECT 1 FROM {table} WHERE id = $id);";
            command.Parameters.AddWithValue("$id", id!);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
                throw new SpindleInterfaceNotFoundException($"{label} '{id}' does not exist.");
            throw new SpindleInterfaceConflictException("spindle_interface_version_conflict", $"{label} was changed by someone else; reload it.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static double? Number(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
