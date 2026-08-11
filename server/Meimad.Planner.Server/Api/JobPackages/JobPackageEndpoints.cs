using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.JobPackages;
using Meimad.Planner.Server.Domain.JobPackages;

namespace Meimad.Planner.Server.Api.JobPackages;

internal static class JobPackageEndpoints
{
    internal static void MapJobPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/job-packages", GenerateAsync);
    }

    private static async Task<IResult> GenerateAsync(
        GenerateJobPackageRequest request,
        HttpContext context,
        JobPackageService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(
                context,
                out var editAuthority,
                out var error))
        {
            return error!;
        }

        try
        {
            var package = await service.GenerateAsync(
                request.ToCommand(),
                editAuthority!,
                cancellationToken);
            return Results.Json(
                JobPackageResponse.FromDomain(package),
                statusCode: StatusCodes.Status201Created);
        }
        catch (JobPackageValidationException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity,
                "job_package_invalid",
                "Job package validation failed.",
                context,
                [new { field = exception.Field, message = exception.Message }]);
        }
        catch (JobPackageOperationNotFoundException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status404NotFound,
                "batch_operation_not_found",
                exception.Message,
                context);
        }
        catch (JobPackageSourceUnavailableException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "package_source_unavailable",
                exception.Message,
                context);
        }
        catch (JobPackageOperationNotAssignedException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "batch_operation_not_assigned",
                exception.Message,
                context);
        }
        catch (JobPackageRevisionConflictException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "package_revision_conflict",
                exception.Message,
                context);
        }
        catch (JobPackageContextChangedException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "package_context_changed",
                exception.Message,
                context);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                exception.Code,
                exception.Message,
                context);
        }
    }
}

internal sealed record GenerateJobPackageRequest(
    string BatchOperationId,
    string Revision,
    string? ToolCartId,
    bool IncludePreview,
    IReadOnlyList<JobPackageSourceFileRequest>? Files,
    IReadOnlyList<ToolTableEntryRequest>? ToolTable,
    IReadOnlyList<OffsetEntryRequest>? Offsets,
    string? Instructions)
{
    internal GenerateJobPackageCommand ToCommand() => new(
        BatchOperationId,
        Revision,
        ToolCartId,
        IncludePreview,
        Files?.Select(file => new JobPackageSourceFileCommand(
            file.AssetType,
            file.SourceRelativePath,
            file.LogicalPath)).ToArray(),
        ToolTable?.Select(row => new ToolTableEntry(
            row.ToolId,
            row.Description,
            row.Diameter,
            row.Length,
            row.Note)).ToArray(),
        Offsets?.Select(row => new OffsetEntry(
            row.Name,
            row.Value,
            row.Unit,
            row.Note)).ToArray(),
        Instructions);
}

internal sealed record JobPackageSourceFileRequest(
    string AssetType,
    string SourceRelativePath,
    string LogicalPath);

internal sealed record ToolTableEntryRequest(
    string ToolId,
    string Description,
    string? Diameter,
    string? Length,
    string? Note);

internal sealed record OffsetEntryRequest(
    string Name,
    string Value,
    string? Unit,
    string? Note);

internal sealed record JobPackageResponse(
    string PackageId,
    string Revision,
    string? ToolCartId,
    DateTimeOffset PublishedAt,
    JobPackageSnapshotResponse Snapshot,
    IReadOnlyList<JobPackageAssetResponse> Assets)
{
    internal static JobPackageResponse FromDomain(JobPackage package) => new(
        package.PackageId,
        package.Revision,
        package.ToolCartId,
        package.PublishedAt,
        JobPackageSnapshotResponse.FromDomain(package.Snapshot),
        package.Assets.Select(JobPackageAssetResponse.FromDomain).ToArray());
}

internal sealed record JobPackageSnapshotResponse(
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
    string OperationName)
{
    internal static JobPackageSnapshotResponse FromDomain(JobPackageSnapshot value) => new(
        value.MachineId,
        value.MachineNumber,
        value.MachineName,
        value.CaseId,
        value.PartNumber,
        value.PartName,
        value.PartRevision,
        value.Customer,
        value.BatchId,
        value.BatchNumber,
        value.PlannedQuantity,
        value.BatchOperationId,
        value.OperationNumber,
        value.OperationName);
}

internal sealed record JobPackageAssetResponse(
    string FileId,
    string AssetType,
    string LogicalPath,
    string MediaType,
    long ByteLength,
    string Sha256,
    int DisplayOrder)
{
    internal static JobPackageAssetResponse FromDomain(JobPackageAsset value) => new(
        value.FileId,
        value.AssetType.ToStorageToken(),
        value.LogicalPath,
        value.MediaType,
        value.ByteLength,
        value.Sha256,
        value.DisplayOrder);
}
