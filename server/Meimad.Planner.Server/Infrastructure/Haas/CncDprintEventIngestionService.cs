using System.Text.Json;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Domain.Cnc;

namespace Meimad.Planner.Server.Infrastructure.Haas;

/// <summary>
/// Atomically converts valid current Offset Loader DPRNT evidence into the shared raw event
/// stream and a one-time pending setup-verification session. Later milestones own response
/// projection and success/failure decisions.
/// </summary>
internal sealed class CncDprintEventIngestionService(
    ICncVerificationFoundationRepository verification,
    ProductionRunWorkflowEventService workflow,
    IProductionRunCncObservationRepository productionRuns,
    ILogger<CncDprintEventIngestionService> logger) : ICncRawTelemetryConsumer
{
    public async Task ConsumeAsync(
        string machineId, IReadOnlyList<RawCncTelemetry> telemetry, CancellationToken token)
    {
        foreach (var item in telemetry.Where(value => value.Operation == "DPRINT_EVENT"))
        {
            if (!HaasDprintProtocol.TryParse(item.RawPayload, out var parsed, out var parseError))
            {
                logger.LogWarning("Rejected malformed CNC DPRINT event. MachineId={MachineId} Error={Error}",
                    machineId, parseError);
                continue;
            }
            if (parsed!.EventType is "CYCLE_START" or "CYCLE_END")
            {
                var result = await productionRuns.ConsumeCycleEventAsync(new(
                    machineId, parsed.EventType, parsed.SourceEventId, parsed.Sequence,
                    parsed.MacroVersion, parsed.ProductionRunId, parsed.ProgramIdentity,
                    parsed.RawLine), token);
                if (!result.Accepted)
                    logger.LogWarning(
                        "Rejected CNC production-cycle event. MachineId={MachineId} EventType={EventType} EventId={EventId} Code={Code}",
                        machineId, parsed.EventType, parsed.SourceEventId, result.Code);
                continue;
            }
            if (parsed.EventType != "OFFSET_LOADER_COMPLETED")
            {
                logger.LogInformation("Deferred CNC DPRINT event until its workflow milestone. MachineId={MachineId} EventType={EventType}",
                    machineId, parsed.EventType);
                continue;
            }
            var context = await verification.ResolveCurrentOffsetLoaderAsync(
                machineId, parsed.OffsetReleaseToken!.Value, token);
            if (context is null)
            {
                logger.LogWarning("Rejected stale or unknown Offset Loader DPRINT event. MachineId={MachineId} ReleaseToken={ReleaseToken}",
                    machineId, parsed.OffsetReleaseToken);
                continue;
            }
            if (parsed.MacroVersion != context.ExpectedMacroVersion
                || parsed.ProductionRunId is not null
                   && parsed.ProductionRunId != context.ProductionRunId)
            {
                logger.LogWarning("Rejected mismatched Offset Loader DPRINT evidence. MachineId={MachineId} EventId={EventId}",
                    machineId, parsed.SourceEventId);
                continue;
            }
            var metadata = JsonSerializer.Serialize(new
            {
                nonce = parsed.Nonce,
                macroVersion = parsed.MacroVersion,
                programIdentity = parsed.ProgramIdentity,
                expectedNcIdentityToken = context.NcIdentityToken,
                rawLine = parsed.RawLine
            });
            await workflow.AppendAsync(new(
                context.ProductionRunId, machineId, parsed.EventType,
                $"HAAS_DPRINT:{machineId}", parsed.SourceEventId, parsed.Sequence,
                NcReleaseId: context.NcReleaseId,
                OffsetLoaderReleaseId: context.OffsetLoaderReleaseId,
                MetadataJson: metadata,
                VerificationSession: new(parsed.Nonce!.Value, parsed.MacroVersion,
                    context.ResponseCodeDigits, context.VerificationTimeoutSeconds)), token);
        }
    }
}
