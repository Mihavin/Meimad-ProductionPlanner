using System.Globalization;
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
    /// viewer machine.
    /// </summary>
    internal static async Task<NcViewerOpenRequest> ForReleaseAsync(
        IPlannerApiClient api,
        string caseId,
        string caseOperationId,
        string releaseId,
        string? machineId,
        string contextTitle,
        CancellationToken cancellationToken = default)
    {
        var bytes = await api.ReadGCodeFileBytesAsync(caseId, caseOperationId, releaseId, cancellationToken);
        PlannerGCodeRelease? release = null;
        try
        {
            var catalog = await api.GetOperationGCodeAsync(caseId, caseOperationId, cancellationToken);
            release = catalog.Releases.FirstOrDefault(value => value.GCodeReleaseId == releaseId);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // The file is enough to show the program; the release details are optional.
        }
        var machines = await MachinesAsync(api, cancellationToken);
        return new NcViewerOpenRequest(
            contextTitle,
            release?.OriginalFileName ?? $"release-{releaseId[..Math.Min(8, releaseId.Length)]}.nc",
            NcTextFile.Decode(bytes),
            ReadOnly: true,
            SourceDescription: Describe(release),
            NcDialect: NcViewerDialects.Resolve(machines, machineId, release?.PostprocessorId),
            FormatService: FormatService(api),
            MachineSelection: NcViewerMachines.Resolve(machines, machineId, release?.PostprocessorId),
            ProgramFolders: NcProgramFolders.ForOperation(
                api,
                caseId,
                caseOperationId,
                release?.PostprocessorId,
                source: release is null
                    ? null
                    : new NcProgramRevision(release.ProcessRevisionNumber, release.PostprocessorId,
                        release.PostprocessorName, release.PostSpecificRevision)));
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
