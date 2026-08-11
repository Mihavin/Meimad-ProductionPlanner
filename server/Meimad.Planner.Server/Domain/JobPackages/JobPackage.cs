namespace Meimad.Planner.Server.Domain.JobPackages;

internal enum JobPackageAssetType
{
    Preview,
    ToolTable,
    Nc,
    Text,
    Offsets,
    Instructions,
    Other
}

internal static class JobPackageAssetTypeExtensions
{
    internal static string ToStorageToken(this JobPackageAssetType value) => value switch
    {
        JobPackageAssetType.Preview => "preview",
        JobPackageAssetType.ToolTable => "tool_table",
        JobPackageAssetType.Nc => "nc",
        JobPackageAssetType.Text => "text",
        JobPackageAssetType.Offsets => "offsets",
        JobPackageAssetType.Instructions => "instructions",
        JobPackageAssetType.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}

internal sealed record JobPackageSnapshot(
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
    string OperationName);

internal sealed record JobPackageAsset(
    string FileId,
    JobPackageAssetType AssetType,
    string LogicalPath,
    string StorageRelativePath,
    string MediaType,
    long ByteLength,
    string Sha256,
    DateTimeOffset ModifiedAt,
    int DisplayOrder);

internal sealed record JobPackage(
    string PackageId,
    string Revision,
    string? ToolCartId,
    DateTimeOffset PublishedAt,
    JobPackageSnapshot Snapshot,
    IReadOnlyList<JobPackageAsset> Assets);

internal sealed record ToolTableEntry(
    string ToolId,
    string Description,
    string? Diameter,
    string? Length,
    string? Note);

internal sealed record OffsetEntry(
    string Name,
    string Value,
    string? Unit,
    string? Note);
