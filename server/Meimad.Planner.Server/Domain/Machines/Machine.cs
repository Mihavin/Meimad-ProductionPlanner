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
    double MachineTimeFactor = 1.0,
    string NcDialect = MachineNcDialects.HaasNgc);

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
    double? MachineTimeFactor = null,
    string? NcDialect = null);

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
    double MachineTimeFactor = 1.0,
    string NcDialect = MachineNcDialects.HaasNgc);

internal static class MachineExecutionModes
{
    internal const string CncGCode = "CNC_GCODE";
    internal const string Manual = "MANUAL";

    internal static bool IsSupported(string? value) => value is CncGCode or Manual;
}

/// <summary>
/// The control family whose syntax the Server injects into this Machine's runnable NC and Offset
/// Loader, and whose variable ranges its verification configuration must respect.
/// </summary>
internal static class MachineNcDialects
{
    internal const string HaasNgc = "HAAS_NGC";
    internal const string FanucMacroB = "FANUC_MACRO_B";
    internal const string MazakMatrixEia = "MAZAK_MATRIX_EIA";
    internal const string OkumaOsp = "OKUMA_OSP";

    internal static readonly IReadOnlyList<string> All = [HaasNgc, FanucMacroB, MazakMatrixEia, OkumaOsp];

    internal static bool IsSupported(string? value) =>
        value is HaasNgc or FanucMacroB or MazakMatrixEia or OkumaOsp;
}
