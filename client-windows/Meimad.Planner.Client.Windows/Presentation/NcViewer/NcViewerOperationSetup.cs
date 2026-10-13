using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation.NcViewer;

/// <summary>
/// The opened Case Operation's placement in the NC viewer, kept on the Server as the one source every user's viewer reads
/// (owner decision 2026-10-13): the work offsets per NC viewer machine — absolute machine coordinates of G54 … (schema
/// v120), which place the program zero, and with it the toolpath, on the machine — and the Operation's Cimatron Work CS
/// (schema v113), which places the Cimatron geometry relative to that work offset. Both are edited in the viewer and saved
/// with their entity tags, so a change based on old values is refused with who changed them.
/// </summary>
internal sealed class NcViewerOperationSetup
{
    private readonly IPlannerApiClient api;
    private readonly Dictionary<string, OperationViewerWorkOffsets> workOffsets;
    private readonly object gate = new();

    private NcViewerOperationSetup(
        IPlannerApiClient api,
        string caseId,
        string caseOperationId,
        IEnumerable<OperationViewerWorkOffsets> offsets,
        CaseOperationCamSetupResource? workCs)
    {
        this.api = api;
        CaseId = caseId;
        CaseOperationId = caseOperationId;
        workOffsets = offsets.ToDictionary(item => item.ViewerMachine, StringComparer.Ordinal);
        WorkCsResource = workCs;
    }

    internal string CaseId { get; }

    internal string CaseOperationId { get; }

    internal CaseOperationCamSetupResource? WorkCsResource { get; private set; }

    internal CaseOperationCamSetup? WorkCs => WorkCsResource?.Value;

    /// <summary>
    /// The Operation's placement, or null when the Server cannot give its work offsets (offline, or a Server older than
    /// schema v120): the viewer then keeps the Home offsets saved on this PC.
    /// </summary>
    internal static async Task<NcViewerOperationSetup?> LoadAsync(
        IPlannerApiClient api, string caseId, string caseOperationId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OperationViewerWorkOffsets> offsets;
        try
        {
            offsets = await api.ListOperationViewerWorkOffsetsAsync(caseId, caseOperationId, cancellationToken);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            return null;
        }
        CaseOperationCamSetupResource? workCs = null;
        try
        {
            workCs = await api.GetCaseOperationCamSetupAsync(caseId, caseOperationId, cancellationToken);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // The geometry is drawn at its own coordinates; the work offsets still apply.
        }
        return new NcViewerOperationSetup(api, caseId, caseOperationId, offsets, workCs);
    }

    internal static NcViewerOperationSetup ForTesting(
        IPlannerApiClient api, string caseId, string caseOperationId,
        IEnumerable<OperationViewerWorkOffsets> offsets, CaseOperationCamSetupResource? workCs = null) =>
        new(api, caseId, caseOperationId, offsets, workCs);

    /// <summary>The work offsets in the engine's form: <c>{viewerMachine: {"G54": {"X": …}}}</c>.</summary>
    internal JsonElement EngineWorkOffsets()
    {
        lock (gate)
        {
            return JsonSerializer.SerializeToElement(workOffsets.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Offsets,
                StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Saves the work offsets of one viewer machine (the engine's sanitized rows; none deletes them) and returns the status
    /// text. A change refused because someone saved first reloads the current values and says who changed them.
    /// </summary>
    internal async Task<(bool Saved, string Message)> SaveWorkOffsetsAsync(
        string viewerMachine, string machineName, JsonElement rows, CancellationToken cancellationToken = default)
    {
        var machine = viewerMachine.Trim().ToLowerInvariant();
        var values = Rows(rows);
        OperationViewerWorkOffsets? current;
        lock (gate) workOffsets.TryGetValue(machine, out current);
        try
        {
            if (values.Count == 0)
            {
                if (current is null) return (true, $"{machineName}: no work offsets saved for this Operation.");
                await api.DeleteOperationViewerWorkOffsetsAsync(CaseId, CaseOperationId, machine, current.EntityTag, cancellationToken);
                lock (gate) workOffsets.Remove(machine);
                return (true, $"{machineName} work offsets of this Operation cleared on the Server, for every user.");
            }
            var saved = await api.SaveOperationViewerWorkOffsetsAsync(
                CaseId, CaseOperationId, machine, values, current?.EntityTag, cancellationToken);
            lock (gate) workOffsets[machine] = saved;
            return (true, $"{machineName} work offsets of this Operation saved on the Server, for every user.");
        }
        catch (PlannerApiException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            await ReloadWorkOffsetsAsync(cancellationToken);
            return (false, Conflict(exception, "work offsets"));
        }
    }

    /// <summary>Saves the Operation's Work CS (creates it when the Operation has none) and returns the saved value.</summary>
    internal async Task<(CaseOperationCamSetup? Saved, string Message)> SaveWorkCsAsync(
        CaseOperationCamSetupWrite value, CancellationToken cancellationToken = default)
    {
        try
        {
            var saved = await api.SaveCaseOperationCamSetupAsync(
                CaseId, CaseOperationId, value, WorkCsResource?.EntityTag, string.Empty, 0, cancellationToken);
            WorkCsResource = saved;
            return (saved.Value, "Work CS of this Operation saved on the Server, for every user.");
        }
        catch (PlannerApiException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            try
            {
                WorkCsResource = await api.GetCaseOperationCamSetupAsync(CaseId, CaseOperationId, cancellationToken);
            }
            catch (Exception reload) when (IsTransient(reload))
            {
                // The message below still explains the refusal.
            }
            return (null, Conflict(exception, "Work CS"));
        }
    }

    private async Task ReloadWorkOffsetsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var items = await api.ListOperationViewerWorkOffsetsAsync(CaseId, CaseOperationId, cancellationToken);
            lock (gate)
            {
                workOffsets.Clear();
                foreach (var item in items) workOffsets[item.ViewerMachine] = item;
            }
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // Keep what this window had; the next save is refused again until the Server answers.
        }
    }

    private static string Conflict(PlannerApiException exception, string what)
    {
        var who = exception.Conflict?.ChangedBy is { Length: > 0 } changedBy ? $" by {changedBy}" : string.Empty;
        var when = exception.Conflict?.ChangedAt is { } changedAt
            ? " at " + changedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : string.Empty;
        return $"Not saved: the {what} of this Operation were changed{who}{when} after this window opened. " +
               "The current values are loaded now; check them, then make your change again.";
    }

    /// <summary>Label → axis → value from the page's rows; non-numbers are left out.</summary>
    internal static Dictionary<string, IReadOnlyDictionary<string, double>> Rows(JsonElement rows)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);
        if (rows.ValueKind != JsonValueKind.Object) return result;
        foreach (var row in rows.EnumerateObject())
        {
            if (row.Value.ValueKind != JsonValueKind.Object) continue;
            var axes = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var axis in row.Value.EnumerateObject())
            {
                if (axis.Value.ValueKind == JsonValueKind.Number && axis.Value.TryGetDouble(out var number) && double.IsFinite(number))
                    axes[axis.Name.ToUpperInvariant()] = number;
            }
            if (axes.Count > 0) result[row.Name] = axes;
        }
        return result;
    }

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or PlannerApiException or NotSupportedException;
}
