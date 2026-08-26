using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Domain.Haas;

namespace Meimad.Planner.Server.Application.Cnc;

/// <summary>Consumes only normalized CNC state; vendor protocol details never enter Bench rules.</summary>
internal sealed class BenchAutomationService(IHaasIntegrationRepository repository) : ICncSnapshotConsumer
{
    public async Task<CncSnapshotConsumptionResult> ConsumeAsync(
        MachineSnapshot snapshot, CancellationToken token)
    {
        if (snapshot.ConnectionStatus is not (CncConnectionStates.Online or CncConnectionStates.Degraded)
            || snapshot.LastSeenAt is null)
            return CncSnapshotConsumptionResult.None;

        var currentCounter = snapshot.PartCounter;
        var partCounter = !currentCounter.Stale && currentCounter.ReadAt is not null
            ? currentCounter.Value
            : null;
        var previous = await repository.GetSnapshotAsync(snapshot.MachineId, token);
        var normalized = new HaasMachineSnapshot(
            snapshot.MachineId,
            snapshot.Timestamp,
            HaasConnectivityStates.Online,
            snapshot.MachineState.Value,
            snapshot.Program.ProgramNumber.Value,
            snapshot.Program.PartName.Stale ? null : snapshot.Program.PartName.Value,
            snapshot.Program.HeaderSourcePath.Stale ? null : snapshot.Program.HeaderSourcePath.Value,
            snapshot.Program.PartName.Stale ? null : snapshot.Program.PartName.ReadAt,
            partCounter,
            null,
            snapshot.LastError,
            snapshot.LastSeenAt,
            previous?.Version + 1 ?? 1);
        var result = await repository.ApplyObservationAsync(normalized, snapshot.Timestamp, token);
        // Controller counters remain diagnostics only. Official output advances only
        // from a validated DPRINT CYCLE_START/CYCLE_END pair after QC approval.
        return new(result.CreatedEventTypes);
    }
}
