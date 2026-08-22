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
        if (snapshot.ConnectionStatus is not (CncConnectionStates.Online or CncConnectionStates.Degraded))
            return CncSnapshotConsumptionResult.None;
        var previous = await repository.GetSnapshotAsync(snapshot.MachineId, token);
        var variable = snapshot.Production.ModeVariableValue.Value ?? previous?.ProductionVariableValue;
        if (variable is null) return CncSnapshotConsumptionResult.None;
        var normalized = new HaasMachineSnapshot(
            snapshot.MachineId,
            snapshot.Timestamp,
            HaasConnectivityStates.Online,
            snapshot.MachineState.Value,
            snapshot.Program.ProgramNumber.Value,
            snapshot.Program.PartName.Stale ? null : snapshot.Program.PartName.Value,
            snapshot.Program.HeaderSourcePath.Stale ? null : snapshot.Program.HeaderSourcePath.Value,
            snapshot.Program.PartName.Stale ? null : snapshot.Program.PartName.ReadAt,
            snapshot.Production.ModeVariableNumber ?? previous?.ProductionVariableNumber ?? 10605,
            variable.Value,
            previous?.ProductionVariableChangedAt,
            snapshot.PartCounter.Value ?? previous?.PartCounter,
            null,
            snapshot.LastError,
            snapshot.LastSeenAt,
            previous?.Version + 1 ?? 1);
        var result = await repository.ApplyObservationAsync(normalized, snapshot.Timestamp, token);
        return new(result.CreatedEventTypes);
    }
}
