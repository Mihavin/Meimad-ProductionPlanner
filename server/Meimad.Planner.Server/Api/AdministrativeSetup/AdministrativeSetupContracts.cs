using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.AdministrativeSetup;
using Meimad.Planner.Server.Domain.AdministrativeSetup;

namespace Meimad.Planner.Server.Api.AdministrativeSetup;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateEmployeeResourceRequest(
    string? EmployeeNumber, string? FirstName, string? LastName, string? Role,
    IReadOnlyList<string?>? Skills, string? AssignedCalendarId, string? PhotoPath, string? Notes,
    string? Email, bool IsActive, bool RespectMasterCalendar = true,
    double ToolLoadSecondsPerTool = 60, double? FixtureAssemblySeconds = null,
    double FirstPartRunningSpeedPercent = 66.6666666667)
{ internal CreateEmployeeResourceCommand ToCommand() => new(EmployeeNumber, FirstName, LastName, Role, Skills, AssignedCalendarId, PhotoPath, Notes, Email, IsActive, RespectMasterCalendar, ToolLoadSecondsPerTool, FixtureAssemblySeconds, FirstPartRunningSpeedPercent); }

internal sealed class PatchEmployeeResourceRequest
{
    [JsonExtensionData] public Dictionary<string, JsonElement> Fields { get; init; } = new(StringComparer.Ordinal);
    internal UpdateEmployeeResourceCommand ToCommand()
    {
        var reader = new AdministrativePatchReader(Fields, new HashSet<string>(["employeeNumber","firstName","lastName","role","skills","assignedCalendarId","photoPath","notes","email","isActive","respectMasterCalendar","toolLoadSecondsPerTool","fixtureAssemblySeconds","firstPartRunningSpeedPercent"], StringComparer.Ordinal));
        var result = new UpdateEmployeeResourceCommand(reader.String("employeeNumber"), reader.String("firstName"), reader.String("lastName"), reader.String("role"), reader.StringArray("skills"), reader.String("assignedCalendarId"), reader.String("photoPath"), reader.String("notes"), reader.String("email"), reader.Boolean("isActive"), reader.Boolean("respectMasterCalendar"), reader.Number("toolLoadSecondsPerTool"), reader.Number("fixtureAssemblySeconds"), reader.Number("firstPartRunningSpeedPercent"));
        reader.ThrowIfInvalid(); return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateIsraeliHolidayRequest(
    DateOnly? Date, string? Name, string? Status, string? StartsAtLocal, string? EndsAtLocal)
{ internal CreateIsraeliHolidayCommand ToCommand() => new(Date, Name, Status, StartsAtLocal, EndsAtLocal); }

internal sealed class PatchIsraeliHolidayRequest
{
    [JsonExtensionData] public Dictionary<string, JsonElement> Fields { get; init; } = new(StringComparer.Ordinal);
    internal UpdateIsraeliHolidayCommand ToCommand()
    {
        var reader = new AdministrativePatchReader(Fields, new HashSet<string>(["date","name","status","startsAtLocal","endsAtLocal"], StringComparer.Ordinal));
        var result = new UpdateIsraeliHolidayCommand(reader.Date("date"), reader.String("name"), reader.String("status"), reader.String("startsAtLocal"), reader.String("endsAtLocal"));
        reader.ThrowIfInvalid(); return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdateReportEmailSettingsRequest(
    string? SenderAddress, IReadOnlyList<string?>? Recipients, string? SmtpHost, int? SmtpPort,
    bool UseSsl, bool DailyReportEnabled, string? DailyReportTimeLocal, string? TimeZoneId,
    bool WeeklyMaterialReportEnabled, string? WeeklyMaterialReportSendDay,
    string? WeeklyMaterialReportTimeLocal, bool WeeklyEmployeeEfficiencyEnabled,
    string? WeeklyEmployeeEfficiencySendDay, string? WeeklyEmployeeEfficiencyTimeLocal)
{
    internal UpdateReportEmailSettingsCommand ToCommand() => new(new(
        SenderAddress, Recipients, SmtpHost, SmtpPort, UseSsl, DailyReportEnabled, DailyReportTimeLocal, TimeZoneId,
        WeeklyMaterialReportEnabled, WeeklyMaterialReportSendDay, WeeklyMaterialReportTimeLocal,
        WeeklyEmployeeEfficiencyEnabled, WeeklyEmployeeEfficiencySendDay, WeeklyEmployeeEfficiencyTimeLocal));
}

internal sealed record EmployeeResourceResponse(
    string ResourceId, string EmployeeNumber, string Name, string FirstName, string LastName, string Role, IReadOnlyList<string> Skills,
    string AssignedCalendarId, string? PhotoPath, string? Notes, string? Email, bool IsActive,
    int Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool RespectMasterCalendar,
    double ToolLoadSecondsPerTool, double? FixtureAssemblySeconds, double FirstPartRunningSpeedPercent)
{ internal static EmployeeResourceResponse FromDomain(EmployeeResource value) => new(value.ResourceId,value.EmployeeNumber,value.Name,value.FirstName,value.LastName,value.ResourceType,value.Skills,value.AssignedCalendarId,value.PhotoPath,value.Notes,value.Email,value.IsActive,value.Version,value.CreatedAt,value.UpdatedAt,value.RespectMasterCalendar,value.ToolLoadSecondsPerTool,value.FixtureAssemblySeconds,value.FirstPartRunningSpeedPercent); }
internal sealed record EmployeeResourceListResponse(IReadOnlyList<EmployeeResourceResponse> Items, string? NextCursor);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateEmployeeCalendarExceptionRequest(
    DateOnly? Date, string? ExceptionType, bool IsFullDay,
    string? StartsAtLocal, string? EndsAtLocal, string? Note)
{
    internal CreateEmployeeCalendarExceptionCommand ToCommand() => new(
        Date, ExceptionType, IsFullDay, StartsAtLocal, EndsAtLocal, Note);
}

internal sealed class PatchEmployeeCalendarExceptionRequest
{
    [JsonExtensionData] public Dictionary<string, JsonElement> Fields { get; init; } = new(StringComparer.Ordinal);
    internal UpdateEmployeeCalendarExceptionCommand ToCommand()
    {
        var reader = new AdministrativePatchReader(Fields, new HashSet<string>(
            ["date", "exceptionType", "isFullDay", "startsAtLocal", "endsAtLocal", "note"], StringComparer.Ordinal));
        var result = new UpdateEmployeeCalendarExceptionCommand(
            reader.Date("date"), reader.String("exceptionType"), reader.Boolean("isFullDay"),
            reader.String("startsAtLocal"), reader.String("endsAtLocal"), reader.String("note"));
        reader.ThrowIfInvalid();
        return result;
    }
}

internal sealed record EmployeeCalendarExceptionResponse(
    string ExceptionId, string ResourceId, DateOnly Date, string ExceptionType, bool IsFullDay,
    string? StartsAtLocal, string? EndsAtLocal, string? Note,
    int Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    internal static EmployeeCalendarExceptionResponse FromDomain(EmployeeCalendarException value) => new(
        value.ExceptionId, value.ResourceId, value.Date, value.ExceptionType, value.IsFullDay,
        value.StartsAtLocal, value.EndsAtLocal, value.Note, value.Version, value.CreatedAt, value.UpdatedAt);
}

internal sealed record EmployeeCalendarExceptionListResponse(
    IReadOnlyList<EmployeeCalendarExceptionResponse> Items, string? NextCursor);

internal sealed record EmployeeAvailabilityWindowResponse(DateTimeOffset StartsAt, DateTimeOffset EndsAt);
internal sealed record EmployeeAvailabilityResponse(
    string ResourceId, bool IsActive, string? AssignedCalendarId, string? TimeZoneId,
    IReadOnlyList<EmployeeAvailabilityWindowResponse> Windows,
    IReadOnlyList<EmployeeCalendarExceptionResponse> Exceptions)
{
    internal static EmployeeAvailabilityResponse FromDomain(EmployeeAvailability value) => new(
        value.ResourceId, value.IsActive, value.AssignedCalendarId, value.TimeZoneId,
        value.Windows.Select(window => new EmployeeAvailabilityWindowResponse(window.StartsAt, window.EndsAt)).ToArray(),
        value.Exceptions.Select(EmployeeCalendarExceptionResponse.FromDomain).ToArray());
}
internal sealed record IsraeliHolidayResponse(
    string IsraeliHolidayId, DateOnly Date, string Name, string Status,
    string? StartsAtLocal, string? EndsAtLocal, string Source, bool IsManualOverride,
    int Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{ internal static IsraeliHolidayResponse FromDomain(IsraeliHoliday value) => new(value.IsraeliHolidayId,value.Date,value.Name,value.Status,value.StartsAtLocal,value.EndsAtLocal,value.Source,value.IsManualOverride,value.Version,value.CreatedAt,value.UpdatedAt); }
internal sealed record IsraeliHolidayListResponse(IReadOnlyList<IsraeliHolidayResponse> Items, string? NextCursor);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SyncIsraeliHolidaysRequest(int FromYear, int ToYear)
{ internal SyncIsraeliHolidaysCommand ToCommand() => new(FromYear, ToYear); }
internal sealed record IsraeliHolidaySyncResponse(
    bool Succeeded, string Provider, int FromYear, int ToYear, int Created, int Updated,
    int PreservedManual, DateTimeOffset LastAttemptAt, DateTimeOffset? LastSuccessAt, string? Error)
{
    internal static IsraeliHolidaySyncResponse FromDomain(IsraeliHolidaySyncResult value) => new(
        value.Succeeded,value.Provider,value.FromYear,value.ToYear,value.Created,value.Updated,
        value.PreservedManual,value.LastAttemptAt,value.LastSuccessAt,value.Error);
}
internal sealed record ReportEmailSettingsResponse(string? SenderAddress, IReadOnlyList<string> Recipients, string? SmtpHost, int? SmtpPort, bool UseSsl, bool DailyReportEnabled, string? DailyReportTimeLocal, string? TimeZoneId, int Version, DateTimeOffset UpdatedAt, bool WeeklyMaterialReportEnabled, string WeeklyMaterialReportSendDay, string WeeklyMaterialReportTimeLocal, bool WeeklyEmployeeEfficiencyEnabled, string WeeklyEmployeeEfficiencySendDay, string WeeklyEmployeeEfficiencyTimeLocal)
{ internal static ReportEmailSettingsResponse FromDomain(ReportEmailSettings value) => new(value.SenderAddress,value.Recipients,value.SmtpHost,value.SmtpPort,value.UseSsl,value.DailyReportEnabled,value.DailyReportTimeLocal,value.TimeZoneId,value.Version,value.UpdatedAt,value.WeeklyMaterialReportEnabled,value.WeeklyMaterialReportSendDay,value.WeeklyMaterialReportTimeLocal,value.WeeklyEmployeeEfficiencyEnabled,value.WeeklyEmployeeEfficiencySendDay,value.WeeklyEmployeeEfficiencyTimeLocal); }

internal sealed record AdministrativeRequestIssue(string Field, string Code, string Message);
internal sealed class AdministrativeRequestException(IReadOnlyList<AdministrativeRequestIssue> issues) : Exception("Administrative Setup request is invalid.")
{ internal IReadOnlyList<AdministrativeRequestIssue> Issues { get; } = issues; }

internal sealed class AdministrativePatchReader
{
    private readonly IReadOnlyDictionary<string, JsonElement> fields;
    private readonly List<AdministrativeRequestIssue> issues = [];
    internal AdministrativePatchReader(IReadOnlyDictionary<string, JsonElement> fields, IReadOnlySet<string> allowed)
    {
        this.fields=fields;
        foreach(var field in fields.Keys.Where(field=>!allowed.Contains(field))) issues.Add(new(field,"unknown_field",$"Field '{field}' is not supported."));
        if(fields.Count==0) issues.Add(new(string.Empty,"empty_patch","At least one field is required."));
    }
    internal AdminField<string?> String(string name)
    { if(!fields.TryGetValue(name,out var value)) return AdminField<string?>.Unspecified; if(value.ValueKind==JsonValueKind.Null)return AdminField<string?>.Specified(null); if(value.ValueKind==JsonValueKind.String)return AdminField<string?>.Specified(value.GetString()); issues.Add(new(name,"invalid_type",$"Field '{name}' must be a string or null.")); return AdminField<string?>.Unspecified; }
    internal AdminField<bool?> Boolean(string name)
    { if(!fields.TryGetValue(name,out var value)) return AdminField<bool?>.Unspecified; if(value.ValueKind==JsonValueKind.True||value.ValueKind==JsonValueKind.False)return AdminField<bool?>.Specified(value.GetBoolean()); issues.Add(new(name,"invalid_type",$"Field '{name}' must be a boolean.")); return AdminField<bool?>.Unspecified; }
    internal AdminField<IReadOnlyList<string?>?> StringArray(string name)
    {
        if(!fields.TryGetValue(name,out var value)) return AdminField<IReadOnlyList<string?>?>.Unspecified;
        if(value.ValueKind==JsonValueKind.Null)return AdminField<IReadOnlyList<string?>?>.Specified(null);
        if(value.ValueKind!=JsonValueKind.Array){issues.Add(new(name,"invalid_type",$"Field '{name}' must be an array of strings or null."));return AdminField<IReadOnlyList<string?>?>.Unspecified;}
        var values=new List<string?>(); var index=0;
        foreach(var item in value.EnumerateArray()){if(item.ValueKind==JsonValueKind.String)values.Add(item.GetString());else {issues.Add(new($"{name}[{index}]","invalid_type",$"Field '{name}' must contain strings."));}index++;}
        return AdminField<IReadOnlyList<string?>?>.Specified(values);
    }
    internal AdminField<double?> Number(string name)
    { if(!fields.TryGetValue(name,out var value)) return AdminField<double?>.Unspecified; if(value.ValueKind==JsonValueKind.Null)return AdminField<double?>.Specified(null); if(value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out var number))return AdminField<double?>.Specified(number); issues.Add(new(name,"invalid_type",$"Field '{name}' must be a number or null.")); return AdminField<double?>.Unspecified; }
    internal AdminField<DateOnly?> Date(string name)
    { if(!fields.TryGetValue(name,out var value)) return AdminField<DateOnly?>.Unspecified; if(value.ValueKind==JsonValueKind.Null)return AdminField<DateOnly?>.Specified(null); if(value.ValueKind==JsonValueKind.String&&DateOnly.TryParseExact(value.GetString(),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))return AdminField<DateOnly?>.Specified(date); issues.Add(new(name,"invalid_date",$"Field '{name}' must use yyyy-MM-dd.")); return AdminField<DateOnly?>.Unspecified; }
    internal void ThrowIfInvalid(){if(issues.Count>0)throw new AdministrativeRequestException(issues);}
}
