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
        await EnsureNameAvailableAsync(connection, transaction, calendar.Name, null, cancellationToken);

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
        command.Parameters.AddWithValue("$calendarJson", SerializeCalendar(calendar));
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

    public async Task<WorkingCalendar?> GetByIdAsync(
        string workingCalendarId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        return await ReadByIdAsync(connection, null, workingCalendarId, cancellationToken);
    }

    public async Task<WorkingCalendar?> UpdateAsync(
        WorkingCalendar calendar,
        int expectedVersion,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        await EnsureNameAvailableAsync(
            connection, transaction, calendar.Name, calendar.WorkingCalendarId, cancellationToken);
        await EnsureUsageChangesAreSafeAsync(connection, transaction, calendar, cancellationToken);
        await EnsureRotationChangesAreSafeAsync(connection, transaction, calendar, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE working_calendars
            SET name = $name,
                time_zone_id = $timeZoneId,
                calendar_json = $calendarJson,
                version = $version,
                updated_at = $updatedAt
            WHERE id = $id AND version = $expectedVersion;
            """;
        command.Parameters.AddWithValue("$id", calendar.WorkingCalendarId);
        command.Parameters.AddWithValue("$name", calendar.Name);
        command.Parameters.AddWithValue("$timeZoneId", calendar.TimeZoneId);
        command.Parameters.AddWithValue("$calendarJson", SerializeCalendar(calendar));
        command.Parameters.AddWithValue("$version", calendar.Version);
        command.Parameters.AddWithValue("$updatedAt", FormatInstant(calendar.UpdatedAt));
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return updated ? calendar : null;
    }

    public async Task<bool> DeleteAsync(
        string workingCalendarId,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        await using (var used = connection.CreateCommand())
        {
            used.Transaction = transaction;
            used.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM machines WHERE working_calendar_id = $id
                    UNION ALL
                    SELECT 1 FROM setup_calendar_settings WHERE working_calendar_id = $id
                    UNION ALL
                    SELECT 1 FROM employee_resources WHERE assigned_calendar_id = $id
                    UNION ALL
                    SELECT 1 FROM application_settings WHERE key = 'master_calendar_id' AND value = $id);
                """;
            used.Parameters.AddWithValue("$id", workingCalendarId);
            if (Convert.ToInt32(await used.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1)
            {
                throw new WorkingCalendarInUseException(workingCalendarId);
            }
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM working_calendars WHERE id = $id;";
        delete.Parameters.AddWithValue("$id", workingCalendarId);
        var deleted = await delete.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<WorkingCalendar?> GetSetupCalendarAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT working_calendars.id, working_calendars.name,
                   working_calendars.time_zone_id, working_calendars.calendar_json,
                   working_calendars.version, working_calendars.created_at,
                   working_calendars.updated_at
            FROM setup_calendar_settings
            JOIN working_calendars
              ON working_calendars.id = setup_calendar_settings.working_calendar_id
            WHERE setup_calendar_settings.id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCalendar(reader) : null;
    }

    public async Task<WorkingCalendar> SetSetupCalendarAsync(
        string workingCalendarId,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        var calendar = await ReadByIdAsync(connection, transaction, workingCalendarId, cancellationToken)
            ?? throw new WorkingCalendarNotFoundException(workingCalendarId);
        if (!calendar.Usages.Contains(WorkingCalendarUsage.SetupWorker, StringComparer.Ordinal))
            throw new WorkingCalendarUsageInUseException(
                workingCalendarId,
                "Only a Calendar with setup_worker usage can be selected as the Setup Calendar.");
        if (calendar.IsRotation)
            throw new WorkingCalendarUsageInUseException(
                workingCalendarId,
                "A shift rotation Calendar belongs to employees and cannot be the Setup Calendar; select a weekly Calendar.");
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE setup_calendar_settings
            SET working_calendar_id = $id,
                legacy_fallback_enabled = 0,
                version = version + CASE WHEN working_calendar_id IS $id AND legacy_fallback_enabled = 0 THEN 0 ELSE 1 END,
                updated_at = CASE WHEN working_calendar_id IS $id AND legacy_fallback_enabled = 0 THEN updated_at ELSE $updatedAt END
            WHERE id = 1;
            """;
        update.Parameters.AddWithValue("$id", workingCalendarId);
        update.Parameters.AddWithValue("$updatedAt", FormatInstant(now));
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return calendar;
    }

    public async Task ClearSetupCalendarAsync(
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE setup_calendar_settings
            SET working_calendar_id = NULL,
                legacy_fallback_enabled = 0,
                version = version + CASE WHEN working_calendar_id IS NULL AND legacy_fallback_enabled = 0 THEN 0 ELSE 1 END,
                updated_at = CASE WHEN working_calendar_id IS NULL AND legacy_fallback_enabled = 0 THEN updated_at ELSE $updatedAt END
            WHERE id = 1;
            """;
        update.Parameters.AddWithValue("$updatedAt", FormatInstant(now));
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<WorkingCalendar?> GetMasterCalendarAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM application_settings WHERE key = 'master_calendar_id';";
        var id = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(id)
            ? null
            : await ReadByIdAsync(connection, null, id, cancellationToken);
    }

    public async Task<WorkingCalendar> SetMasterCalendarAsync(
        string workingCalendarId,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        var calendar = await ReadByIdAsync(connection, transaction, workingCalendarId, cancellationToken)
            ?? throw new WorkingCalendarNotFoundException(workingCalendarId);
        if (calendar.IsRotation)
            throw new WorkingCalendarUsageInUseException(
                workingCalendarId,
                "A shift rotation Calendar belongs to employees and cannot be the Israel Master Calendar; select a weekly Calendar.");
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "INSERT INTO application_settings (key, value) VALUES ('master_calendar_id', $id) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        update.Parameters.AddWithValue("$id", workingCalendarId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return calendar;
    }

    public async Task ClearMasterCalendarAsync(EditAuthority editAuthority, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, editAuthority, cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "INSERT INTO application_settings (key, value) VALUES ('master_calendar_id', '') ON CONFLICT(key) DO UPDATE SET value = '';";
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<WorkingCalendar?> ReadByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string workingCalendarId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, name, time_zone_id, calendar_json, version, created_at, updated_at
            FROM working_calendars
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", workingCalendarId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCalendar(reader) : null;
    }

    private static WorkingCalendar ReadCalendar(SqliteDataReader reader) => Parse(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));

    /// <summary>A stored Calendar; <c>calendar_json</c> holds either a weekly schedule or a shift rotation.</summary>
    internal static WorkingCalendar Parse(
        string id, string name, string timeZoneId, string calendarJson, int version,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        IReadOnlyList<string> workdays = [];
        string? startsAt = null;
        string? endsAt = null;
        IReadOnlyList<WorkingCalendarWindow> windows = [];
        IReadOnlyList<WorkingCalendarWindow> breakWindows = [];
        IReadOnlyList<WorkingCalendarException> exceptions = [];
        IReadOnlyList<string> usages = WorkingCalendarUsage.All;
        var kind = "explicit";
        var useIsraeliHolidays = false;
        ShiftRotation? rotation = null;
        try
        {
            using var json = JsonDocument.Parse(calendarJson);
            if (json.RootElement.TryGetProperty("rotation", out var rotationElement)
                && rotationElement.ValueKind == JsonValueKind.Object)
            {
                var document = rotationElement.Deserialize<RotationDocument>(JsonOptions)
                    ?? throw new JsonException("The rotation is empty.");
                rotation = new ShiftRotation(
                    document.AnchorDate,
                    (document.Shifts ?? []).Select(shift => new RotationShift(
                        shift.Code, shift.Name, shift.StartsAtLocal, shift.EndsAtLocal, shift.BreakWindows ?? [])).ToArray(),
                    document.Pattern ?? [],
                    document.Crews ?? []);
                exceptions = (document.Exceptions ?? []).Select(exception => exception with
                {
                    Windows = exception.Windows ?? [], BreakWindows = exception.BreakWindows ?? []
                }).ToArray();
                if (json.RootElement.TryGetProperty("usages", out var rotationUsages) && rotationUsages.ValueKind == JsonValueKind.Array)
                    usages = rotationUsages.EnumerateArray().Select(value => value.GetString()!).ToArray();
                if (json.RootElement.TryGetProperty("useIsraeliHolidays", out var rotationHolidays)
                    && rotationHolidays.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    useIsraeliHolidays = rotationHolidays.GetBoolean();
                kind = WorkingCalendarScheduleKind.Rotation;
            }
            else if (json.RootElement.TryGetProperty("weeklySchedule", out var weekly))
            {
                workdays = weekly.GetProperty("workdays")
                    .EnumerateArray().Select(value => value.GetString()!).ToArray();
                if (weekly.TryGetProperty("windows", out var windowsElement) && windowsElement.ValueKind == JsonValueKind.Array)
                    windows = ReadWindows(windowsElement);
                else
                {
                    startsAt = weekly.GetProperty("shiftStartsAtLocal").GetString();
                    endsAt = weekly.GetProperty("shiftEndsAtLocal").GetString();
                    windows = startsAt is not null && endsAt is not null ? [new WorkingCalendarWindow(startsAt, endsAt)] : [];
                }
                if (weekly.TryGetProperty("breakWindows", out var breaksElement) && breaksElement.ValueKind == JsonValueKind.Array)
                    breakWindows = ReadWindows(breaksElement);
                if (weekly.TryGetProperty("exceptions", out var exceptionsElement) && exceptionsElement.ValueKind == JsonValueKind.Array)
                    exceptions = exceptionsElement.EnumerateArray().Select(exception => new WorkingCalendarException(
                        exception.GetProperty("date").GetString()!,
                        exception.TryGetProperty("windows", out var exceptionWindows) ? ReadWindows(exceptionWindows) : [],
                        exception.TryGetProperty("breakWindows", out var exceptionBreaks) ? ReadWindows(exceptionBreaks) : [],
                        exception.TryGetProperty("name", out var exceptionName) && exceptionName.ValueKind == JsonValueKind.String
                            ? exceptionName.GetString()
                            : null)).ToArray();
                if (json.RootElement.TryGetProperty("usages", out var usagesElement) && usagesElement.ValueKind == JsonValueKind.Array)
                    usages = usagesElement.EnumerateArray().Select(value => value.GetString()!).ToArray();
                if (json.RootElement.TryGetProperty("useIsraeliHolidays", out var holidayElement)
                    && holidayElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    useIsraeliHolidays = holidayElement.GetBoolean();
                if (windows.Count == 1) { startsAt = windows[0].StartsAtLocal; endsAt = windows[0].EndsAtLocal; }
                kind = "weekly";
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or NotSupportedException)
        {
            kind = "invalid";
        }

        return new WorkingCalendar(
            id, name, timeZoneId,
            workdays, startsAt, endsAt, windows, breakWindows, exceptions, usages, kind, version,
            createdAt, updatedAt, useIsraeliHolidays, rotation);
    }

    private sealed record RotationDocument(
        string? AnchorDate,
        IReadOnlyList<RotationShiftDocument>? Shifts,
        IReadOnlyList<string>? Pattern,
        IReadOnlyList<RotationCrew>? Crews,
        IReadOnlyList<WorkingCalendarException>? Exceptions);

    private sealed record RotationShiftDocument(
        string Code, string Name, string StartsAtLocal, string EndsAtLocal,
        IReadOnlyList<WorkingCalendarWindow>? BreakWindows);

    private static async Task EnsureNameAvailableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        string? exceptWorkingCalendarId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM working_calendars
                WHERE name = $name COLLATE NOCASE
                  AND ($exceptId IS NULL OR id <> $exceptId));
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue(
            "$exceptId",
            exceptWorkingCalendarId is null ? DBNull.Value : exceptWorkingCalendarId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1)
        {
            throw new WorkingCalendarNameConflictException(name);
        }
    }

    private static async Task EnsureUsageChangesAreSafeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkingCalendar calendar,
        CancellationToken cancellationToken)
    {
        if (!calendar.Usages.Contains(WorkingCalendarUsage.Machine, StringComparer.Ordinal))
        {
            await using var machine = connection.CreateCommand();
            machine.Transaction = transaction;
            machine.CommandText = "SELECT EXISTS(SELECT 1 FROM machines WHERE working_calendar_id = $id);";
            machine.Parameters.AddWithValue("$id", calendar.WorkingCalendarId);
            if (Convert.ToInt32(await machine.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1)
                throw new WorkingCalendarUsageInUseException(
                    calendar.WorkingCalendarId,
                    "Machine usage cannot be removed while Machines reference this Calendar.");
        }

        if (!calendar.Usages.Contains(WorkingCalendarUsage.SetupWorker, StringComparer.Ordinal))
        {
            await using var setup = connection.CreateCommand();
            setup.Transaction = transaction;
            setup.CommandText = "SELECT EXISTS(SELECT 1 FROM setup_calendar_settings WHERE working_calendar_id = $id);";
            setup.Parameters.AddWithValue("$id", calendar.WorkingCalendarId);
            if (Convert.ToInt32(await setup.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1)
                throw new WorkingCalendarUsageInUseException(
                    calendar.WorkingCalendarId,
                    "Setup-worker usage cannot be removed while this is the selected Setup Calendar.");
        }

        foreach (var (role, usage) in new[]
        {
            ("setup_worker", WorkingCalendarUsage.SetupWorker),
            ("regular_worker", WorkingCalendarUsage.RegularWorker),
            ("qa_worker", WorkingCalendarUsage.QaWorker)
        })
        {
            if (calendar.Usages.Contains(usage, StringComparer.Ordinal)) continue;
            await using var employee = connection.CreateCommand();
            employee.Transaction = transaction;
            employee.CommandText = "SELECT EXISTS(SELECT 1 FROM employee_resources WHERE assigned_calendar_id = $id AND resource_type = $role);";
            employee.Parameters.AddWithValue("$id", calendar.WorkingCalendarId);
            employee.Parameters.AddWithValue("$role", role);
            if (Convert.ToInt32(await employee.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1)
                throw new WorkingCalendarUsageInUseException(
                    calendar.WorkingCalendarId,
                    $"{usage} usage cannot be removed while an Employee Resource with role '{role}' references this Calendar.");
        }
    }

    /// <summary>
    /// A rotation cannot drop a crew an employee follows, or a shift the Shift Roster still uses from
    /// yesterday on; the planner moves those employees or entries first.
    /// </summary>
    private static async Task EnsureRotationChangesAreSafeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkingCalendar calendar,
        CancellationToken cancellationToken)
    {
        var crews = calendar.Rotation?.Crews.Select(crew => crew.Code).ToArray() ?? [];
        await using (var crew = connection.CreateCommand())
        {
            crew.Transaction = transaction;
            crew.CommandText = """
                SELECT name, shift_crew_code FROM employee_resources
                WHERE assigned_calendar_id = $id AND shift_crew_code IS NOT NULL
                ORDER BY employee_number;
                """;
            crew.Parameters.AddWithValue("$id", calendar.WorkingCalendarId);
            await using var reader = await crew.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!crews.Contains(reader.GetString(1), StringComparer.OrdinalIgnoreCase))
                    throw new WorkingCalendarUsageInUseException(
                        calendar.WorkingCalendarId,
                        $"Crew '{reader.GetString(1)}' cannot be removed while {reader.GetString(0)} follows it. Move the employee to another crew first.");
            }
        }

        var shifts = calendar.Rotation?.Shifts.Select(shift => shift.Code).Append(RotationShiftCode.Off).ToArray() ?? [];
        await using var roster = connection.CreateCommand();
        roster.Transaction = transaction;
        roster.CommandText = """
            SELECT DISTINCT employee_shift_roster_entries.shift_code
            FROM employee_shift_roster_entries
            JOIN employee_resources ON employee_resources.id = employee_shift_roster_entries.resource_id
            WHERE employee_resources.assigned_calendar_id = $id AND employee_shift_roster_entries.roster_date >= $from;
            """;
        roster.Parameters.AddWithValue("$id", calendar.WorkingCalendarId);
        roster.Parameters.AddWithValue("$from", calendar.UpdatedAt.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await using var rosterReader = await roster.ExecuteReaderAsync(cancellationToken);
        while (await rosterReader.ReadAsync(cancellationToken))
        {
            var code = rosterReader.GetString(0);
            if (!shifts.Contains(code, StringComparer.Ordinal))
                throw new WorkingCalendarUsageInUseException(
                    calendar.WorkingCalendarId,
                    $"Shift '{code}' cannot be removed while the Shift Roster uses it. Change those roster days first.");
        }
    }

    private static async Task EnsureEditAuthorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        // Single Edit Mode is retired: the API authorized the signed-in user for this change.
        SignedInActor.Require(editAuthority);
        await Task.CompletedTask;
    }

    private static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string SerializeCalendar(WorkingCalendar calendar) => calendar.Rotation is { } rotation
        ? JsonSerializer.Serialize(new
        {
            rotation = new
            {
                anchorDate = rotation.AnchorDate,
                shifts = rotation.Shifts,
                pattern = rotation.Pattern,
                crews = rotation.Crews,
                exceptions = calendar.Exceptions
            },
            usages = calendar.Usages,
            useIsraeliHolidays = calendar.UseIsraeliHolidays
        }, JsonOptions)
        : JsonSerializer.Serialize(new
        {
            weeklySchedule = new
            {
                workdays = calendar.Workdays,
                shiftStartsAtLocal = calendar.Windows.Count == 1 ? calendar.Windows[0].StartsAtLocal : null,
                shiftEndsAtLocal = calendar.Windows.Count == 1 ? calendar.Windows[0].EndsAtLocal : null,
                windows = calendar.Windows,
                breakWindows = calendar.BreakWindows,
                exceptions = calendar.Exceptions
            },
            usages = calendar.Usages,
            useIsraeliHolidays = calendar.UseIsraeliHolidays
        }, JsonOptions);

    private static IReadOnlyList<WorkingCalendarWindow> ReadWindows(JsonElement element) =>
        element.EnumerateArray().Select(window => new WorkingCalendarWindow(
            window.GetProperty("startsAtLocal").GetString()!,
            window.GetProperty("endsAtLocal").GetString()!)).ToArray();
}
