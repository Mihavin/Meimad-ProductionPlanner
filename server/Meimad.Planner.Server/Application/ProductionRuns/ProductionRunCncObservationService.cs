namespace Meimad.Planner.Server.Application.ProductionRuns;

internal interface IProductionRunCncObservationRepository
{
    Task<IReadOnlyList<string>> ConsumeCounterAsync(string machineId, string? partName,
        string? programNumber, int? previousCounter, int currentCounter,
        DateTimeOffset observedAt, CancellationToken token);
}
