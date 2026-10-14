using System.Net.Http;
using System.Net.Http.Json;

namespace Meimad.Planner.Client.Windows.Api;

internal partial interface IPlannerApiClient
{
    /// <summary>The Work Order's quantity, suggested part price, route resources with hourly prices and saved target time.</summary>
    Task<WorkOrderTargetTimeOverview> GetWorkOrderTargetTimeAsync(string batchId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>Calculates the target time without saving it.</summary>
    Task<TargetTimeCalculation> CalculateWorkOrderTargetTimeAsync(string batchId, WorkOrderTargetTimeInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>Creates (entity tag null) or replaces the Work Order's target time; the Server calculates what it stores.</summary>
    Task<WorkOrderTargetTime> SaveWorkOrderTargetTimeAsync(string batchId, WorkOrderTargetTimeInput input, string? entityTag, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task DeleteWorkOrderTargetTimeAsync(string batchId, string entityTag, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed partial class PlannerApiClient
{
    private static string TargetTimePath(string batchId) => $"api/v1/batches/{Uri.EscapeDataString(batchId)}/target-time";

    public async Task<WorkOrderTargetTimeOverview> GetWorkOrderTargetTimeAsync(string batchId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(TargetTimePath(batchId), cancellationToken);
        return await ReadSuccessAsync<WorkOrderTargetTimeOverview>(response, cancellationToken);
    }

    public async Task<TargetTimeCalculation> CalculateWorkOrderTargetTimeAsync(string batchId, WorkOrderTargetTimeInput input, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(TargetTimePath(batchId) + "/calculate", input, JsonOptions, cancellationToken);
        return await ReadSuccessAsync<TargetTimeCalculation>(response, cancellationToken);
    }

    public async Task<WorkOrderTargetTime> SaveWorkOrderTargetTimeAsync(string batchId, WorkOrderTargetTimeInput input, string? entityTag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, TargetTimePath(batchId));
        if (entityTag is null) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        else request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Content = JsonContent.Create(input, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<WorkOrderTargetTime>(response, cancellationToken);
    }

    public async Task DeleteWorkOrderTargetTimeAsync(string batchId, string entityTag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, TargetTimePath(batchId));
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessWithoutBodyAsync(response, cancellationToken);
    }
}

/// <summary>The planner's inputs: the price of one part and the material cost of the whole Work Order, in NIS.</summary>
internal sealed record WorkOrderTargetTimeInput(double PartPriceNis, double MaterialTotalNis, string? Notes = null);

/// <summary>
/// One resource of the route: the hourly price used and the target hours the whole Work Order margin buys at it (each
/// resource on its own), the same per part in minutes, and the hours its operations are planned to take.
/// <see cref="Kind"/> is MACHINE, MACHINE_TYPE (not assigned yet) or WORKSTATION_TYPE.
/// </summary>
internal sealed record TargetTimeResource(
    string Kind,
    string? ResourceId,
    string Name,
    double HourlyRateNis,
    bool DefaultRate,
    double TargetHours,
    double TargetMinutesPerPart,
    double PlannedHours,
    IReadOnlyList<int> OperationNumbers);

internal sealed record TargetTimeCalculation(
    double PartPriceNis, double MaterialTotalNis, int Quantity, double MarginNis, IReadOnlyList<TargetTimeResource> Resources);

internal sealed record WorkOrderTargetTime(
    string ProductionBatchId,
    TargetTimeCalculation Calculation,
    string? Notes,
    int Version,
    string EntityTag,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

internal sealed record TargetTimeResourcePrice(string Kind, string? ResourceId, string Name, double HourlyRateNis, bool DefaultRate, IReadOnlyList<int> OperationNumbers);

internal sealed record WorkOrderTargetTimeOverview(
    string ProductionBatchId,
    string BatchNumber,
    int Quantity,
    double? SuggestedPartPriceNis,
    double MachineDefaultHourlyRateNis,
    double WorkstationDefaultHourlyRateNis,
    IReadOnlyList<TargetTimeResourcePrice> Resources,
    WorkOrderTargetTime? Saved);
