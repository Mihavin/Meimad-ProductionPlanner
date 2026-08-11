using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.WorkingCalendars;
using Meimad.Planner.Server.Domain.WorkingCalendars;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteWorkingCalendarRepository : IWorkingCalendarRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase database;

    public SqliteWorkingCalendarRepository(SqliteDatabase database) => this.database = database;

    public async Task<WorkingCalendar> CreateAsync(
        WorkingCalendar calendar,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        await EnsureNameAvailableAsync(connection, transaction, calendar.Name, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO working_calendars (
                id, name, time_zone_id, calendar_json, version, created_at, updated_at)
            VALUES ($id, $name, $timeZoneId, $calendarJson, $version, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", calendar.WorkingCalendarId);
        command.Parameters.AddWithValue("$name", calendar.Name);
        command.Parameters.AddWithValue("$timeZoneId", calendar.TimeZoneId);
        command.Parameters.AddWithValue("$calendarJson", JsonSerializer.Serialize(new
        {
            weeklySchedule = new
            {
                workdays = calendar.Workdays,
                shiftStartsAtLocal = calendar.ShiftStartsAtLocal,
                shiftEndsAtLocal = calendar.ShiftEndsAtLocal
            }
        }, JsonOptions));
        command.Parameters.AddWithValue("$version", calendar.Version);
        command.Parameters.AddWithValue("$createdAt", FormatInstant(calendar.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatInstant(calendar.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return calendar;
    }

    public async Task<IReadOnlyList<WorkingCalendar>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, time_zone_id, calendar_json, version, created_at, updated_at
            FROM working_calendars
            ORDER BY name COLLATE NOCASE, id;
            """;
        var calendars = new List<WorkingCalendar>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            calendars.Add(ReadCalendar(reader));
        }

        return calendars;
    }

    private static WorkingCalendar ReadCalendar(SqliteDataReader reader)
    {
        IReadOnlyList<string> workdays = [];
        string? startsAt = null;
        string? endsAt = null;
        var kind = "explicit";
        try
        {
            using var json = JsonDocument.Parse(reader.GetString(3));
            if (json.RootElement.TryGetProperty("weeklySchedule", out var weekly))
            {
                workdays = weekly.GetProperty("workdays")
                    .EnumerateArray().Select(value => value.GetString()!).ToArray();
                startsAt = weekly.GetProperty("shiftStartsAtLocal").GetString();
                endsAt = weekly.GetProperty("shiftEndsAtLocal").GetString();
                kind = "weekly";
            }
        }
        catch (JsonException)
        {
            kind = "invalid";
        }

        return new WorkingCalendar(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            workdays, startsAt, endsAt, kind, reader.GetInt32(4),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
    }

    private static async Task EnsureNameAvailableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM working_calendars WHERE name = $name COLLATE NOCASE);";
        command.Parameters.AddWithValue("$name", name);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1)
        {
            throw new WorkingCalendarNameConflictException(name);
        }
    }

    private static async Task EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(
            connection, transaction, DateTimeOffset.UtcNow, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT holder_client_id, generation FROM edit_tokens WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        }

        if (!string.Equals(reader.GetString(0), editAuthority.ClientId, StringComparison.Ordinal)
            || reader.GetInt64(1) != editAuthority.Generation)
        {
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
        }
    }

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
