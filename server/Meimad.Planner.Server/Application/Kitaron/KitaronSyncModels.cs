namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed record KitaronSourceRow(IReadOnlyDictionary<string, object?> Values);

internal interface IKitaronSourceReader
{
    Task<IReadOnlyList<KitaronSourceRow>> ReadAsync(
        StoredKitaronConnectionSettings settings,
        string password,
        IReadOnlyList<string> columns,
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

internal sealed record KitaronSyncPlan(
    int SourceRows,
    IReadOnlyList<KitaronSyncCase> Cases,
    IReadOnlyList<KitaronSyncOrder> Orders,
    IReadOnlyList<KitaronSyncOperation> Operations,
    IReadOnlyList<string> Warnings,
    int MappingVersion);

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
    int WarningCount,
    int? MappingVersion,
    int Version);

internal interface IKitaronSyncRepository
{
    Task<KitaronSyncStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<KitaronSyncStatus> MarkStartedAsync(int mappingVersion, DateTimeOffset now, CancellationToken cancellationToken);
    Task<KitaronSyncStatus> MarkFailedAsync(string status, string message, DateTimeOffset now, CancellationToken cancellationToken);
    Task<KitaronSyncStatus> ApplyAsync(KitaronSyncPlan plan, DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed class KitaronSyncBlockedException(string message) : Exception(message);

internal sealed class KitaronSyncDataException(string message) : Exception(message);
