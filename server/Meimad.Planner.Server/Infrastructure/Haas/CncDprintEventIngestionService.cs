using System.Text.Json;
using Meimad.Planner.Server.Application.Anomalies;
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
    OperationalAnomalyService anomalies,
    TimeProvider timeProvider,
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
                if (parsed.ProgramIdentity is null)
                {
                    await TrackAsync(
                        "active_nc_identity_unavailable", machineId,
                        parsed.ProductionRunId, parsed.SourceEventId,
                        "program_identity_missing", token);
                    logger.LogWarning(
                        "Rejected CNC production-cycle event without NC identity. MachineId={MachineId} EventType={EventType} EventId={EventId}",
                        machineId, parsed.EventType, parsed.SourceEventId);
                    continue;
                }
                var result = await productionRuns.ConsumeCycleEventAsync(new(
                    machineId, parsed.EventType, parsed.SourceEventId, parsed.Sequence,
                    parsed.MacroVersion, parsed.ProductionRunId, parsed.ProgramIdentity,
                    parsed.RawLine), token);
                if (result.WasDuplicate)
                    await TrackAsync(
                        "duplicate_cnc_event", machineId, result.ProductionRunId,
                        parsed.SourceEventId, "duplicate", token);
                else if (!result.Accepted)
                {
                    var anomalyType = result.Code switch
                    {
                        "cycle_start_requires_qc_pass_or_completed_cycle" =>
                            "cycle_started_before_qc_pass",
                        "cycle_target_ambiguous" => "ambiguous_production_run",
                        "cycle_target_unresolved" when parsed.ProductionRunId is not null =>
                            "unknown_production_run",
                        "cycle_target_unresolved" when parsed.ProgramIdentity is not null =>
                            "wrong_nc_program",
                        "cycle_target_unresolved" => "active_nc_identity_unavailable",
                        "source_event_conflict" => "duplicate_cnc_event",
                        _ => (string?)null
                    };
                    if (anomalyType is not null)
                        await TrackAsync(
                            anomalyType, machineId,
                            result.ProductionRunId ?? parsed.ProductionRunId,
                            parsed.SourceEventId, result.Code, token);
                    logger.LogWarning(
                        "Rejected CNC production-cycle event. MachineId={MachineId} EventType={EventType} EventId={EventId} Code={Code}",
                        machineId, parsed.EventType, parsed.SourceEventId, result.Code);
                }
                else if (result.Code == "cycle_end_unmatched")
                    logger.LogWarning(
                        "Retained unmatched CNC CYCLE_END as workflow anomaly. MachineId={MachineId} EventId={EventId} Sequence={Sequence}",
                        machineId, parsed.SourceEventId, parsed.Sequence);
                continue;
            }
            if (parsed.EventType is "SETUP_VERIFICATION_SUCCEEDED" or "SETUP_VERIFICATION_FAILED")
            {
                var detectedAt = timeProvider.GetUtcNow();
                var pending = await verification.ResolvePendingVerificationAsync(
                    machineId, parsed.SourceEventId, detectedAt, token);
                if (pending is null)
                {
                    await TrackAsync(
                        "offset_loader_not_executed", machineId, parsed.ProductionRunId,
                        parsed.SourceEventId, "no_verification_session", token);
                    logger.LogWarning(
                        "Rejected CNC verification result without a current pending session. MachineId={MachineId} EventId={EventId}",
                        machineId, parsed.SourceEventId);
                    continue;
                }
                if (parsed.OffsetReleaseToken != pending.VerificationReleaseToken)
                {
                    await TrackAsync(
                        "stale_offset_loader", machineId, pending.ProductionRunId,
                        parsed.SourceEventId, "verification_release_token_mismatch", token);
                    continue;
                }
                if (parsed.Nonce != pending.Nonce)
                {
                    await TrackAsync(
                        "offset_loader_not_executed", machineId, pending.ProductionRunId,
                        parsed.SourceEventId, "verification_challenge_mismatch", token);
                    continue;
                }
                if (pending.WasDuplicate)
                {
                    await TrackAsync(
                        "duplicate_cnc_event", machineId, pending.ProductionRunId,
                        parsed.SourceEventId, "duplicate", token);
                    continue;
                }
                if (pending.SessionState != "PENDING")
                {
                    var anomalyType = pending.SessionState switch
                    {
                        "SUPERSEDED" => "stale_offset_loader",
                        "SUCCEEDED" or "FAILED" => "duplicate_cnc_event",
                        _ => (string?)null
                    };
                    if (anomalyType is not null)
                        await TrackAsync(
                            anomalyType, machineId, pending.ProductionRunId,
                            parsed.SourceEventId, "verification_session_not_pending", token);
                    logger.LogWarning(
                        "Rejected CNC verification result for a non-pending session. MachineId={MachineId} EventId={EventId} SessionState={SessionState}",
                        machineId, parsed.SourceEventId, pending.SessionState);
                    continue;
                }
                if (parsed.MacroVersion != pending.ExpectedMacroVersion
                    || parsed.MacroVersion != pending.MacroVersion)
                {
                    await TrackAsync(
                        "verification_macro_version_mismatch", machineId,
                        pending.ProductionRunId, parsed.SourceEventId,
                        "macro_version_mismatch", token);
                    continue;
                }
                if (parsed.ProductionRunId is not null
                    && parsed.ProductionRunId != pending.ProductionRunId)
                {
                    await TrackAsync(
                        "unknown_production_run", machineId, parsed.ProductionRunId,
                        parsed.SourceEventId, "run_identity_mismatch", token);
                    continue;
                }
                if (parsed.ProgramIdentity is null)
                {
                    await TrackAsync(
                        "active_nc_identity_unavailable", machineId,
                        pending.ProductionRunId, parsed.SourceEventId,
                        "program_identity_missing", token);
                    continue;
                }
                if (parsed.ProgramIdentity != pending.NcIdentityToken.ToString(
                    System.Globalization.CultureInfo.InvariantCulture))
                {
                    await TrackAsync(
                        "wrong_nc_program", machineId, pending.ProductionRunId,
                        parsed.SourceEventId, "nc_identity_mismatch", token);
                    continue;
                }
                var succeeded = parsed.EventType == "SETUP_VERIFICATION_SUCCEEDED";
                await workflow.AppendAsync(new(
                    pending.ProductionRunId, machineId, parsed.EventType,
                    $"HAAS_DPRINT:{machineId}", parsed.SourceEventId, parsed.Sequence,
                    NcReleaseId: pending.NcReleaseId,
                    OffsetLoaderReleaseId: pending.OffsetLoaderReleaseId,
                    MetadataJson: JsonSerializer.Serialize(new
                    {
                        macroVersion = parsed.MacroVersion,
                        programIdentity = parsed.ProgramIdentity,
                        offsetReleaseToken = parsed.OffsetReleaseToken,
                        nonce = parsed.Nonce,
                        rawLine = parsed.RawLine
                    }),
                    VerificationResolution: new(pending.SessionId, succeeded)), token);
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
                await TrackAsync(
                    "stale_offset_loader", machineId, parsed.ProductionRunId,
                    parsed.SourceEventId, "offset_loader_not_current", token);
                logger.LogWarning("Rejected stale or unknown Offset Loader DPRINT event. MachineId={MachineId} ReleaseToken={ReleaseToken}",
                    machineId, parsed.OffsetReleaseToken);
                continue;
            }
            if (parsed.MacroVersion != context.ExpectedMacroVersion)
            {
                await TrackAsync(
                    "verification_macro_version_mismatch", machineId,
                    context.ProductionRunId, parsed.SourceEventId,
                    "macro_version_mismatch", token);
                logger.LogWarning("Rejected mismatched Offset Loader DPRINT evidence. MachineId={MachineId} EventId={EventId}",
                    machineId, parsed.SourceEventId);
                continue;
            }
            if (parsed.ProductionRunId is not null
                && parsed.ProductionRunId != context.ProductionRunId)
            {
                await TrackAsync(
                    "unknown_production_run", machineId, parsed.ProductionRunId,
                    parsed.SourceEventId, "run_identity_mismatch", token);
                logger.LogWarning("Rejected mismatched Offset Loader DPRINT evidence. MachineId={MachineId} EventId={EventId}",
                    machineId, parsed.SourceEventId);
                continue;
            }
            if (parsed.ProgramIdentity is null)
            {
                await TrackAsync(
                    "active_nc_identity_unavailable", machineId,
                    context.ProductionRunId, parsed.SourceEventId,
                    "program_identity_missing", token);
                logger.LogWarning(
                    "Rejected Offset Loader DPRINT evidence without NC identity. MachineId={MachineId} EventId={EventId}",
                    machineId, parsed.SourceEventId);
                continue;
            }
            if (parsed.ProgramIdentity != context.NcIdentityToken.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            {
                await TrackAsync(
                    "wrong_nc_program", machineId, context.ProductionRunId,
                    parsed.SourceEventId, "nc_identity_mismatch", token);
                logger.LogWarning(
                    "Rejected Offset Loader DPRINT evidence with mismatched NC identity. MachineId={MachineId} EventId={EventId}",
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
            var retained = await workflow.AppendAsync(new(
                context.ProductionRunId, machineId, parsed.EventType,
                $"HAAS_DPRINT:{machineId}", parsed.SourceEventId, parsed.Sequence,
                NcReleaseId: context.NcReleaseId,
                OffsetLoaderReleaseId: context.OffsetLoaderReleaseId,
                MetadataJson: metadata,
                VerificationSession: new(parsed.Nonce!.Value, parsed.MacroVersion,
                    context.ResponseCodeDigits, context.VerificationTimeoutSeconds)), token);
            if (retained.WasDuplicate)
                await TrackAsync(
                    "duplicate_cnc_event", machineId, context.ProductionRunId,
                    parsed.SourceEventId, "duplicate", token);
        }
    }

    private Task TrackAsync(
        string anomalyType,
        string machineId,
        string? productionRunId,
        string sourceEventId,
        string code,
        CancellationToken cancellationToken) => anomalies.AppendAsync(new(
            anomalyType,
            $"HAAS_DPRINT:{machineId}",
            $"cnc:{machineId}:{sourceEventId}:{anomalyType}",
            timeProvider.GetUtcNow(),
            machineId,
            productionRunId,
            SourceEventId: sourceEventId,
            DetailsJson: JsonSerializer.Serialize(new { code })), cancellationToken);
}
