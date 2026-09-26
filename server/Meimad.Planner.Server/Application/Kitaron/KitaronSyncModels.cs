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

/// <summary>
/// One open Kitaron work order (`TRootCard`): the factory's actual production launch for one
/// part against one sales-order line. `Amount` is the launched quantity (with cutting reserve),
/// `ProductionAmount` the net ordered quantity.
/// </summary>
internal sealed record KitaronSourceWorkOrder(
    int Number,
    string PartNumber,
    string OrderRecordId,
    double? Amount,
    double? ProductionAmount,
    DateTime? SupplyDate,
    string? LotNumber,
    string? RawMaterialId = null,
    string? CustomerOrderNumber = null,
    string? Customer = null,
    string? PartName = null,
    string? PartRevision = null);

/// <summary>An order-line allocation of a work order (`TOrderLinkRoot`).</summary>
internal sealed record KitaronSourceWorkOrderLink(
    int WorkOrderNumber,
    string OrderRecordId,
    double? ProductionAmount);

/// <summary>
/// One material line of Kitaron's per-work-order material calculation (`TBOMWithdrawalByRoot`):
/// what the work order needs, what the stock held at the last calculation, and what is on
/// purchase orders. Kitaron maintains these rows only for parts with stock-managed materials.
/// </summary>
internal sealed record KitaronSourceWorkOrderMaterial(
    int WorkOrderNumber,
    string MaterialPartNumber,
    double RequiredAmount,
    double? IssuedAmount,
    double? StockAmount,
    double? OnPurchaseAmount,
    double? RunningBalance);

internal sealed record KitaronSourceSnapshot(
    IReadOnlyList<KitaronSourceRow> WorkRows,
    IReadOnlyList<KitaronSourceOrder> Orders,
    IReadOnlyList<KitaronSourceComponent> Components,
    IReadOnlyList<KitaronSourceRow>? MaterialRows = null,
    IReadOnlyList<KitaronSourceRouteStep>? RouteSteps = null,
    IReadOnlyList<KitaronDiscoveredStation>? Stations = null,
    IReadOnlyList<KitaronSourceWorkOrder>? WorkOrders = null,
    IReadOnlyList<KitaronSourceWorkOrderLink>? WorkOrderLinks = null,
    IReadOnlyList<KitaronSourceWorkOrderMaterial>? WorkOrderMaterials = null);

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

/// <summary>
/// A Kitaron route operation. `RoutePosition` is its place in the Kitaron route and
/// `PredecessorSourceKey` the route operation it follows (null for the first one); the
/// synchronization makes the Case Operation SEQUENTIAL after that predecessor.
/// </summary>
internal sealed record KitaronSyncOperation(
    string SourceKey,
    string CaseSourceKey,
    int OperationNumber,
    int RoutePosition,
    string Name,
    string? RequiredMachineType,
    int? SetupSeconds,
    int? CycleSeconds,
    string SourceHash,
    string? PredecessorSourceKey = null);

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
    string SourceHash)
{
    internal double? UnitPrice { get; init; }

    internal double? LineTotal { get; init; }

    /// <summary>Customer order row the purchase names directly (rare in Kitaron).</summary>
    internal string? CustomerOrderReference { get; init; }
}

/// <summary>
/// Snapshot of one open Kitaron work order for reference lists: its raw material ties it to the
/// material purchase lines. Replaced by every synchronization.
/// </summary>
internal sealed record KitaronSyncWorkOrderSnapshot(
    int Number,
    string PartNumber,
    string? RawMaterialId,
    string? CustomerOrderNumber,
    string? Customer,
    int? Quantity,
    DateOnly? SupplyDate);

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

/// <summary>One Order-line allocation of an imported Production Batch. A null
/// `OrderSourceKey` allocates the quantity to stock (the Meimad Order is missing or closed);
/// `ScrapAllowance` marks the launched cutting reserve above the net ordered quantity.</summary>
internal sealed record KitaronSyncBatchAllocation(
    string? OrderSourceKey,
    int Quantity,
    bool ScrapAllowance = false);

/// <summary>
/// One Production Batch imported from an open Kitaron work order. `MaterialState` mirrors
/// Kitaron's own per-work-order material calculation: available, on_order, missing, or unknown
/// when Kitaron keeps no material rows for the part. Kitaron (the ERP) stays authoritative for
/// stock; the state is an advisory fact for planning, refreshed by every synchronization.
/// </summary>
internal sealed record KitaronSyncBatch(
    string SourceKey,
    string CaseSourceKey,
    string BatchNumber,
    int PlannedQuantity,
    IReadOnlyList<KitaronSyncBatchAllocation> Allocations,
    string MaterialState,
    string? MaterialDetail,
    string SourceHash)
{
    /// <summary>Open Kitaron raw-material purchase lines (material order source keys) assigned to
    /// this batch because its work order uses that raw material, earliest due first.</summary>
    internal IReadOnlyList<string> MaterialOrderKeys { get; init; } = [];

    /// <summary>Readable form of the assigned purchase lines, e.g. "76423/1 due 2026-10-06".</summary>
    internal string? MaterialOrdersText { get; init; }
}

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
    int RouteStepsSkipped = 0,
    IReadOnlySet<string>? RoutePartNumbers = null,
    IReadOnlyList<KitaronSyncBatch>? Batches = null,
    IReadOnlyList<KitaronSyncWorkOrderSnapshot>? WorkOrders = null);

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
