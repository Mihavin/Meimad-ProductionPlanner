using System.Collections.Concurrent;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Domain.Cnc;

namespace Meimad.Planner.Server.Application.MachineEvidence;

/// <summary>
/// The <c>WORK_OFFSET</c> evidence type of the existing DPRNT pipeline. Every adapter (Haas NGC, FANUC
/// FOCAS, DPRNT-only Mazak and Okuma) already turns the <c>MEIMAD/</c> lines of its configured DPRNT
/// source - TCP (controller or serial-to-Ethernet bridge), FILE (for example the Mazak
/// <c>print.txt</c>), FTP or SERIAL - into <c>DPRINT_EVENT</c> telemetry; this consumer feeds the
/// <c>MEIMAD/OFFSET...</c> lines of each Machine through its own <see cref="OffsetEvidenceParser"/> and
/// stores every finished frame through <see cref="WorkOffsetIngestService"/>. The parser keeps its open
/// session across polls, so a frame split over several reads is joined; an unfinished frame is never
/// stored. PartName, Offset Loader and workflow events are not touched: the
/// <c>CncDprintEventIngestionService</c> skips work offset lines.
/// </summary>
internal sealed class WorkOffsetDprntConsumer(
    WorkOffsetIngestService ingest,
    IWorkOffsetRepository repository,
    ILogger<WorkOffsetDprntConsumer> logger) : ICncRawTelemetryConsumer
{
    private readonly ConcurrentDictionary<string, Channel> channels = new(StringComparer.Ordinal);

    public async Task ConsumeAsync(string machineId, IReadOnlyList<RawCncTelemetry> telemetry, CancellationToken cancellationToken)
    {
        foreach (var item in telemetry)
        {
            if (item.Operation != "DPRINT_EVENT" || !DprntLineNormalizer.IsWorkOffsetLine(item.RawPayload)) continue;
            var channel = channels.GetOrAdd(machineId + "|" + item.ConnectionId, _ => new Channel());
            OffsetEvidenceParseResult result;
            lock (channel) result = channel.Parser.Accept(item.RawPayload);
            switch (result.Outcome)
            {
                case OffsetEvidenceParseOutcome.Completed or OffsetEvidenceParseOutcome.Rejected:
                    await StoreAsync(machineId, item.ConnectionId, result.Message!, cancellationToken);
                    break;
                case OffsetEvidenceParseOutcome.RefusedLine:
                    logger.LogWarning(
                        "Refused a work offset DPRNT line. MachineId={MachineId} Code={Code} Session={Session}",
                        machineId, result.Code, result.Session);
                    break;
                case OffsetEvidenceParseOutcome.Discarded:
                    logger.LogWarning(
                        "Dropped an incomplete work offset DPRNT session; nothing was stored. MachineId={MachineId} Code={Code} Session={Session}",
                        machineId, result.Code, result.Session);
                    break;
            }
        }
    }

    private async Task StoreAsync(
        string machineId, string connectionId, WorkOffsetMessage message, CancellationToken token)
    {
        // Read when a frame finishes (rare), so a changed DPRNT source is named correctly at once.
        var source = await SourceAsync(machineId, token);
        var evidence = WorkOffsetEvidence.FromMessage(message, machineId, source, "DPRNT:" + connectionId);
        try
        {
            var stored = await ingest.IngestAsync(evidence, token);
            if (stored.Duplicate)
                logger.LogInformation(
                    "Ignored a repeated work offset DPRNT message. MachineId={MachineId} Session={Session}",
                    machineId, message.Session);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A storage fault must not stop the Machine's poll loop; the controller can print the report again.
            logger.LogError(exception,
                "A work offset DPRNT message could not be stored. MachineId={MachineId} Session={Session}",
                machineId, message.Session);
        }
    }

    private async Task<string> SourceAsync(string machineId, CancellationToken token)
    {
        var configured = await repository.GetDprntSourceAsync(machineId, token);
        return configured switch
        {
            CncDprntSources.Tcp => WorkOffsetSources.DprntTcp,
            CncDprntSources.File => WorkOffsetSources.DprntFile,
            CncDprntSources.Ftp => WorkOffsetSources.DprntFtp,
            "SERIAL" => WorkOffsetSources.DprntSerial,
            _ => WorkOffsetSources.Dprnt
        };
    }

    private sealed class Channel
    {
        internal OffsetEvidenceParser Parser { get; } = new();
    }
}
