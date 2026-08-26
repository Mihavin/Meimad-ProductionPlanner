namespace Meimad.Planner.Server.Application.ProductionRuns;

internal interface IProductionRunCncObservationRepository
{
    Task<CncCycleObservationResult> ConsumeCycleEventAsync(
        CncCycleObservation observation, CancellationToken token);
}

internal sealed record CncCycleObservation(
    string MachineId, string EventType, string SourceEventId, long Sequence,
    int MacroVersion, string? ProductionRunIdentity, string? ProgramIdentity,
    string RawLine);

internal sealed record CncCycleObservationResult(
    bool Accepted, bool WasDuplicate, bool CycleCompleted, string Code,
    string? ProductionRunId = null, string? ProductionRunProgramId = null,
    int? CompletedCycleCount = null);
