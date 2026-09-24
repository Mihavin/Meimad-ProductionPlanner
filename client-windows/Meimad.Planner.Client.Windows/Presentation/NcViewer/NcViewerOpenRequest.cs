using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.NcEngine;

namespace Meimad.Planner.Client.Windows.Presentation.NcViewer;

/// <summary>What the NC viewer shows and what it may do with it.</summary>
/// <param name="ContextTitle">Where the program comes from, for the window title (Case, Operation, Machine).</param>
/// <param name="DocumentName">File name shown in the editor.</param>
/// <param name="Document">Program text with its encoding and line ending.</param>
/// <param name="ReadOnly">
/// True for an immutable Server release: the editor cannot change it and Save only writes a
/// local copy. Applying the Meimad format switches to an editable, unsaved local copy.
/// </param>
/// <param name="SourceDescription">Release/source details for the status bar.</param>
/// <param name="FilePath">Local file backing an editable document, if any.</param>
/// <param name="NcDialect">Meimad Machine NC dialect for interpretation and the format default.</param>
/// <param name="FormatService">Server "Apply Meimad Planner Format"; null when offline.</param>
/// <param name="UseForRelease">
/// Hands a saved local file to the Release G-code form of the Case/Operation that opened the
/// editor. Returns a message for the viewer's status bar. Null hides the action.
/// </param>
/// <param name="MachineSelection">
/// NC engine machine id from the Meimad Machine's Setup "NC viewer machine" (or the one shared by
/// the postprocessor's Machines); null lets the engine detect the machine. The viewer's machine
/// list can still change it for the open window.
/// </param>
/// <param name="ValidateService">The Server's canonical-template check; null when offline.</param>
/// <param name="ReleaseContext">
/// Release choices of the Case Operation that opened the viewer (postprocessors, tool table);
/// null when the viewer cannot release to the Server from this window.
/// </param>
/// <param name="ReleaseToServer">
/// Releases the saved program to the Server as a new G-code release of that Operation, with the
/// same rules as the Release G-code form (Edit Mode, comment, confirmations). Null hides the action.
/// </param>
/// <param name="ProgramFolders">
/// The Case Operation's revision folders in the Case Working Folder. Save, "Use for G-code
/// release" and "Release to Server" write the program there; null keeps the file dialogs.
/// </param>
/// <param name="ToolRoomTable">
/// The Tool Room's latest tool table for the Operation on its Machine (measured diameter and
/// length, cutter shape). It replaces the tool values the viewer infers from the program, so the
/// simulation cuts with the prepared tools; null keeps the inferred values.
/// </param>
internal sealed record NcViewerOpenRequest(
    string ContextTitle,
    string DocumentName,
    NcTextDocument Document,
    bool ReadOnly,
    string? SourceDescription = null,
    string? FilePath = null,
    string? NcDialect = null,
    Func<string, string, CancellationToken, Task<NcTemplateFormatResult>>? FormatService = null,
    Func<string, Task<string>>? UseForRelease = null,
    string? MachineSelection = null,
    Func<string, CancellationToken, Task<NcTemplateValidation>>? ValidateService = null,
    NcViewerReleaseContext? ReleaseContext = null,
    Func<NcViewerReleaseCommand, CancellationToken, Task<NcViewerReleaseOutcome>>? ReleaseToServer = null,
    NcProgramFolders? ProgramFolders = null,
    NcViewerToolRoomTable? ToolRoomTable = null)
{
    /// <summary>The upstream viewer's blank program.</summary>
    internal const string BlankProgram = "%\nO0001\n\nM30\n%\n";

    internal static NcTextDocument NewDocument(string text) => new(NcTextFile.Normalize(text), "\r\n", false, "UTF-8");
}

/// <summary>A postprocessor the Operation can be released for (from the G-code catalog).</summary>
internal sealed record NcViewerReleaseTarget(string Id, string Name, string Status);

/// <summary>What the viewer's "Release to Server" dialog offers; captured when the viewer opens.</summary>
/// <param name="OperationTitle">Case and Operation the release belongs to.</param>
/// <param name="Postprocessors">Postprocessors of the Operation's Case.</param>
/// <param name="DefaultPostprocessorId">The postprocessor selected in the Release G-code form.</param>
/// <param name="HasActiveProcessRevision">False for a first release: a tool table must be uploaded and the scope is a new process revision.</param>
/// <param name="ToolTableFilePath">Tool-table file already chosen in the form, if any.</param>
internal sealed record NcViewerReleaseContext(
    string OperationTitle,
    IReadOnlyList<NcViewerReleaseTarget> Postprocessors,
    string? DefaultPostprocessorId,
    bool HasActiveProcessRevision,
    string? ToolTableFilePath);

/// <summary>The viewer's release request: the saved file plus the Release G-code form fields.</summary>
internal sealed record NcViewerReleaseCommand(
    string FilePath,
    string PostprocessorId,
    string ChangeScope,
    string ReleaseComment,
    string? ProcessChangeDescription,
    bool ConfirmNewProcessRevision,
    bool ReuseActiveToolTable,
    bool ConfirmToolTable,
    string? ToolTableFilePath,
    bool HasActiveProcessRevision);

/// <summary>
/// Result of a viewer release; <see cref="Message"/> is shown in the viewer either way.
/// <see cref="FilePath"/> is where the released program is after the release, when it moved to
/// the folder of the numbers the Server assigned.
/// </summary>
internal sealed record NcViewerReleaseOutcome(
    bool Succeeded,
    string Message,
    string? ReleaseId = null,
    int? ProcessRevisionNumber = null,
    int? PostSpecificRevision = null,
    string? FilePath = null);

internal sealed record NcDialectOption(string Id, string Name);

internal static class NcViewerDialects
{
    internal const string Default = "HAAS_NGC";

    internal static readonly IReadOnlyList<NcDialectOption> All =
    [
        new("HAAS_NGC", "Haas NGC"),
        new("FANUC_MACRO_B", "FANUC Macro B (0i / 30i)"),
        new("MAZAK_MATRIX_EIA", "Mazak Matrix (EIA)"),
        new("OKUMA_OSP", "Okuma OSP")
    ];

    /// <summary>
    /// The Machine's dialect when the Machine is known; otherwise the single dialect shared by all
    /// Machines that support the postprocessor; otherwise null (the viewer auto-detects).
    /// </summary>
    internal static string? Resolve(IReadOnlyList<PlannerMachine> machines, string? machineId, string? postprocessorId)
    {
        if (machineId is not null
            && machines.FirstOrDefault(machine => machine.MachineId == machineId) is { } machine)
        {
            return Normalize(machine.NcDialect);
        }
        if (postprocessorId is null) return null;
        var dialects = machines
            .Where(value => value.SupportedPostprocessorIds?.Contains(postprocessorId, StringComparer.Ordinal) == true)
            .Select(value => Normalize(value.NcDialect))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return dialects.Length == 1 ? dialects[0] : null;
    }

    internal static string? Normalize(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return All.Any(option => option.Id == normalized) ? normalized : null;
    }
}

/// <summary>The Meimad Machine's NC viewer machine (Setup) as the viewer's initial machine.</summary>
internal static class NcViewerMachines
{
    /// <summary>
    /// The Machine's NC viewer machine when the Machine is known; otherwise the single NC viewer
    /// machine configured on the Machines that support the postprocessor (Machines left on
    /// auto-detect do not count as a disagreement); otherwise null (the engine detects).
    /// </summary>
    internal static string? Resolve(IReadOnlyList<PlannerMachine> machines, string? machineId, string? postprocessorId)
    {
        if (machineId is not null
            && machines.FirstOrDefault(machine => machine.MachineId == machineId) is { } machine)
        {
            return Normalize(machine.NcViewerMachine);
        }
        if (postprocessorId is null) return null;
        var selections = machines
            .Where(value => value.SupportedPostprocessorIds?.Contains(postprocessorId, StringComparer.Ordinal) == true)
            .Select(value => Normalize(value.NcViewerMachine))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return selections.Length == 1 ? selections[0] : null;
    }

    internal static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return NcEngineMachineCatalog.IsIdentifier(normalized)
            && !string.Equals(normalized, NcEngineInfo.AutoMachine, StringComparison.Ordinal)
            ? normalized
            : null;
    }
}

/// <summary>Everything the viewer session needs from its window (dialogs, the page, the UI thread).</summary>
internal interface INcViewerHostUi
{
    void PostToPage(string json);
    void PublishModel(string token, byte[] utf8Json);
    string? ChooseOpenFile(string? initialDirectory);
    string? ChooseSaveFile(string suggestedName, string? initialDirectory, string title);
    string? ChooseFolder(string title, string? initialDirectory);
    /// <summary>A tool-table file (CSV, JSON or Cimatron MHT) for a release.</summary>
    string? ChooseToolTableFile(string? initialDirectory);
    bool ConfirmDiscardChanges(string documentName);
    /// <summary>Asks before a different file of the same name in a revision folder is replaced.</summary>
    bool ConfirmReplaceFile(string path);
    void UpdateTitle(string documentName, bool dirty);
}
