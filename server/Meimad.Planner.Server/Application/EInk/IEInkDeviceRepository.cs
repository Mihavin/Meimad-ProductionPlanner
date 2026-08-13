namespace Meimad.Planner.Server.Application.EInk;

internal interface IEInkDeviceRepository
{
    Task<EInkDeviceSource?> ReadAsync(string deviceId, CancellationToken cancellationToken);
}

internal sealed record EInkDeviceSource(
    string DeviceId,
    string DeviceName,
    string? CredentialHash,
    bool IsEnabled,
    string? MachineId,
    EInkMachineSource? Machine,
    IReadOnlyList<EInkOperationSource> Backlog,
    EInkPackageSource? Package,
    string RevisionSeed);

internal sealed record EInkMachineSource(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    bool IsActive);

internal sealed record EInkOperationSource(
    string OperationId,
    string BatchId,
    string BatchNumber,
    string PartNumber,
    int PlannedQuantity,
    int OperationNumber,
    string OperationName,
    string Status,
    int BacklogPosition);

internal sealed record EInkPackageSource(
    string PackageId,
    string Revision,
    string BatchId,
    string BatchOperationId,
    string? ToolCartId,
    DateTimeOffset PublishedAt,
    EInkPackageMetadataSource? Metadata,
    IReadOnlyList<EInkPackageFileSource> Files);

internal sealed record EInkPackageMetadataSource(
    string MachineId,
    string MachineNumber,
    string MachineName,
    string CaseId,
    string PartNumber,
    string PartName,
    string? PartRevision,
    string? Customer,
    string BatchId,
    string BatchNumber,
    int PlannedQuantity,
    string BatchOperationId,
    int OperationNumber,
    string OperationName,
    string? SetupWorkerId,
    string? SetupWorkerFirstName,
    string? SetupWorkerLastName,
    string? SetupWorkerPhotoFileId,
    DateTimeOffset? PlannedSetupStartsAt,
    DateTimeOffset? PlannedSetupEndsAt,
    IReadOnlyList<EInkToolSource> JobTools,
    IReadOnlyList<EInkToolSource> ExpectedMachineTools,
    IReadOnlyList<EInkChecklistItemSource> LocalChecklistItems);

internal sealed record EInkToolSource(
    string ToolId,
    string Description,
    string? Diameter,
    string? Length,
    string? Note);

internal sealed record EInkChecklistItemSource(string ItemId, string Label);

internal sealed record EInkPackageFileSource(
    string FileId,
    string LogicalPath,
    string StorageRelativePath,
    string MediaType,
    long ByteLength,
    string Sha256,
    DateTimeOffset ModifiedAt,
    int DisplayOrder,
    string AssetType);
