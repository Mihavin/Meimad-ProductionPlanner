using Meimad.Planner.Server.Domain.ToolPreparations;

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
    string NcDialect = MachineNcDialects.HaasNgc,
    string? NcViewerMachine = null,
    string ToolDiameterOffsetKind = ToolDiameterOffsetKinds.Radius);

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
    string? NcDialect = null,
    string? NcViewerMachine = null,
    string? ToolDiameterOffsetKind = null);

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
    string NcDialect = MachineNcDialects.HaasNgc,
    string? NcViewerMachine = null,
    string ToolDiameterOffsetKind = ToolDiameterOffsetKinds.Radius);

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

/// <summary>
/// The NC engine machine definition (NC viewer machine) a Machine's programs are interpreted
/// with: an id such as <c>haas-vf-3ss</c> from the installed engine catalog, or null for the
/// engine's automatic detection. The catalog check happens in <c>MachineService</c>; the value
/// grammar is the engine registry's.
/// </summary>
internal static class MachineNcViewerMachines
{
    internal const string Auto = "auto";
    internal const int MaximumLength = 100;

    /// <summary>Null for blank or "auto"; otherwise the trimmed id.</summary>
    internal static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) || string.Equals(normalized, Auto, StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    internal static bool IsIdentifier(string value) =>
        value.Length is >= 1 and <= MaximumLength
        && (char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0]))
        && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');
}
