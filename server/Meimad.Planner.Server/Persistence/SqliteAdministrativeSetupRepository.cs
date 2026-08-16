using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.AdministrativeSetup;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.AdministrativeSetup;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteAdministrativeSetupRepository : IAdministrativeSetupRepository
{
    private readonly SqliteDatabase database;
    public SqliteAdministrativeSetupRepository(SqliteDatabase database) => this.database = database;

    public async Task<IReadOnlyList<EmployeeResource>> ListResourcesAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, employee_number, name, resource_type, first_name, last_name, skills_json, assigned_calendar_id, photo_path, notes, email, is_active, version, created_at, updated_at, respect_master_calendar FROM employee_resources ORDER BY employee_number COLLATE NOCASE, id;";
        var values = new List<EmployeeResource>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values.Add(ReadResource(reader));
        return values;
    }

    public async Task<EmployeeResource?> GetResourceAsync(string id, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        return await ReadResourceAsync(connection, null, id, token);
    }

    public Task<EmployeeResource> CreateResourceAsync(EmployeeResource value, EditAuthority authority, CancellationToken token) =>
        WriteAsync<EmployeeResource>(authority, async (connection, transaction) =>
        {
            await EnsureEmployeeNumberAvailableAsync(connection, transaction, value.EmployeeNumber, null, token);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO employee_resources (id, employee_number, name, resource_type, first_name, last_name, skills_json, assigned_calendar_id, photo_path, notes, email, is_active, version, created_at, updated_at, respect_master_calendar) VALUES ($id,$number,$name,$type,$firstName,$lastName,$skills,$calendar,$photoPath,$notes,$email,$active,$version,$created,$updated,$respectMaster);";
            BindResource(command, value); await command.ExecuteNonQueryAsync(token); return value;
        }, token);

    public Task<EmployeeResource?> UpdateResourceAsync(EmployeeResource value, int expectedVersion, EditAuthority authority, CancellationToken token) =>
        WriteAsync<EmployeeResource?>(authority, async (connection, transaction) =>
        {
            await EnsureEmployeeNumberAvailableAsync(connection, transaction, value.EmployeeNumber, value.ResourceId, token);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE employee_resources SET employee_number=$number,name=$name,resource_type=$type,first_name=$firstName,last_name=$lastName,skills_json=$skills,assigned_calendar_id=$calendar,photo_path=$photoPath,notes=$notes,email=$email,is_active=$active,respect_master_calendar=$respectMaster,version=$version,updated_at=$updated WHERE id=$id AND version=$expected;";
            BindResource(command, value); command.Parameters.AddWithValue("$expected", expectedVersion);
            return await command.ExecuteNonQueryAsync(token) == 1 ? value : null;
        }, token);

    public Task<bool> DeleteResourceAsync(string id, EditAuthority authority, CancellationToken token) =>
        DeleteAsync("employee_resources", id, authority, token);

    public async Task<IReadOnlyList<EmployeeCalendarException>> ListEmployeeExceptionsAsync(
        string resourceId, DateOnly? from, DateOnly? to, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, resource_id, exception_date, exception_type, is_full_day,
                   starts_at_local, ends_at_local, note, version, created_at, updated_at
            FROM employee_calendar_exceptions
            WHERE resource_id = $resource
              AND ($from IS NULL OR exception_date >= $from)
              AND ($to IS NULL OR exception_date <= $to)
            ORDER BY exception_date, starts_at_local, id;
            """;
        command.Parameters.AddWithValue("$resource", resourceId);
        command.Parameters.AddWithValue("$from", from.HasValue ? Date(from.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$to", to.HasValue ? Date(to.Value) : DBNull.Value);
        var values = new List<EmployeeCalendarException>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values.Add(ReadEmployeeException(reader));
        return values;
    }

    public async Task<EmployeeCalendarException?> GetEmployeeExceptionAsync(
        string resourceId, string exceptionId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        return await ReadEmployeeExceptionAsync(connection, null, resourceId, exceptionId, token);
    }

    public Task<EmployeeCalendarException> CreateEmployeeExceptionAsync(
        EmployeeCalendarException value, EditAuthority authority, CancellationToken token) =>
        WriteAsync(authority, async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO employee_calendar_exceptions
                    (id, resource_id, exception_date, exception_type, is_full_day,
                     starts_at_local, ends_at_local, note, version, created_at, updated_at)
                VALUES
                    ($id, $resource, $date, $type, $fullDay,
                     $starts, $ends, $note, $version, $created, $updated);
                """;
            BindEmployeeException(command, value);
            await command.ExecuteNonQueryAsync(token);
            return value;
        }, token);

    public Task<EmployeeCalendarException?> UpdateEmployeeExceptionAsync(
        EmployeeCalendarException value, int expectedVersion, EditAuthority authority, CancellationToken token) =>
        WriteAsync<EmployeeCalendarException?>(authority, async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE employee_calendar_exceptions
                SET exception_date = $date, exception_type = $type, is_full_day = $fullDay,
                    starts_at_local = $starts, ends_at_local = $ends, note = $note,
                    version = $version, updated_at = $updated
                WHERE id = $id AND resource_id = $resource AND version = $expected;
                """;
            BindEmployeeException(command, value);
            command.Parameters.AddWithValue("$expected", expectedVersion);
            return await command.ExecuteNonQueryAsync(token) == 1 ? value : null;
        }, token);

    public Task<bool> DeleteEmployeeExceptionAsync(
        string resourceId, string exceptionId, EditAuthority authority, CancellationToken token) =>
        WriteAsync(authority, async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM employee_calendar_exceptions WHERE id = $id AND resource_id = $resource;";
            command.Parameters.AddWithValue("$id", exceptionId);
            command.Parameters.AddWithValue("$resource", resourceId);
            return await command.ExecuteNonQueryAsync(token) == 1;
        }, token);

    public async Task<IReadOnlyList<IsraeliHoliday>> ListHolidaysAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,holiday_date,name,holiday_status,starts_at_local,ends_at_local,source,external_id,is_manual_override,version,created_at,updated_at FROM israeli_holidays ORDER BY holiday_date,id;";
        var values = new List<IsraeliHoliday>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) values.Add(ReadHoliday(reader));
        return values;
    }

    public async Task<IsraeliHoliday?> GetHolidayAsync(string id, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        return await ReadHolidayAsync(connection, null, id, token);
    }

    public Task<IsraeliHoliday> CreateHolidayAsync(IsraeliHoliday value, EditAuthority authority, CancellationToken token) =>
        WriteAsync(authority, async (connection, transaction) =>
        {
            await EnsureHolidayDateAvailableAsync(connection, transaction, value.Date, null, token);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO israeli_holidays (id,holiday_date,name,holiday_status,starts_at_local,ends_at_local,source,external_id,is_manual_override,version,created_at,updated_at) VALUES ($id,$date,$name,$status,$starts,$ends,$source,$external,$manual,$version,$created,$updated);";
            BindHoliday(command, value); await command.ExecuteNonQueryAsync(token); return value;
        }, token);

    public Task<IsraeliHoliday?> UpdateHolidayAsync(IsraeliHoliday value, int expectedVersion, EditAuthority authority, CancellationToken token) =>
        WriteAsync<IsraeliHoliday?>(authority, async (connection, transaction) =>
        {
            await EnsureHolidayDateAvailableAsync(connection, transaction, value.Date, value.IsraeliHolidayId, token);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE israeli_holidays SET holiday_date=$date,name=$name,holiday_status=$status,starts_at_local=$starts,ends_at_local=$ends,source=$source,external_id=$external,is_manual_override=$manual,version=$version,updated_at=$updated WHERE id=$id AND version=$expected;";
            BindHoliday(command, value); command.Parameters.AddWithValue("$expected", expectedVersion);
            return await command.ExecuteNonQueryAsync(token) == 1 ? value : null;
        }, token);

    public Task<bool> DeleteHolidayAsync(string id, EditAuthority authority, CancellationToken token) =>
        DeleteAsync("israeli_holidays", id, authority, token);

    public Task<IsraeliHolidaySyncResult> SynchronizeHolidaysAsync(
        IReadOnlyList<IsraeliHolidaySourceItem>? items, string provider, int fromYear, int toYear,
        DateTimeOffset attemptAt, string? error, EditAuthority authority, CancellationToken token) =>
        WriteAsync<IsraeliHolidaySyncResult>(authority, async (connection, transaction) =>
        {
            var created = 0; var updated = 0; var preserved = 0;
            if (items is not null && error is null)
            {
                foreach (var item in items)
                {
                    await using var lookup = connection.CreateCommand(); lookup.Transaction = transaction;
                    lookup.CommandText = "SELECT id,name,holiday_status,is_manual_override,version,created_at FROM israeli_holidays WHERE holiday_date=$date;";
                    lookup.Parameters.AddWithValue("$date", Date(item.Date));
                    await using var reader = await lookup.ExecuteReaderAsync(token);
                    if (await reader.ReadAsync(token))
                    {
                        var id=reader.GetString(0); var currentName=reader.GetString(1); var currentStatus=reader.GetString(2);
                        var manual=reader.GetInt32(3)==1; var version=reader.GetInt32(4);
                        await reader.DisposeAsync();
                        if (manual) { preserved++; continue; }
                        if (currentName == item.Name && currentStatus == item.Status) continue;
                        await using var update=connection.CreateCommand(); update.Transaction=transaction;
                        update.CommandText="UPDATE israeli_holidays SET name=$name,holiday_status=$status,starts_at_local=NULL,ends_at_local=NULL,source=$source,external_id=$external,version=$version,updated_at=$updated WHERE id=$id;";
                        update.Parameters.AddWithValue("$id",id);update.Parameters.AddWithValue("$name",item.Name);update.Parameters.AddWithValue("$status",item.Status);update.Parameters.AddWithValue("$source",provider);update.Parameters.AddWithValue("$external",item.ExternalId);update.Parameters.AddWithValue("$version",version+1);update.Parameters.AddWithValue("$updated",Instant(attemptAt));
                        await update.ExecuteNonQueryAsync(token); updated++;
                    }
                    else
                    {
                        await using var insert=connection.CreateCommand();insert.Transaction=transaction;
                        insert.CommandText="INSERT INTO israeli_holidays(id,holiday_date,name,holiday_status,starts_at_local,ends_at_local,source,external_id,is_manual_override,version,created_at,updated_at) VALUES($id,$date,$name,$status,NULL,NULL,$source,$external,0,1,$now,$now);";
                        insert.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("N"));insert.Parameters.AddWithValue("$date",Date(item.Date));insert.Parameters.AddWithValue("$name",item.Name);insert.Parameters.AddWithValue("$status",item.Status);insert.Parameters.AddWithValue("$source",provider);insert.Parameters.AddWithValue("$external",item.ExternalId);insert.Parameters.AddWithValue("$now",Instant(attemptAt));
                        await insert.ExecuteNonQueryAsync(token);created++;
                    }
                }
            }
            await using var state=connection.CreateCommand();state.Transaction=transaction;
            state.CommandText="UPDATE israeli_holiday_sync_state SET provider=$provider,last_attempt_at=$attempt,last_success_at=CASE WHEN $error IS NULL THEN $attempt ELSE last_success_at END,last_error=$error,from_year=$from,to_year=$to WHERE id=1;";
            state.Parameters.AddWithValue("$provider",provider);state.Parameters.AddWithValue("$attempt",Instant(attemptAt));state.Parameters.AddWithValue("$error",Db(error));state.Parameters.AddWithValue("$from",fromYear);state.Parameters.AddWithValue("$to",toYear);
            await state.ExecuteNonQueryAsync(token);
            DateTimeOffset? lastSuccess=null;
            await using var readState=connection.CreateCommand();readState.Transaction=transaction;readState.CommandText="SELECT last_success_at FROM israeli_holiday_sync_state WHERE id=1;";
            var scalar=await readState.ExecuteScalarAsync(token);if(scalar is string instant)lastSuccess=ParseInstant(instant);
            return new(provider,fromYear,toYear,created,updated,preserved,attemptAt,lastSuccess,error);
        }, token);

    public async Task<ReportEmailSettings> GetReportEmailSettingsAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sender_address, recipients_json, smtp_host, smtp_port, use_ssl, daily_report_enabled, daily_report_time_local, time_zone_id, version, updated_at, weekly_material_report_enabled, weekly_material_report_send_day, weekly_material_report_time_local, weekly_employee_efficiency_enabled, weekly_employee_efficiency_send_day, weekly_employee_efficiency_time_local FROM report_email_settings WHERE id=1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Report email settings singleton is missing.");
        return ReadReportSettings(reader);
    }

    public Task<ReportEmailSettings?> UpdateReportEmailSettingsAsync(ReportEmailSettings value, int expectedVersion, EditAuthority authority, CancellationToken token) =>
        WriteAsync<ReportEmailSettings?>(authority, async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE report_email_settings SET sender_address=$sender,recipients_json=$recipients,smtp_host=$host,smtp_port=$port,use_ssl=$ssl,daily_report_enabled=$enabled,daily_report_time_local=$time,time_zone_id=$zone,weekly_material_report_enabled=$weeklyEnabled,weekly_material_report_send_day=$weeklyDay,weekly_material_report_time_local=$weeklyTime,weekly_employee_efficiency_enabled=$efficiencyEnabled,weekly_employee_efficiency_send_day=$efficiencyDay,weekly_employee_efficiency_time_local=$efficiencyTime,version=$version,updated_at=$updated WHERE id=1 AND version=$expected;";
            command.Parameters.AddWithValue("$sender", Db(value.SenderAddress));
            command.Parameters.AddWithValue("$recipients", JsonSerializer.Serialize(value.Recipients));
            command.Parameters.AddWithValue("$host", Db(value.SmtpHost));
            command.Parameters.AddWithValue("$port", value.SmtpPort.HasValue ? value.SmtpPort.Value : DBNull.Value);
            command.Parameters.AddWithValue("$ssl", value.UseSsl ? 1 : 0);
            command.Parameters.AddWithValue("$enabled", value.DailyReportEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$time", Db(value.DailyReportTimeLocal));
            command.Parameters.AddWithValue("$zone", Db(value.TimeZoneId));
            command.Parameters.AddWithValue("$weeklyEnabled", value.WeeklyMaterialReportEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$weeklyDay", value.WeeklyMaterialReportSendDay);
            command.Parameters.AddWithValue("$weeklyTime", value.WeeklyMaterialReportTimeLocal);
            command.Parameters.AddWithValue("$efficiencyEnabled", value.WeeklyEmployeeEfficiencyEnabled ? 1 : 0);
            command.Parameters.AddWithValue("$efficiencyDay", value.WeeklyEmployeeEfficiencySendDay);
            command.Parameters.AddWithValue("$efficiencyTime", value.WeeklyEmployeeEfficiencyTimeLocal);
            command.Parameters.AddWithValue("$version", value.Version);
            command.Parameters.AddWithValue("$updated", Instant(value.UpdatedAt));
            command.Parameters.AddWithValue("$expected", expectedVersion);
            return await command.ExecuteNonQueryAsync(token) == 1 ? value : null;
        }, token);

    private async Task<bool> DeleteAsync(string table, string id, EditAuthority authority, CancellationToken token) =>
        await WriteAsync(authority, async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE id=$id;"; command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteNonQueryAsync(token) == 1;
        }, token);

    private async Task<T> WriteAsync<T>(EditAuthority authority, Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        var result = await action(connection, transaction);
        await transaction.CommitAsync(token); return result;
    }

    private static async Task EnsureEmployeeNumberAvailableAsync(SqliteConnection c, SqliteTransaction t, string number, string? except, CancellationToken token)
    {
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM employee_resources WHERE employee_number=$number COLLATE NOCASE AND ($except IS NULL OR id<>$except));";
        command.Parameters.AddWithValue("$number", number); command.Parameters.AddWithValue("$except", Db(except));
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1) throw new EmployeeNumberConflictException(number);
    }

    private static async Task EnsureHolidayDateAvailableAsync(SqliteConnection c, SqliteTransaction t, DateOnly date, string? except, CancellationToken token)
    {
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM israeli_holidays WHERE holiday_date=$date AND ($except IS NULL OR id<>$except));";
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$except", Db(except));
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1) throw new HolidayDateConflictException(date);
    }

    private static async Task EnsureEditAuthorityAsync(SqliteConnection c, SqliteTransaction t, EditAuthority authority, CancellationToken token)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(c, t, DateTimeOffset.UtcNow, token);
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "SELECT holder_client_id,generation FROM edit_tokens WHERE id=1;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0)) throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        if (reader.GetString(0) != authority.ClientId || reader.GetInt64(1) != authority.Generation) throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
    }

    private static async Task<EmployeeResource?> ReadResourceAsync(SqliteConnection c, SqliteTransaction? t, string id, CancellationToken token)
    { await using var command=c.CreateCommand(); command.Transaction=t; command.CommandText="SELECT id,employee_number,name,resource_type,first_name,last_name,skills_json,assigned_calendar_id,photo_path,notes,email,is_active,version,created_at,updated_at,respect_master_calendar FROM employee_resources WHERE id=$id;"; command.Parameters.AddWithValue("$id",id); await using var reader=await command.ExecuteReaderAsync(token); return await reader.ReadAsync(token)?ReadResource(reader):null; }
    private static EmployeeResource ReadResource(SqliteDataReader r) => new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.IsDBNull(10)?null:r.GetString(10),r.GetString(4),r.GetString(5),JsonSerializer.Deserialize<string[]>(r.GetString(6))??[],r.IsDBNull(7)?string.Empty:r.GetString(7),r.IsDBNull(8)?null:r.GetString(8),r.IsDBNull(9)?null:r.GetString(9),r.GetInt32(11)==1,r.GetInt32(12),ParseInstant(r.GetString(13)),ParseInstant(r.GetString(14)),r.GetInt32(15)==1);
    private static async Task<EmployeeCalendarException?> ReadEmployeeExceptionAsync(
        SqliteConnection c, SqliteTransaction? t, string resourceId, string exceptionId, CancellationToken token)
    {
        await using var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = """
            SELECT id, resource_id, exception_date, exception_type, is_full_day,
                   starts_at_local, ends_at_local, note, version, created_at, updated_at
            FROM employee_calendar_exceptions WHERE id = $id AND resource_id = $resource;
            """;
        command.Parameters.AddWithValue("$id", exceptionId);
        command.Parameters.AddWithValue("$resource", resourceId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadEmployeeException(reader) : null;
    }
    private static EmployeeCalendarException ReadEmployeeException(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), DateOnly.ParseExact(r.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        r.GetString(3), r.GetInt32(4) == 1, r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.GetInt32(8), ParseInstant(r.GetString(9)), ParseInstant(r.GetString(10)));
    private static async Task<IsraeliHoliday?> ReadHolidayAsync(SqliteConnection c, SqliteTransaction? t, string id, CancellationToken token)
    { await using var command=c.CreateCommand(); command.Transaction=t; command.CommandText="SELECT id,holiday_date,name,holiday_status,starts_at_local,ends_at_local,source,external_id,is_manual_override,version,created_at,updated_at FROM israeli_holidays WHERE id=$id;"; command.Parameters.AddWithValue("$id",id); await using var reader=await command.ExecuteReaderAsync(token); return await reader.ReadAsync(token)?ReadHoliday(reader):null; }
    private static IsraeliHoliday ReadHoliday(SqliteDataReader r) => new(r.GetString(0),DateOnly.ParseExact(r.GetString(1),"yyyy-MM-dd",CultureInfo.InvariantCulture),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.GetInt32(8)==1,r.GetInt32(9),ParseInstant(r.GetString(10)),ParseInstant(r.GetString(11)));
    private static ReportEmailSettings ReadReportSettings(SqliteDataReader r) => new(r.IsDBNull(0)?null:r.GetString(0),JsonSerializer.Deserialize<string[]>(r.GetString(1))??[],r.IsDBNull(2)?null:r.GetString(2),r.IsDBNull(3)?null:r.GetInt32(3),r.GetInt32(4)==1,r.GetInt32(5)==1,r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),r.GetInt32(8),ParseInstant(r.GetString(9)),r.GetInt32(10)==1,r.GetString(11),r.GetString(12),r.GetInt32(13)==1,r.GetString(14),r.GetString(15));
    private static void BindResource(SqliteCommand c, EmployeeResource v) { c.Parameters.AddWithValue("$id",v.ResourceId);c.Parameters.AddWithValue("$number",v.EmployeeNumber);c.Parameters.AddWithValue("$name",v.Name);c.Parameters.AddWithValue("$type",v.ResourceType);c.Parameters.AddWithValue("$firstName",v.FirstName);c.Parameters.AddWithValue("$lastName",v.LastName);c.Parameters.AddWithValue("$skills",JsonSerializer.Serialize(v.Skills));c.Parameters.AddWithValue("$calendar",Db(v.AssignedCalendarId));c.Parameters.AddWithValue("$photoPath",Db(v.PhotoPath));c.Parameters.AddWithValue("$notes",Db(v.Notes));c.Parameters.AddWithValue("$email",Db(v.Email));c.Parameters.AddWithValue("$active",v.IsActive?1:0);c.Parameters.AddWithValue("$version",v.Version);c.Parameters.AddWithValue("$created",Instant(v.CreatedAt));c.Parameters.AddWithValue("$updated",Instant(v.UpdatedAt));c.Parameters.AddWithValue("$respectMaster",v.RespectMasterCalendar?1:0); }
    private static void BindEmployeeException(SqliteCommand c, EmployeeCalendarException v)
    {
        c.Parameters.AddWithValue("$id", v.ExceptionId);
        c.Parameters.AddWithValue("$resource", v.ResourceId);
        c.Parameters.AddWithValue("$date", Date(v.Date));
        c.Parameters.AddWithValue("$type", v.ExceptionType);
        c.Parameters.AddWithValue("$fullDay", v.IsFullDay ? 1 : 0);
        c.Parameters.AddWithValue("$starts", Db(v.StartsAtLocal));
        c.Parameters.AddWithValue("$ends", Db(v.EndsAtLocal));
        c.Parameters.AddWithValue("$note", Db(v.Note));
        c.Parameters.AddWithValue("$version", v.Version);
        c.Parameters.AddWithValue("$created", Instant(v.CreatedAt));
        c.Parameters.AddWithValue("$updated", Instant(v.UpdatedAt));
    }
    private static void BindHoliday(SqliteCommand c, IsraeliHoliday v) { c.Parameters.AddWithValue("$id",v.IsraeliHolidayId);c.Parameters.AddWithValue("$date",v.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));c.Parameters.AddWithValue("$name",v.Name);c.Parameters.AddWithValue("$status",v.Status);c.Parameters.AddWithValue("$starts",Db(v.StartsAtLocal));c.Parameters.AddWithValue("$ends",Db(v.EndsAtLocal));c.Parameters.AddWithValue("$source",v.Source);c.Parameters.AddWithValue("$external",Db(v.ExternalId));c.Parameters.AddWithValue("$manual",v.IsManualOverride?1:0);c.Parameters.AddWithValue("$version",v.Version);c.Parameters.AddWithValue("$created",Instant(v.CreatedAt));c.Parameters.AddWithValue("$updated",Instant(v.UpdatedAt)); }
    private static object Db(string? value) => value is null ? DBNull.Value : value;
    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Instant(DateTimeOffset value) => value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseInstant(string value) => DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal);
}
