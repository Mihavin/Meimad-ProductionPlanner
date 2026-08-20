using System.Text.Json;
using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.Machines;
using Meimad.Planner.Server.Domain.Machines;

namespace Meimad.Planner.Server.Api.Machines;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateMachineRequest(
    string? Number,
    string? Name,
    string? ProcessType,
    string? AxisType,
    IReadOnlyList<string?>? Capabilities,
    string? WorkingCalendarId,
    bool? IsActive,
    bool? DisplayEnabled,
    string? PicturePath,
    string? MachineTypeId = null,
    bool? RespectMasterCalendar = true,
    string? ExecutionMode = null,
    IReadOnlyList<string?>? SupportedPostprocessorIds = null,
    int? UsableToolPositions = null,
    double? RapidRateMillimetersPerMinute = null,
    double? ToolChangeTimeSeconds = null,
    double? MachineTimeFactor = null)
{
    internal CreateMachineCommand ToCommand() => new(
        Number,
        Name,
        ProcessType,
        AxisType,
        Capabilities,
        WorkingCalendarId,
        IsActive,
        DisplayEnabled,
        PicturePath,
        MachineTypeId,
        RespectMasterCalendar,
        ExecutionMode,
        SupportedPostprocessorIds,
        UsableToolPositions,
        RapidRateMillimetersPerMinute,
        ToolChangeTimeSeconds,
        MachineTimeFactor);
}

internal sealed class PatchMachineRequest
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Fields { get; init; } =
        new(StringComparer.Ordinal);

    internal UpdateMachineCommand ToCommand()
    {
        var reader = new Reader(Fields);
        var command = new UpdateMachineCommand(
            reader.String("number"),
            reader.String("name"),
            reader.String("processType"),
            reader.String("axisType"),
            reader.StringArray("capabilities"),
            reader.String("workingCalendarId"),
            reader.Boolean("isActive"),
            reader.Boolean("displayEnabled"),
            reader.String("picturePath"),
            reader.String("machineTypeId"),
            reader.Boolean("respectMasterCalendar"),
            reader.String("executionMode"),
            reader.StringArray("supportedPostprocessorIds"),
            reader.Integer("usableToolPositions"),
            reader.Double("rapidRateMillimetersPerMinute"),
            reader.Double("toolChangeTimeSeconds"),
            reader.Double("machineTimeFactor"));
        reader.ThrowIfInvalid();
        return command;
    }

    private sealed class Reader
    {
        private static readonly HashSet<string> Allowed =
        [
            "number", "name", "processType", "axisType", "capabilities",
            "workingCalendarId", "isActive", "displayEnabled", "picturePath",
            "machineTypeId", "respectMasterCalendar", "executionMode",
            "supportedPostprocessorIds", "usableToolPositions",
            "rapidRateMillimetersPerMinute", "toolChangeTimeSeconds", "machineTimeFactor"
        ];

        private readonly IReadOnlyDictionary<string, JsonElement> fields;
        private readonly List<MachineRequestIssue> issues = [];

        internal Reader(IReadOnlyDictionary<string, JsonElement> fields)
        {
            this.fields = fields;
            foreach (var field in fields.Keys.Where(field => !Allowed.Contains(field)))
            {
                issues.Add(new MachineRequestIssue(field, "unknown_field", $"Field '{field}' is not supported."));
            }

            if (fields.Count == 0)
            {
                issues.Add(new MachineRequestIssue(string.Empty, "empty_patch", "At least one Machine field is required."));
            }
        }

        internal MachineField<string?> String(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return MachineField<string?>.Unspecified;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                return MachineField<string?>.Specified(null);
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                return MachineField<string?>.Specified(element.GetString());
            }

            InvalidType(name, "string or null");
            return MachineField<string?>.Unspecified;
        }

        internal MachineField<bool?> Boolean(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return MachineField<bool?>.Unspecified;
            }

            if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return MachineField<bool?>.Specified(element.GetBoolean());
            }

            InvalidType(name, "boolean");
            return MachineField<bool?>.Unspecified;
        }

        internal MachineField<IReadOnlyList<string?>?> StringArray(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return MachineField<IReadOnlyList<string?>?>.Unspecified;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                return MachineField<IReadOnlyList<string?>?>.Specified(null);
            }

            if (element.ValueKind != JsonValueKind.Array)
            {
                InvalidType(name, "string array or null");
                return MachineField<IReadOnlyList<string?>?>.Unspecified;
            }

            var values = new List<string?>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    InvalidType(name, "string array or null");
                    return MachineField<IReadOnlyList<string?>?>.Unspecified;
                }

                values.Add(item.ValueKind == JsonValueKind.Null ? null : item.GetString());
            }

            return MachineField<IReadOnlyList<string?>?>.Specified(values);
        }

        internal MachineField<int?> Integer(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return MachineField<int?>.Unspecified;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                return MachineField<int?>.Specified(null);
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
            {
                return MachineField<int?>.Specified(value);
            }

            InvalidType(name, "32-bit integer or null");
            return MachineField<int?>.Unspecified;
        }

        internal MachineField<double?> Double(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return MachineField<double?>.Unspecified;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                return MachineField<double?>.Specified(null);
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
            {
                return MachineField<double?>.Specified(value);
            }

            InvalidType(name, "number or null");
            return MachineField<double?>.Unspecified;
        }

        internal void ThrowIfInvalid()
        {
            if (issues.Count > 0)
            {
                throw new MachineRequestException(issues);
            }
        }

        private void InvalidType(string field, string expected) =>
            issues.Add(new MachineRequestIssue(field, "invalid_type", $"Field '{field}' must be a {expected}."));
    }
}

internal sealed record MachineResponse(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    string WorkingCalendarId,
    bool IsActive,
    bool DisplayEnabled,
    string? PicturePath,
    string? DeviceId,
    int BacklogCount,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? MachineTypeId,
    bool RespectMasterCalendar,
    string ExecutionMode,
    IReadOnlyList<string> SupportedPostprocessorIds,
    int? UsableToolPositions,
    double? RapidRateMillimetersPerMinute,
    double? ToolChangeTimeSeconds,
    double MachineTimeFactor)
{
    internal static MachineResponse FromDomain(Machine machine) => new(
        machine.MachineId,
        machine.Number,
        machine.Name,
        machine.ProcessType,
        machine.AxisType,
        machine.Capabilities,
        machine.WorkingCalendarId,
        machine.IsActive,
        machine.DisplayEnabled,
        machine.PicturePath,
        machine.DisplayDeviceId,
        machine.BacklogCount,
        machine.Version,
        machine.CreatedAt,
        machine.UpdatedAt,
        machine.MachineTypeId,
        machine.RespectMasterCalendar,
        machine.ExecutionMode,
        machine.SupportedPostprocessorIds ?? [],
        machine.UsableToolPositions,
        machine.RapidRateMillimetersPerMinute,
        machine.ToolChangeTimeSeconds,
        machine.MachineTimeFactor);
}

internal sealed record MachineListResponse(IReadOnlyList<MachineResponse> Items, string? NextCursor);

internal sealed record MachineRequestIssue(string Field, string Code, string Message);

internal sealed class MachineRequestException : Exception
{
    internal MachineRequestException(IReadOnlyList<MachineRequestIssue> issues)
        : base("Machine request is invalid.")
    {
        Issues = issues;
    }

    internal IReadOnlyList<MachineRequestIssue> Issues { get; }
}
