namespace Meimad.Planner.Client.Windows.Api;
internal sealed record WorkbenchAllocation(string AllocationId, string Type, string? OrderId, string? OrderReference,
    long Quantity, int Version, string? DueDate);
internal sealed record OperationWorkbenchDetails(string CaseId, string PartNumber, string CaseName, string BatchId,
    string BatchNumber, long BatchQuantity, int BatchVersion, string BatchOperationId, int OperationNumber, string OperationName,
    int OperationVersion, string RecipeCaseId, string RecipeOperationId, string? ProcessRevisionId, string? MaterialType,
    IReadOnlyList<WorkbenchAllocation> Allocations, bool AllocationsTruncated, DateTimeOffset SampledAt)
{
    public string Breadcrumb => $"{PartNumber} / {CaseName} → {BatchNumber} → OP{OperationNumber} {OperationName}";
    public string AllocationNotice => AllocationsTruncated ? "First 200 allocations shown. Open Case demand for the remaining allocations." : "All batch allocations shown; these are demand allocations, not warehouse inventory.";
}
