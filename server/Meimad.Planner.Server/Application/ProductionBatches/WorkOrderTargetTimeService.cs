using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Machines;

namespace Meimad.Planner.Server.Application.ProductionBatches;

/// <summary>Resource kinds of a Work Order target time.</summary>
internal static class TargetTimeResourceKinds
{
    /// <summary>The Machine an operation is assigned to (or ran on).</summary>
    internal const string Machine = "MACHINE";
    /// <summary>An operation not assigned to a Machine yet: the Machine type it needs, at the Machine default price.</summary>
    internal const string MachineType = "MACHINE_TYPE";
    /// <summary>A station step (deburring, inspection …) of an operation: its Workstation Type.</summary>
    internal const string WorkstationType = "WORKSTATION_TYPE";
}

/// <summary>One resource of a Work Order's route, with the planned seconds its operations take for the whole quantity.</summary>
internal sealed record TargetTimeResourceInput(
    string Kind, string? ResourceId, string Name, double? HourlyRateNis, int OperationNumber, double PlannedSeconds);

/// <summary>
/// One resource of the target time: the hourly price used (its own, or the default), the target hours the whole Work Order
/// margin buys at that price, the same per part in minutes, and the hours its operations are planned to take.
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

/// <summary>A calculation: margin = part price × quantity − material total; per resource, target hours = margin ÷ hourly price.</summary>
internal sealed record TargetTimeCalculation(
    double PartPriceNis, double MaterialTotalNis, int Quantity, double MarginNis, IReadOnlyList<TargetTimeResource> Resources);

/// <summary>The saved target time of a Work Order (schema v122).</summary>
internal sealed record WorkOrderTargetTime(
    string ProductionBatchId,
    TargetTimeCalculation Calculation,
    string? Notes,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

/// <summary>What the Server knows about a Work Order for its target time.</summary>
internal sealed record WorkOrderTargetTimeContext(
    string ProductionBatchId, string BatchNumber, int Quantity, double? SuggestedPartPriceNis, IReadOnlyList<TargetTimeResourceInput> Resources);

internal enum WorkOrderTargetTimeOutcome { Created, Updated, Deleted, NotFound, TargetTimeNotFound, AlreadyExists, Stale }

internal sealed record WorkOrderTargetTimeWriteResult(WorkOrderTargetTimeOutcome Outcome, WorkOrderTargetTime? TargetTime);

internal sealed class WorkOrderTargetTimeValidationException(string field, string message) : Exception(message)
{
    internal string Field { get; } = field;
}

internal interface IWorkOrderTargetTimeRepository
{
    /// <summary>The Work Order's quantity, suggested part price and route resources, or null when there is no such Work Order.</summary>
    Task<WorkOrderTargetTimeContext?> GetContextAsync(string productionBatchId, CancellationToken token);

    Task<WorkOrderTargetTime?> GetAsync(string productionBatchId, CancellationToken token);

    /// <summary>Creates (<paramref name="expectedVersion"/> null) or replaces the target time in one immediate transaction.</summary>
    Task<WorkOrderTargetTimeWriteResult> SaveAsync(
        string productionBatchId, Func<WorkOrderTargetTimeContext, TargetTimeCalculation> calculate, string? notes,
        int? expectedVersion, string actor, DateTimeOffset now, CancellationToken token);

    Task<WorkOrderTargetTimeWriteResult> DeleteAsync(string productionBatchId, int expectedVersion, string actor, DateTimeOffset now, CancellationToken token);
}

/// <summary>
/// The target time of a Work Order (owner request 2026-10-14): how much work time the part price leaves after material,
/// at the work-hour price of each resource of the route. Each resource is shown on its own: the whole margin divided by
/// that resource's hourly price (a Machine's own price or 240 NIS, a Workstation Type's own price or 100 NIS).
/// </summary>
internal sealed class WorkOrderTargetTimeService(IWorkOrderTargetTimeRepository repository, TimeProvider timeProvider)
{
    internal const double MaximumPartPriceNis = 100_000_000;
    internal const double MaximumMaterialTotalNis = 1_000_000_000;
    internal const int MaximumNotesLength = 500;

    public Task<WorkOrderTargetTimeContext?> GetContextAsync(string productionBatchId, CancellationToken token) =>
        repository.GetContextAsync(productionBatchId.Trim(), token);

    public Task<WorkOrderTargetTime?> GetAsync(string productionBatchId, CancellationToken token) =>
        repository.GetAsync(productionBatchId.Trim(), token);

    public async Task<TargetTimeCalculation?> CalculateAsync(string productionBatchId, double? partPriceNis, double? materialTotalNis, CancellationToken token)
    {
        var (price, material) = ValidateInputs(partPriceNis, materialTotalNis);
        var context = await repository.GetContextAsync(productionBatchId.Trim(), token);
        return context is null ? null : Calculate(context, price, material);
    }

    public Task<WorkOrderTargetTimeWriteResult> SaveAsync(
        string productionBatchId, double? partPriceNis, double? materialTotalNis, string? notes, int? expectedVersion,
        EditAuthority authority, CancellationToken token)
    {
        var (price, material) = ValidateInputs(partPriceNis, materialTotalNis);
        var trimmed = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (trimmed is { Length: > MaximumNotesLength })
            throw new WorkOrderTargetTimeValidationException("notes", $"notes may contain at most {MaximumNotesLength} characters.");
        return repository.SaveAsync(productionBatchId.Trim(), context => Calculate(context, price, material), trimmed, expectedVersion,
            SignedInActor.Require(authority), timeProvider.GetUtcNow(), token);
    }

    public Task<WorkOrderTargetTimeWriteResult> DeleteAsync(string productionBatchId, int expectedVersion, EditAuthority authority, CancellationToken token) =>
        repository.DeleteAsync(productionBatchId.Trim(), expectedVersion, SignedInActor.Require(authority), timeProvider.GetUtcNow(), token);

    private static (double Price, double Material) ValidateInputs(double? partPriceNis, double? materialTotalNis)
    {
        if (partPriceNis is not { } price || !double.IsFinite(price) || price < 0 || price > MaximumPartPriceNis)
            throw new WorkOrderTargetTimeValidationException("partPriceNis",
                string.Create(CultureInfo.InvariantCulture, $"partPriceNis is required: the price of one part in NIS, from 0 to {MaximumPartPriceNis:0}."));
        if (materialTotalNis is not { } material || !double.IsFinite(material) || material < 0 || material > MaximumMaterialTotalNis)
            throw new WorkOrderTargetTimeValidationException("materialTotalNis",
                string.Create(CultureInfo.InvariantCulture, $"materialTotalNis is required: the material cost of the whole Work Order in NIS, from 0 to {MaximumMaterialTotalNis:0}."));
        return (price, material);
    }

    /// <summary>
    /// margin = part price × quantity − material total; for each resource (operations on the same resource together),
    /// target hours = margin ÷ hourly price and target minutes per part = target hours × 60 ÷ quantity. A negative margin
    /// gives negative target times: the work cannot be paid for.
    /// </summary>
    internal static TargetTimeCalculation Calculate(WorkOrderTargetTimeContext context, double partPriceNis, double materialTotalNis)
    {
        var quantity = Math.Max(1, context.Quantity);
        var margin = Round(partPriceNis * quantity - materialTotalNis, 2);
        var resources = context.Resources
            .GroupBy(resource => (resource.Kind, Key: resource.ResourceId ?? resource.Name))
            .Select(group =>
            {
                var first = group.First();
                var rate = first.HourlyRateNis ?? (first.Kind == TargetTimeResourceKinds.WorkstationType
                    ? ResourceHourlyRates.WorkstationDefaultNis
                    : ResourceHourlyRates.MachineDefaultNis);
                var hours = rate > 0 ? margin / rate : 0;
                return new TargetTimeResource(
                    first.Kind, first.ResourceId, first.Name, rate, first.HourlyRateNis is null,
                    Round(hours, 3), Round(hours * 60 / quantity, 3), Round(group.Sum(resource => resource.PlannedSeconds) / 3600, 3),
                    group.Select(resource => resource.OperationNumber).Distinct().Order().ToArray());
            })
            .OrderBy(resource => resource.OperationNumbers.FirstOrDefault())
            .ThenBy(resource => resource.Kind == TargetTimeResourceKinds.WorkstationType ? 1 : 0)
            .ThenBy(resource => resource.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new TargetTimeCalculation(partPriceNis, materialTotalNis, quantity, margin, resources);
    }

    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
