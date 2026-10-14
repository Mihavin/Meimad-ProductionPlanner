using System.Globalization;
using System.IO;
using System.Net.Http;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.NcEngine;

namespace Meimad.Planner.Client.Windows.Presentation.NcViewer;

/// <summary>Builds NC viewer requests from Server data.</summary>
internal static class NcViewerRequests
{
    /// <summary>
    /// The immutable release file exactly as stored, shown read-only. The Machine (when the caller
    /// knows the assignment) or the postprocessor's Machines decide the NC dialect and the NC
    /// viewer machine. The release's tool table (with the Tool Room's shapes and measurements when
    /// the Batch Operation is known) drives the preview's tools instead of the program's comments.
    /// </summary>
    internal static async Task<NcViewerOpenRequest> ForReleaseAsync(
        IPlannerApiClient api,
        string caseId,
        string caseOperationId,
        string releaseId,
        string? machineId,
        string contextTitle,
        string? batchOperationId = null,
        CancellationToken cancellationToken = default, ProductionPackageContext? context = null)
    {
        var bytes = await api.ReadGCodeFileBytesAsync(caseId, caseOperationId, releaseId, cancellationToken);
        // The Operation's tool table drives the preview's tools instead of the program's comments:
        // with the Batch Operation known, the Tool Room's shapes and measurements come with it;
        // otherwise the released rows of the release's tool table alone.
        NcViewerOperationToolTable? operationToolTable = null;
        string? preparationReleaseId = null;
        if (batchOperationId is not null)
        {
            try
            {
                var preparation = await api.GetToolPreparationAsync(batchOperationId, cancellationToken, context);
                operationToolTable = NcViewerOperationToolTable.From(preparation);
                if (operationToolTable is not null) preparationReleaseId = preparation.ToolTableReleaseId;
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                // No released tool table or an offline Server: fall back to the catalog rows below.
            }
        }
        PlannerGCodeRelease? release = null;
        PlannerGCodeCatalog? catalog = null;
        try
        {
            catalog = await api.GetOperationGCodeAsync(caseId, caseOperationId, cancellationToken);
            release = catalog.Releases.FirstOrDefault(value => value.GCodeReleaseId == releaseId);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // The file is enough to show the program; the release details are optional.
        }
        operationToolTable ??= NcViewerOperationToolTable.FromCatalog(catalog, release?.ToolTableReleaseId);
        operationToolTable = await WithFileSizesAsync(api, caseId, caseOperationId, operationToolTable,
            preparationReleaseId ?? release?.ToolTableReleaseId, cancellationToken);
        var machines = await MachinesAsync(api, cancellationToken);
        var programFolders = NcProgramFolders.ForOperation(
            api,
            caseId,
            caseOperationId,
            release?.PostprocessorId,
            source: release is null
                ? null
                : new NcProgramRevision(release.ProcessRevisionNumber, release.PostprocessorId,
                    release.PostprocessorName, release.PostSpecificRevision));
        var subprogramFolder = await PlaceSubprogramsAsync(
            api, caseId, caseOperationId, release, programFolders, cancellationToken);
        var scene = await NcViewerScenes.ForOperationAsync(
            api, caseId, caseOperationId, machineId, machines, contextTitle, cancellationToken);
        return new NcViewerOpenRequest(
            contextTitle,
            release?.OriginalFileName ?? $"release-{releaseId[..Math.Min(8, releaseId.Length)]}.nc",
            NcTextFile.Decode(bytes),
            ReadOnly: true,
            SourceDescription: Describe(release),
            NcDialect: NcViewerDialects.Resolve(machines, machineId, release?.PostprocessorId),
            FormatService: FormatService(api),
            MachineSelection: NcViewerMachines.Resolve(machines, machineId, release?.PostprocessorId, scene.Compiled),
            ProgramFolders: programFolders,
            OperationToolTable: operationToolTable,
            SubprogramFolder: subprogramFolder,
            Scene: scene,
            WorkOffsets: NcViewerWorkOffsets.For(api, caseOperationId, releaseId, machineId, machines, release));
    }

    /// <summary>
    /// A release with subprogram files is shown with them: they are written into the release's
    /// revision folder in the Case Working Folder (where released NC programs belong) unless a file
    /// of that name is already there, which is never replaced. Returns that folder, or null when the
    /// release has no subprograms or the folder or the Server is unavailable (the preview then
    /// notes the subprograms it cannot find).
    /// </summary>
    private static async Task<string?> PlaceSubprogramsAsync(
        IPlannerApiClient api,
        string caseId,
        string caseOperationId,
        PlannerGCodeRelease? release,
        NcProgramFolders folders,
        CancellationToken cancellationToken)
    {
        if (release?.Subprograms is not { Count: > 0 } subprograms || folders.Source is not { } source) return null;
        try
        {
            var folder = (await folders.ReleaseFolderAsync(source, cancellationToken)).Path;
            foreach (var subprogram in subprograms)
            {
                var target = Path.Combine(folder, Path.GetFileName(subprogram.OriginalFileName));
                if (File.Exists(target)) continue;
                var bytes = await api.ReadGCodeSubprogramBytesAsync(
                    caseId, caseOperationId, release.GCodeReleaseId, subprogram.SubprogramId, cancellationToken);
                await File.WriteAllBytesAsync(target, bytes, cancellationToken);
            }
            return folder;
        }
        catch (Exception exception) when (IsTransient(exception) || exception is IOException
            or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds the sizes the released tool table file states (Cimatron's Dia and stick-out) to the tools
    /// the Tool Room has not measured. Without them, or with the Server unavailable, the table stays
    /// as it is and the sizes come from the descriptions.
    /// </summary>
    private static async Task<NcViewerOperationToolTable?> WithFileSizesAsync(
        IPlannerApiClient api,
        string caseId,
        string caseOperationId,
        NcViewerOperationToolTable? table,
        string? toolTableReleaseId,
        CancellationToken cancellationToken)
    {
        if (table is null || toolTableReleaseId is null) return table;
        try
        {
            var geometry = await api.GetToolTableGeometryAsync(caseId, caseOperationId, toolTableReleaseId, cancellationToken);
            return table.WithFileSizes(geometry.Tools);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            return table;
        }
    }

    /// <summary>
    /// An empty, editable program not tied to a Case Operation (main window "NC viewer" button): Open/Save work on local
    /// files, Home offsets stay on this PC, and with the Server connected the factory Machines' published Machine Designer
    /// definitions and 3D models are listed, and "Apply Meimad Planner Format" and the template check are available.
    /// Nothing is released from here.
    /// </summary>
    internal static async Task<NcViewerOpenRequest> StandaloneAsync(IPlannerApiClient? api, CancellationToken cancellationToken = default)
    {
        NcViewerSetupScene? scene = null;
        if (api is not null)
        {
            var machines = await MachinesAsync(api, cancellationToken);
            var compiled = await NcViewerMachineDefinitions.LoadAsync(api, cancellationToken: cancellationToken);
            scene = NcViewerSetupScene.Empty with
            {
                MachineModels = NcViewerScenes.WithCompiledModels(NcViewerScenes.MachineModels(machines, null), compiled),
                CompiledMachines = compiled
            };
        }
        return new NcViewerOpenRequest(
            "NC viewer",
            "Untitled.NC",
            NcViewerOpenRequest.NewDocument(NcViewerOpenRequest.BlankProgram),
            ReadOnly: false,
            SourceDescription: "Local NC program (not linked to a Case Operation)",
            FormatService: api is null ? null : FormatService(api),
            ValidateService: api is null ? null : (text, token) => api.ValidateNcTemplateAsync(text, token),
            Scene: scene);
    }

    /// <summary>The Server's stateless "Apply Meimad Planner Format".</summary>
    internal static Func<string, string, CancellationToken, Task<NcTemplateFormatResult>> FormatService(IPlannerApiClient api) =>
        (text, dialect, token) => api.FormatNcTemplateAsync(text, dialect, token);

    internal static async Task<IReadOnlyList<PlannerMachine>> MachinesAsync(
        IPlannerApiClient api, CancellationToken cancellationToken)
    {
        try
        {
            return await api.ListMachinesAsync(cancellationToken);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            return [];
        }
    }

    internal static string Describe(PlannerGCodeRelease? release) => release is null
        ? "Immutable Server release"
        : string.Create(CultureInfo.InvariantCulture,
            $"Server release: process r{release.ProcessRevisionNumber} · {release.PostprocessorName} r{release.PostSpecificRevision} · released {release.ReleasedAt.ToLocalTime():yyyy-MM-dd HH:mm} by {release.ReleasedBy} · SHA-256 {release.ShortHash}");

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or PlannerApiException or NotSupportedException;
}
