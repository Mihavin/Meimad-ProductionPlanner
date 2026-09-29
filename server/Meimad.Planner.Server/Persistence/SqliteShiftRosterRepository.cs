using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.ShiftRoster;
using Meimad.Planner.Server.Domain.WorkingCalendars;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteShiftRosterRepository(SqliteDatabase database) : IShiftRosterRepository
{
    public async Task<IReadOnlyList<ShiftRosterEntry>> ListAsync(
        DateOnly from, DateOnly to, string? resourceId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        return await ListAsync(connection, null, from, to, resourceId, token);
    }

    public async Task<IReadOnlyList<ShiftRosterEntry>> ApplyAsync(
        IReadOnlyList<ShiftRosterChange> changes, DateTimeOffset now, EditAuthority authority, CancellationToken token)
    {
        var actor = SignedInActor.Require(authority);
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var written = new List<ShiftRosterEntry>();
        foreach (var change in changes)
        {
            var current = await ReadAsync(connection, transaction, change.ResourceId, change.Date, token);
            if (current?.Version != change.ExpectedVersion)
                throw new ShiftRosterStaleException(change, current);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (change.ShiftCode is null)
            {
                if (current is null) continue;
                command.CommandText = "DELETE FROM employee_shift_roster_entries WHERE id = $id;";
                command.Parameters.AddWithValue("$id", current.EntryId);
                await command.ExecuteNonQueryAsync(token);
                continue;
            }

            var entry = current is null
                ? new ShiftRosterEntry(Guid.NewGuid().ToString("N"), change.ResourceId, change.Date,
                    change.ShiftCode, change.Note, 1, now, now, actor)
                : current with
                {
                    ShiftCode = change.ShiftCode, Note = change.Note, Version = current.Version + 1,
                    UpdatedAt = now, UpdatedBy = actor
                };
            command.CommandText = current is null
                ? """
                  INSERT INTO employee_shift_roster_entries
                      (id, resource_id, roster_date, shift_code, note, version, created_at, updated_at, updated_by)
                  VALUES ($id, $resourceId, $date, $shiftCode, $note, $version, $createdAt, $updatedAt, $updatedBy);
                  """
                : """
                  UPDATE employee_shift_roster_entries
                  SET shift_code = $shiftCode, note = $note, version = $version,
                      updated_at = $updatedAt, updated_by = $updatedBy
                  WHERE id = $id;
                  """;
            command.Parameters.AddWithValue("$id", entry.EntryId);
            command.Parameters.AddWithValue("$resourceId", entry.ResourceId);
            command.Parameters.AddWithValue("$date", Date(entry.Date));
            command.Parameters.AddWithValue("$shiftCode", entry.ShiftCode);
            command.Parameters.AddWithValue("$note", (object?)entry.Note ?? DBNull.Value);
            command.Parameters.AddWithValue("$version", entry.Version);
            command.Parameters.AddWithValue("$createdAt", Instant(entry.CreatedAt));
            command.Parameters.AddWithValue("$updatedAt", Instant(entry.UpdatedAt));
            command.Parameters.AddWithValue("$updatedBy", (object?)entry.UpdatedBy ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(token);
            written.Add(entry);
        }

        await transaction.CommitAsync(token);
        return written;
    }

    internal static async Task<IReadOnlyList<ShiftRosterEntry>> ListAsync(
        SqliteConnection connection, SqliteTransaction? transaction,
        DateOnly from, DateOnly to, string? resourceId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, resource_id, roster_date, shift_code, note, version, created_at, updated_at, updated_by
            FROM employee_shift_roster_entries
            WHERE roster_date >= $from AND roster_date <= $to
              AND ($resourceId IS NULL OR resource_id = $resourceId)
            ORDER BY resource_id, roster_date;
            """;
        command.Parameters.AddWithValue("$from", Date(from));
        command.Parameters.AddWithValue("$to", Date(to));
        command.Parameters.AddWithValue("$resourceId", (object?)resourceId ?? DBNull.Value);
        var entries = new List<ShiftRosterEntry>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) entries.Add(Read(reader));
        return entries;
    }

    private static async Task<ShiftRosterEntry?> ReadAsync(
        SqliteConnection connection, SqliteTransaction transaction, string resourceId, DateOnly date, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, resource_id, roster_date, shift_code, note, version, created_at, updated_at, updated_by
            FROM employee_shift_roster_entries
            WHERE resource_id = $resourceId AND roster_date = $date;
            """;
        command.Parameters.AddWithValue("$resourceId", resourceId);
        command.Parameters.AddWithValue("$date", Date(date));
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    private static ShiftRosterEntry Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt32(5),
        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
        DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
