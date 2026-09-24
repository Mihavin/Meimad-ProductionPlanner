using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meimad.Planner.NcEngine;

public static class NcEngineInfo
{
    /// <summary>Version of the vendored upstream viewer (third_party/chevalier-nc-viewer/UPSTREAM.md).</summary>
    public const string UpstreamVersion = "0.17.0";

    /// <summary>Revision of the Meimad adapter and mapping; bump when analysis output changes.</summary>
    /// <remarks>m2: Meimad machine definitions, dialect translations, lathe subprogram inlining.</remarks>
    public const int AdapterRevision = 2;

    /// <summary>Stored as the NC analysis parser version, e.g. <c>nc-engine/0.17.0+m1</c>.</summary>
    public static string AnalysisVersion => $"nc-engine/{UpstreamVersion}+m{AdapterRevision}";

    public const string AutoMachine = "auto";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>Viewer settings that change how the program is interpreted.</summary>
public sealed record NcEngineSettings(
    double? G30X = null,
    double? G30Z = null,
    string InitialVariables = "#500=0,#505=0");

/// <summary>Input for the cycle-time analysis.</summary>
/// <param name="Text">Program text; Meimad placeholders are neutralized by the engine host.</param>
/// <param name="Dialect">Meimad Machine NC dialect (HAAS_NGC, FANUC_MACRO_B, ...), or null to auto-detect.</param>
/// <param name="MachineSelection">Engine machine id (the Meimad Machine's NC viewer machine), or "auto".</param>
/// <param name="DocumentDirectory">Folder of the program; lathe subprogram calls are inlined from it (must be a readable folder).</param>
/// <param name="ProgramMemory">Program-memory folder per engine machine id, searched after the program folder.</param>
public sealed record NcEngineAnalysisRequest(
    string Text,
    string? Dialect = null,
    string MachineSelection = NcEngineInfo.AutoMachine,
    NcEngineSettings? Settings = null,
    string? DocumentDirectory = null,
    IReadOnlyDictionary<string, string>? ProgramMemory = null);

/// <summary>
/// Machine-independent timing facts from the engine. Rapid and tool-change time are recomputed
/// per Meimad Machine from its configured rapid rate and tool-change time; the engine's own
/// values (from its built-in machine definitions) are informational only.
/// </summary>
public sealed record NcEngineAnalysis(
    string? MachineId,
    string? MachineName,
    string? MachineType,
    string? Interpreter,
    string? SelectionReason,
    string? Kind,
    string Units,
    int LineCount,
    int ExecutedBlockCount,
    int SegmentCount,
    double FeedSeconds,
    double RapidDistanceMillimeters,
    double EngineRapidSeconds,
    int ToolChangeCount,
    double EngineToolChangeSeconds,
    double DwellSeconds,
    double? EngineEstimatedCycleSeconds,
    int UnestimatedSegmentCount,
    bool ResourceLimited,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    string? Translation = null,
    IReadOnlyList<string>? Subprograms = null);

/// <summary>Input for a viewer parse. Mirrors the upstream desktop parse job.</summary>
public sealed record NcEnginePreviewRequest(
    string Text,
    string MachineSelection = NcEngineInfo.AutoMachine,
    string? Dialect = null,
    NcEngineSettings? Settings = null,
    JsonElement? ToolTable = null,
    string? ToolTableSourcePath = null,
    JsonElement? WorkOffsets = null,
    IReadOnlyDictionary<string, string>? ProgramMemory = null,
    string? DocumentDirectory = null,
    string? MachineParametersText = null,
    string? MachineParameterPath = null,
    IReadOnlyList<string>? ResourceWarnings = null);

public sealed record NcEngineMachineRef(string Id, string Name, string Type);

public sealed record NcEngineCompensationIssue(int Line, string Message);

public sealed record NcEngineEffectiveSettings(double G30X, double G30Z, string InitialVariables);

public sealed record NcEnginePreviewSummary(
    string? Kind,
    int SegmentCount,
    IReadOnlyList<NcEngineCompensationIssue> CompensationIssues,
    NcEngineMachineRef? Machine,
    NcEngineEffectiveSettings Settings,
    double? EstimatedCycleSeconds,
    int ErrorCount,
    int WarningCount,
    string? SelectionReason = null);

/// <summary>The packed viewer model (media/model-transport.js format) and its summary.</summary>
public sealed record NcEnginePreviewResult(string Packed, NcEnginePreviewSummary Summary);

/// <param name="Translation">Meimad dialect translation applied for this machine's control, if any.</param>
public sealed record NcEngineMachineSummary(string Id, string Name, string Type, string? Control, bool BuiltIn, string? Translation = null);

/// <summary>Tool table state: the engine's table object (opaque JSON), its XML, and the editor view.</summary>
public sealed record NcEngineToolTable(JsonElement Table, string Xml, JsonElement Editable);

/// <summary>One released tool-table row handed to the engine's description heuristics.</summary>
public sealed record NcEngineToolDescription(int Number, string Description);

/// <summary>What the engine reads from a tool-table description: type, sizes and, for lathes, the tip.</summary>
public sealed record NcEngineInferredTool(
    int Number,
    string Type,
    double? Diameter,
    double? Length,
    double CornerRadius,
    int Tip,
    double? Width,
    string Description);

public class NcEngineException : Exception
{
    public NcEngineException(string message) : base(message) { }
    public NcEngineException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class NcEngineTimeoutException : NcEngineException
{
    public NcEngineTimeoutException(TimeSpan timeout)
        : base($"The NC engine did not finish within {timeout.TotalSeconds:0} seconds.") { }
}
