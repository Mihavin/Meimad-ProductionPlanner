namespace Meimad.Planner.Server.Domain.Machines;

internal sealed record Machine(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    string WorkingCalendarId,
    bool IsActive,
    bool DisplayEnabled,
    string? DisplayDeviceId,
    int BacklogCount,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PicturePath = null,
    string? MachineTypeId = null,
    IReadOnlyList<string>? MachineTypeCapabilities = null,
    bool RespectMasterCalendar = true,
    string ExecutionMode = MachineExecutionModes.Manual,
    IReadOnlyList<string>? SupportedPostprocessorIds = null,
    int? UsableToolPositions = null,
    double? RapidRateMillimetersPerMinute = null,
    double? ToolChangeTimeSeconds = null,
    double MachineTimeFactor = 1.0);

internal sealed record MachineValues(
    string? Number,
    string? Name,
    string? ProcessType,
    string? AxisType,
    IReadOnlyList<string?>? Capabilities,
    string? WorkingCalendarId,
    bool? IsActive,
    bool? DisplayEnabled,
    string? PicturePath = null,
    string? MachineTypeId = null,
    string? ExecutionMode = null,
    IReadOnlyList<string?>? SupportedPostprocessorIds = null,
    int? UsableToolPositions = null,
    double? RapidRateMillimetersPerMinute = null,
    double? ToolChangeTimeSeconds = null,
    double? MachineTimeFactor = null);

internal sealed record ValidatedMachineValues(
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    string WorkingCalendarId,
    bool IsActive,
    bool DisplayEnabled,
    string? PicturePath = null,
    string? MachineTypeId = null,
    string ExecutionMode = MachineExecutionModes.Manual,
    IReadOnlyList<string>? SupportedPostprocessorIds = null,
    int? UsableToolPositions = null,
    double? RapidRateMillimetersPerMinute = null,
    double? ToolChangeTimeSeconds = null,
    double MachineTimeFactor = 1.0);

internal static class MachineExecutionModes
{
    internal const string CncGCode = "CNC_GCODE";
    internal const string Manual = "MANUAL";

    internal static bool IsSupported(string? value) => value is CncGCode or Manual;
}
