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
    bool StopProduction);

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

internal sealed record KitaronSourceSnapshot(
    IReadOnlyList<KitaronSourceRow> WorkRows,
    IReadOnlyList<KitaronSourceOrder> Orders,
    IReadOnlyList<KitaronSourceComponent> Components,
    IReadOnlyList<KitaronSourceRow>? MaterialRows = null);

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

internal sealed record KitaronSyncPlan(
    int SourceRows,
    IReadOnlyList<KitaronSyncCase> Cases,
    IReadOnlyList<KitaronSyncOrder> Orders,
    IReadOnlyList<KitaronSyncOperation> Operations,
    IReadOnlyList<KitaronSyncComponent> Components,
    IReadOnlySet<string> KnownComponentSourceKeys,
    IReadOnlyList<string> Warnings,
    int MappingVersion,
    IReadOnlyList<KitaronSyncMaterialOrder>? MaterialOrders = null);

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
    int Version);

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
