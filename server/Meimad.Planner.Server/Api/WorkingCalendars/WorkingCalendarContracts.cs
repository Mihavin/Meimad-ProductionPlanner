using System.Text.Json;
using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.WorkingCalendars;
using Meimad.Planner.Server.Domain.WorkingCalendars;

namespace Meimad.Planner.Server.Api.WorkingCalendars;

internal sealed record CreateWorkingCalendarRequest(
    string? Name,
    string? TimeZoneId,
    IReadOnlyList<string?>? Workdays,
    string? ShiftStartsAtLocal,
    string? ShiftEndsAtLocal,
    IReadOnlyList<WorkingCalendarWindow?>? Windows = null,
    IReadOnlyList<WorkingCalendarWindow?>? BreakWindows = null,
    IReadOnlyList<WorkingCalendarException?>? Exceptions = null,
    IReadOnlyList<string?>? Usages = null,
    bool UseIsraeliHolidays = false)
{
    internal CreateWorkingCalendarCommand ToCommand() => new(
        Name, TimeZoneId, Workdays, ShiftStartsAtLocal, ShiftEndsAtLocal,
        Windows, BreakWindows, Exceptions, Usages, UseIsraeliHolidays);
}

internal sealed class PatchWorkingCalendarRequest
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Fields { get; init; } = new(StringComparer.Ordinal);

    internal UpdateWorkingCalendarCommand ToCommand()
    {
        var reader = new FieldReader(Fields);
        var command = new UpdateWorkingCalendarCommand(
            reader.String("name"),
            reader.String("timeZoneId"),
            reader.StringArray("workdays"),
            reader.String("shiftStartsAtLocal"),
            reader.String("shiftEndsAtLocal"),
            reader.Windows("windows"),
            reader.Windows("breakWindows"),
            reader.Exceptions("exceptions"),
            reader.StringArray("usages"),
            reader.Boolean("useIsraeliHolidays"));
        reader.ThrowIfInvalid();
        return command;
    }

    private sealed class FieldReader
    {
        private static readonly HashSet<string> Allowed =
            ["name", "timeZoneId", "workdays", "shiftStartsAtLocal", "shiftEndsAtLocal", "windows", "breakWindows", "exceptions", "usages", "useIsraeliHolidays"];
        private readonly IReadOnlyDictionary<string, JsonElement> fields;
        private readonly List<WorkingCalendarRequestIssue> issues = [];

        internal FieldReader(IReadOnlyDictionary<string, JsonElement> fields)
        {
            this.fields = fields;
            foreach (var field in fields.Keys.Where(field => !Allowed.Contains(field)))
                issues.Add(new(field, "unknown_field", $"Field '{field}' is not supported."));
            if (fields.Count == 0) issues.Add(new(string.Empty, "empty_patch", "At least one Working Calendar field is required."));
        }

        internal WorkingCalendarField<string?> String(string name)
        {
            if (!fields.TryGetValue(name, out var value)) return WorkingCalendarField<string?>.Unspecified;
            if (value.ValueKind == JsonValueKind.Null) return WorkingCalendarField<string?>.Specified(null);
            if (value.ValueKind == JsonValueKind.String) return WorkingCalendarField<string?>.Specified(value.GetString());
            issues.Add(new(name, "invalid_type", $"Field '{name}' must be a string or null."));
            return WorkingCalendarField<string?>.Unspecified;
        }

        internal WorkingCalendarField<IReadOnlyList<string?>?> StringArray(string name)
        {
            if (!fields.TryGetValue(name, out var value)) return WorkingCalendarField<IReadOnlyList<string?>?>.Unspecified;
            if (value.ValueKind == JsonValueKind.Null) return WorkingCalendarField<IReadOnlyList<string?>?>.Specified(null);
            if (value.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new(name, "invalid_type", $"Field '{name}' must be a string array or null."));
                return WorkingCalendarField<IReadOnlyList<string?>?>.Unspecified;
            }
            var values = new List<string?>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    issues.Add(new(name, "invalid_type", $"Field '{name}' must be a string array or null."));
                    return WorkingCalendarField<IReadOnlyList<string?>?>.Unspecified;
                }
                values.Add(item.ValueKind == JsonValueKind.Null ? null : item.GetString());
            }
            return WorkingCalendarField<IReadOnlyList<string?>?>.Specified(values);
        }

        internal WorkingCalendarField<bool?> Boolean(string name)
        {
            if (!fields.TryGetValue(name, out var value)) return WorkingCalendarField<bool?>.Unspecified;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return WorkingCalendarField<bool?>.Specified(value.GetBoolean());
            issues.Add(new(name, "invalid_type", $"Field '{name}' must be a boolean."));
            return WorkingCalendarField<bool?>.Unspecified;
        }

        internal WorkingCalendarField<IReadOnlyList<WorkingCalendarWindow?>?> Windows(string name)
        {
            if (!fields.TryGetValue(name, out var value)) return WorkingCalendarField<IReadOnlyList<WorkingCalendarWindow?>?>.Unspecified;
            if (value.ValueKind == JsonValueKind.Null) return WorkingCalendarField<IReadOnlyList<WorkingCalendarWindow?>?>.Specified(null);
            var windows = ParseWindows(value, name);
            return windows is null
                ? WorkingCalendarField<IReadOnlyList<WorkingCalendarWindow?>?>.Unspecified
                : WorkingCalendarField<IReadOnlyList<WorkingCalendarWindow?>?>.Specified(windows);
        }

        internal WorkingCalendarField<IReadOnlyList<WorkingCalendarException?>?> Exceptions(string name)
        {
            if (!fields.TryGetValue(name, out var value)) return WorkingCalendarField<IReadOnlyList<WorkingCalendarException?>?>.Unspecified;
            if (value.ValueKind == JsonValueKind.Null) return WorkingCalendarField<IReadOnlyList<WorkingCalendarException?>?>.Specified(null);
            if (value.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new(name, "invalid_type", $"Field '{name}' must be an array or null."));
                return WorkingCalendarField<IReadOnlyList<WorkingCalendarException?>?>.Unspecified;
            }

            var exceptions = new List<WorkingCalendarException?>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("date", out var date) || date.ValueKind != JsonValueKind.String)
                {
                    issues.Add(new(name, "invalid_type", "Each exception requires a string date field."));
                    return WorkingCalendarField<IReadOnlyList<WorkingCalendarException?>?>.Unspecified;
                }

                var windows = item.TryGetProperty("windows", out var windowsElement)
                    ? ParseWindows(windowsElement, $"{name}.windows")
                    : [];
                var breaks = item.TryGetProperty("breakWindows", out var breaksElement)
                    ? ParseWindows(breaksElement, $"{name}.breakWindows")
                    : [];
                if (windows is null || breaks is null)
                    return WorkingCalendarField<IReadOnlyList<WorkingCalendarException?>?>.Unspecified;
                string? exceptionName = null;
                if (item.TryGetProperty("name", out var nameElement))
                {
                    if (nameElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    {
                        issues.Add(new(name, "invalid_type", "Exception name must be a string or null."));
                        return WorkingCalendarField<IReadOnlyList<WorkingCalendarException?>?>.Unspecified;
                    }
                    exceptionName = nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
                }
                exceptions.Add(new WorkingCalendarException(
                    date.GetString()!,
                    windows.Cast<WorkingCalendarWindow>().ToArray(),
                    breaks.Cast<WorkingCalendarWindow>().ToArray(),
                    exceptionName));
            }
            return WorkingCalendarField<IReadOnlyList<WorkingCalendarException?>?>.Specified(exceptions);
        }

        private IReadOnlyList<WorkingCalendarWindow?>? ParseWindows(JsonElement value, string name)
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new(name, "invalid_type", $"Field '{name}' must be an array."));
                return null;
            }
            var windows = new List<WorkingCalendarWindow?>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("startsAtLocal", out var start) || start.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("endsAtLocal", out var end) || end.ValueKind != JsonValueKind.String)
                { issues.Add(new(name, "invalid_type", "Each window requires string startsAtLocal and endsAtLocal fields.")); return null; }
                windows.Add(new WorkingCalendarWindow(start.GetString()!, end.GetString()!));
            }
            return windows;
        }

        internal void ThrowIfInvalid()
        {
            if (issues.Count > 0) throw new WorkingCalendarRequestException(issues);
        }
    }
}

internal sealed record WorkingCalendarResponse(
    string WorkingCalendarId,
    string Name,
    string TimeZoneId,
    IReadOnlyList<string> Workdays,
    string? ShiftStartsAtLocal,
    string? ShiftEndsAtLocal,
    IReadOnlyList<WorkingCalendarWindow> Windows,
    IReadOnlyList<WorkingCalendarWindow> BreakWindows,
    IReadOnlyList<WorkingCalendarException> Exceptions,
    IReadOnlyList<string> Usages,
    string ScheduleKind,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool UseIsraeliHolidays)
{
    internal static WorkingCalendarResponse FromDomain(WorkingCalendar calendar) => new(
        calendar.WorkingCalendarId,
        calendar.Name,
        calendar.TimeZoneId,
        calendar.Workdays,
        calendar.ShiftStartsAtLocal,
        calendar.ShiftEndsAtLocal,
        calendar.Windows,
        calendar.BreakWindows,
        calendar.Exceptions,
        calendar.Usages,
        calendar.ScheduleKind,
        calendar.Version,
        calendar.CreatedAt,
        calendar.UpdatedAt,
        calendar.UseIsraeliHolidays);
}

internal sealed record WorkingCalendarListResponse(
    IReadOnlyList<WorkingCalendarResponse> Items,
    string? NextCursor);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetSetupCalendarRequest(string? WorkingCalendarId);

internal sealed record SetupCalendarResponse(
    string? WorkingCalendarId,
    WorkingCalendarResponse? Calendar)
{
    internal static SetupCalendarResponse FromDomain(WorkingCalendar? calendar) =>
        new(calendar?.WorkingCalendarId, calendar is null ? null : WorkingCalendarResponse.FromDomain(calendar));
}

internal sealed record WorkingCalendarRequestIssue(string Field, string Code, string Message);
internal sealed class WorkingCalendarRequestException(IReadOnlyList<WorkingCalendarRequestIssue> issues)
    : Exception("Working Calendar request is invalid.")
{
    internal IReadOnlyList<WorkingCalendarRequestIssue> Issues { get; } = issues;
}
