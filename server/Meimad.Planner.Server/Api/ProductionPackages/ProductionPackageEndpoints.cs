using Meimad.Planner.Server.Application.ProductionPackages;

namespace Meimad.Planner.Server.Api.ProductionPackages;

internal static class ProductionPackageEndpoints
{
    internal static void MapProductionPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/batch-operations/{operationId}/production-package", CreateAsync);
        endpoints.MapGet("/api/v1/batch-operations/{operationId}/production-package", ReadCurrentAsync);
        endpoints.MapGet(
            "/api/v1/batch-operations/{operationId}/production-package/artifacts/{artifactId}",
            DownloadAsync);
    }

    private static async Task<IResult> CreateAsync(
        string operationId,
        ProductionPackageService service,
        HttpContext context,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadClientIdentity(context, out _, out var userId, out var error))
            return error!;
        try
        {
            var package = await service.CreateAsync(operationId, userId!, token);
            return Results.Created(
                $"/api/v1/batch-operations/{operationId}/production-package",
                ProductionPackageResponse.FromDomain(package));
        }
        catch (ProductionPackageBuildException exception)
        {
            return PlanningHttpSupport.Error(422, exception.Code, exception.Message, context);
        }
    }

    private static async Task<IResult> ReadCurrentAsync(
        string operationId,
        ProductionPackageService service,
        CancellationToken token)
    {
        var package = await service.ReadCurrentAsync(operationId, token);
        return package is null ? Results.NotFound() : Results.Ok(ProductionPackageResponse.FromDomain(package));
    }

    private static async Task<IResult> DownloadAsync(
        string operationId,
        string artifactId,
        ProductionPackageService service,
        HttpContext context,
        CancellationToken token)
    {
        try
        {
            var file = await service.OpenCurrentArtifactAsync(operationId, artifactId, token);
            if (file is null) return Results.NotFound();
            context.Response.Headers.ETag = $"\"sha256:{file.Value.Hash}\"";
            return Results.File(file.Value.Path, "application/octet-stream", file.Value.FileName,
                enableRangeProcessing: true);
        }
        catch (ProductionPackageBuildException exception)
        {
            return PlanningHttpSupport.Error(409, exception.Code, exception.Message, context);
        }
    }
}

internal sealed record ProductionPackageResponse(
    string ProductionPackageId,
    string BatchOperationId,
    string? ProductionRunId,
    string MachineAssignmentId,
    string MachineId,
    string? GCodeReleaseId,
    string ToolTableReleaseId,
    string? OffsetLoaderReleaseId,
    string ExecutionMode,
    bool VerificationEnabled,
    int? VerificationConfigurationVersion,
    int? VerificationMacroVersion,
    string ManifestSha256,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string? SupersedesProductionPackageId,
    bool FileExportAvailable,
    bool DirectTransferConfigured,
    bool DirectTransferOnline,
    IReadOnlyList<ProductionPackageArtifactResponse> Artifacts)
{
    internal static ProductionPackageResponse FromDomain(ProductionPackageRecord value) => new(
        value.ProductionPackageId, value.BatchOperationId, value.ProductionRunId,
        value.MachineAssignmentId, value.MachineId, value.GCodeReleaseId,
        value.ToolTableReleaseId, value.OffsetLoaderReleaseId, value.ExecutionMode,
        value.VerificationEnabled, value.VerificationConfigurationVersion,
        value.VerificationMacroVersion, value.ManifestHash, value.CreatedAt, value.CreatedBy,
        value.SupersedesPackageId, true, value.DirectTransferConfigured,
        value.DirectTransferOnline,
        value.Artifacts.Select(ProductionPackageArtifactResponse.FromDomain).ToArray());
}

internal sealed record ProductionPackageArtifactResponse(
    string ArtifactId,
    string ArtifactType,
    string LogicalPath,
    long FileSize,
    string Sha256,
    string? SourceReleaseId)
{
    internal static ProductionPackageArtifactResponse FromDomain(ProductionPackageArtifact value) => new(
        value.ArtifactId, value.ArtifactType, value.LogicalPath,
        value.FileSize, value.FileHash, value.SourceReleaseId);
}
