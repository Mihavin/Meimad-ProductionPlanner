using Meimad.Planner.Server.Application.Maintenance;

namespace Meimad.Planner.Server.Api.Maintenance;

internal sealed record CollectedDataPreviewRequest(
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    IReadOnlyList<string>? Types,
    string? MachineId);

internal sealed record CollectedDataPurgeRequest(
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    IReadOnlyList<string>? Types,
    string? MachineId,
    long ExpectedTotalRows,
    string? Reason);

internal sealed record ServerMaintenanceCatalogResponse(
    DatabaseStorageStatus Database,
    IReadOnlyList<CollectedDataTypeOptionResponse> DeletableTypes,
    string BackupDownloadMethod,
    string BackupDownloadPath,
    string DeleteRangeSemantics)
{
    internal static ServerMaintenanceCatalogResponse FromStatus(DatabaseStorageStatus status) => new(
        status,
        [
            new(CollectedDataTypes.CncRawTelemetry, "Raw CNC telemetry", "Non-authoritative bounded adapter payloads."),
            new(CollectedDataTypes.CncStateHistory, "Machine state history", "Non-authoritative normalized snapshot history; current Machine state is retained."),
            new(CollectedDataTypes.CncConnectionEvents, "CNC connection events", "Non-authoritative connect/disconnect/retry diagnostics.")
        ],
        "POST",
        "/api/v1/server-maintenance/backups/download",
        "Half-open UTC interval: fromInclusive <= timestamp < toExclusive");
}

internal sealed record CollectedDataTypeOptionResponse(
    string Type,
    string DisplayName,
    string Description);
