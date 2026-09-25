namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed record KitaronSourceRow(IReadOnlyDictionary<string, object?> Values);

internal sealed record KitaronSourceOrder(
    string SourceKey,
    string PartNumber,
    string Name,
    string? Revision,
    string OrderNumber,
    double? Quantity,
    DateTime? WorkFinishDate,
    bool StopProduction,
    bool IsClosed = false,
    decimal? Price = null,
    double? Supplied = null);

internal sealed record KitaronSourceComponent(
    string SourceKey,
    string ParentPartNumber,
    string ParentName,
    string? ParentRevision,
    string ChildPartNumber,
    string ChildName,
    string? ChildRevision,
    double QuantityPerParent,
    int SortOrder);

/// <summary>
/// One row of the Kitaron route master (`TDetailDirectionList` → `TDetailDirectionHeader` →
/// `TDirection`), with the header facts needed to pick one route per part.
/// </summary>
internal sealed record KitaronSourceRouteStep(
    string PartNumber,
    string? PartRevision,
    int DirectionHeaderId,
    string? HeaderRevision,
    bool ChartMaster,
    bool IsMaster,
    DateTime? ExpiredDate,
    int DirectionId,
    int? NumOrder,
    string? ActionNumber,
    string? Description,
    string? OperationName,
    int? StationId,
    bool WorkPlanning,
    double? TimeProductionMinutes,
    double? DirectionTimeMinutes,
    int? SupplierId);

internal sealed record KitaronSourceSnapshot(
    IReadOnlyList<KitaronSourceRow> WorkRows,
    IReadOnlyList<KitaronSourceOrder> Orders,
    IReadOnlyList<KitaronSourceComponent> Components,
    IReadOnlyList<KitaronSourceRow>? MaterialRows = null,
    IReadOnlyList<KitaronSourceRouteStep>? RouteSteps = null,
    IReadOnlyList<KitaronDiscoveredStation>? Stations = null);

internal interface IKitaronSourceReader
{
    Task<KitaronSourceSnapshot> ReadAsync(
        StoredKitaronConnectionSettings settings,
        string password,
        IReadOnlyList<string> workColumns,
        IReadOnlyList<string> materialColumns,
        CancellationToken cancellationToken);
}

internal sealed record KitaronSyncCase(
    string SourceKey,
    string PartNumber,
    string Name,
    string? Revision,
    string? Customer,
    string WorkingFolderPath,
    string SourceHash);

internal sealed record KitaronSyncOrder(
    string SourceKey,
    string CaseSourceKey,
    string OrderNumber,
    int Quantity,
    DateOnly WorkFinishDate,
    string Status,
    string SourceHash)
{
    internal string CanonicalOrderNumber { get; init; } = OrderNumber;
    internal decimal? Price { get; init; }
}

internal sealed record KitaronSyncComponent(
    string SourceKey,
    string ParentCaseSourceKey,
    string ChildCaseSourceKey,
    double QuantityPerParent,
    int SortOrder,
    string SourceHash);

internal sealed record KitaronSyncOperation(
    string SourceKey,
    string CaseSourceKey,
    int OperationNumber,
    int RoutePosition,
    string Name,
    string? RequiredMachineType,
    int? SetupSeconds,
    int? CycleSeconds,
    string SourceHash);

internal sealed record KitaronSyncMaterialOrder(
    string SourceKey,
    string PurchaseOrderNumber,
    string LineNumber,
    string MaterialNumber,
    string? Description,
    string? Supplier,
    double OrderedQuantity,
    double? ReceivedQuantity,
    string? Unit,
    DateOnly? RequestedDeliveryDate,
    DateOnly? ApprovedDeliveryDate,
    double? ApprovedQuantity,
    string? ApprovalNote,
    string? Status,
    bool Closed,
    string SourceHash);

/// <summary>
/// An auxiliary route step (inspection, deburring, packing, subcontract, ...) imported as a resource
/// requirement of the machining Case Operation it surrounds. `Direction` BACKWARD means before the
/// Machine anchor, FORWARD after it; `PredecessorSourceKey` chains steps of one side in route order.
/// </summary>
internal sealed record KitaronSyncRequirement(
    string SourceKey,
    string CaseSourceKey,
    string OperationSourceKey,
    int StepNumber,
    int SequencePosition,
    string Name,
    string ResourceClass,
    string? WorkstationTypeId,
    string? ExternalResourceId,
    int CapacityRequired,
    int DurationSeconds,
    int DurationPerUnitSeconds,
    string Direction,
    string? PredecessorSourceKey,
    string SourceHash);

internal sealed record KitaronSyncPlan(
    int SourceRows,
    IReadOnlyList<KitaronSyncCase> Cases,
    IReadOnlyList<KitaronSyncOrder> Orders,
    IReadOnlyList<KitaronSyncOperation> Operations,
    IReadOnlyList<KitaronSyncComponent> Components,
    IReadOnlySet<string> KnownComponentSourceKeys,
    IReadOnlyList<string> Warnings,
    int MappingVersion,
    IReadOnlyList<KitaronSyncMaterialOrder>? MaterialOrders = null,
    IReadOnlyList<KitaronSyncRequirement>? Requirements = null,
    IReadOnlyList<KitaronDiscoveredStation>? Stations = null,
    int RouteStepsSkipped = 0);

internal sealed record KitaronSyncStatus(
    string Status,
    string? Message,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastCompletedAt,
    int SourceRows,
    int CasesCreated,
    int CasesUpdated,
    int CasesMatched,
    int OrdersCreated,
    int OrdersUpdated,
    int OrdersMatched,
    int OperationsCreated,
    int OperationsUpdated,
    int OperationsMatched,
    int ComponentsCreated,
    int ComponentsUpdated,
    int ComponentsMatched,
    int WarningCount,
    int? MappingVersion,
    int Version,
    int RequirementsCreated = 0,
    int RequirementsUpdated = 0,
    int RequirementsMatched = 0,
    int RouteStepsSkipped = 0);

internal interface IKitaronSyncRepository
{
    Task<KitaronSyncStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlySet<string>> GetExistingCasePartNumbersAsync(CancellationToken cancellationToken);
    Task<KitaronSyncStatus> MarkStartedAsync(int mappingVersion, DateTimeOffset now, CancellationToken cancellationToken);
    Task<KitaronSyncStatus> MarkFailedAsync(string status, string message, DateTimeOffset now, CancellationToken cancellationToken);
    Task<KitaronSyncStatus> ApplyAsync(KitaronSyncPlan plan, DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed class KitaronSyncBlockedException(string message) : Exception(message);

internal sealed class KitaronSyncDataException(string message) : Exception(message);
