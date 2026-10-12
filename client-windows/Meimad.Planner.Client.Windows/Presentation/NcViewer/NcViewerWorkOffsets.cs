using System.Net.Http;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation.NcViewer;

/// <summary>
/// What the NC viewer's "Measured work offset" card (NcViewer/meimad-work-offsets.js) shows for the
/// opened release: the Case Operation, the release and, when the caller knows the assignment, the
/// Machine. <see cref="Load"/> reads the measurements from the Server (schema v116); it never writes.
/// </summary>
internal sealed record NcViewerWorkOffsetContext(
    string CaseOperationId,
    string? MachineId,
    string? MachineLabel,
    string? ReleaseId,
    int? NcIdentityToken,
    string? ReleaseLabel,
    Func<CancellationToken, Task<IReadOnlyList<WorkOffsetRecordInfo>>> Load);

/// <summary>Builds and answers the viewer's measured work offset requests.</summary>
internal static class NcViewerWorkOffsets
{
    /// <summary>
    /// The card's context for a release opened from a Case Operation. With a Machine the card reads that
    /// Machine's measurements of the Operation; without one it reads the current and waiting
    /// measurements of the Operation on every Machine.
    /// </summary>
    internal static NcViewerWorkOffsetContext For(
        IPlannerApiClient api,
        string caseOperationId,
        string? releaseId,
        string? machineId,
        IReadOnlyList<PlannerMachine> machines,
        PlannerGCodeRelease? release)
    {
        var machine = machineId is null ? null : machines.FirstOrDefault(value => value.MachineId == machineId);
        return new NcViewerWorkOffsetContext(
            caseOperationId,
            machineId,
            machine is null ? machineId : string.IsNullOrWhiteSpace(machine.Number) || machine.Number == machine.Name
                ? machine.Name : $"{machine.Number} - {machine.Name}",
            releaseId,
            release?.VerificationHook?.NcIdentityToken,
            release is null ? null : $"process r{release.ProcessRevisionNumber} · {release.PostprocessorName} r{release.PostSpecificRevision}",
            async token => machineId is null
                ? await api.ListWorkOffsetsAsync("VALID,REVIEW_REQUIRED", null, caseOperationId, 200, token)
                : (await api.GetOperationWorkOffsetsAsync(machineId, caseOperationId, null, 200, token)).History);
    }

    /// <summary>The page's <c>meimadWorkOffsets</c> answer: the context and the measurements, newest first, or why there are none.</summary>
    internal static async Task<object> LoadAsync(NcViewerWorkOffsetContext? context, CancellationToken token = default)
    {
        if (context is null)
            return new { available = false, reason = "Open the program from a Case Operation to see measured work offsets." };
        IReadOnlyList<WorkOffsetRecordInfo> records;
        string? error = null;
        try
        {
            records = await context.Load(token);
        }
        catch (Exception exception) when (exception is PlannerApiException or PlannerProtocolException
            or HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            records = [];
            error = exception is HttpRequestException or TaskCanceledException
                ? "The Server could not be reached, so measured work offsets are not shown."
                : exception.Message;
        }
        return new
        {
            available = true,
            error,
            caseOperationId = context.CaseOperationId,
            machineId = context.MachineId,
            machineLabel = context.MachineLabel,
            releaseId = context.ReleaseId,
            ncIdentityToken = context.NcIdentityToken,
            releaseLabel = context.ReleaseLabel,
            records = records.Select(record => new
            {
                id = record.WorkOffsetId,
                machineId = record.MachineId,
                machineLabel = record.MachineText,
                workOffset = record.WorkOffset,
                x = record.PositionMm.X,
                y = record.PositionMm.Y,
                z = record.PositionMm.Z,
                a = record.RotationDeg.A,
                b = record.RotationDeg.B,
                c = record.RotationDeg.C,
                units = record.Units,
                source = record.Source,
                measuredAt = record.MeasuredAt,
                receivedAt = record.ReceivedAt,
                receivedBy = record.ReceivedBy,
                status = record.Status,
                statusReason = record.StatusReason,
                reasonText = record.ReasonText,
                ncReleaseId = record.NcReleaseId,
                ncReleaseLabel = record.NcReleaseLabel,
                ncIdentityToken = record.NcIdentityToken,
                ncReleaseIsCurrent = record.NcReleaseIsCurrent,
                reviewedBy = record.ReviewedBy,
                rawMessage = record.RawMessage
            }).ToArray()
        };
    }
}
