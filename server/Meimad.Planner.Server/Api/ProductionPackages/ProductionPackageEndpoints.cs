using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Application.Accounts;

namespace Meimad.Planner.Server.Api.ProductionPackages;

internal static class ProductionPackageEndpoints
{
    internal static void MapProductionPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/batch-operations/{operationId}/workbench", async (string operationId, HttpContext context,
            ExecutionContextService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out _, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var result = await service.ReadWorkbenchAsync(operationId, Selection(context), token);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "context_invalid", exception.Message, context); }
            catch (ProductionPackageBuildException exception) { return PlanningHttpSupport.Error(409, exception.Code, exception.Message, context); }
        });
        endpoints.MapGet("/api/v1/production-packages/{packageId}/handoffs", async (string packageId, HttpContext context,
            PackageHandoffService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out _, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await service.ReadAsync(packageId, token)); }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(404, "handoff_context_missing", exception.Message, context); }
        });
        endpoints.MapPost("/api/v1/production-packages/{packageId}/handoffs", async (string packageId, HandoffRequest request,
            HttpContext context, PackageHandoffService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, Permissions.PrepareTools, out var user, out var error)) return error!;
            try { return Results.Ok(await service.RecordAsync(packageId, request, user!.UserName, token)); }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "handoff_invalid", exception.Message, context); }
            catch (InvalidOperationException exception) { return PlanningHttpSupport.Error(409, "handoff_changed", exception.Message, context); }
            catch (ProductionPackageBuildException exception) { return PlanningHttpSupport.Error(409, exception.Code, exception.Message, context); }
        });
        endpoints.MapGet("/api/v1/batch-operations/{operationId}/execution-contexts", async (string operationId,
            HttpContext context, ExecutionContextService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out _, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await service.ListAsync(operationId, token)); }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "context_invalid", exception.Message, context); }
        });
        endpoints.MapGet("/api/v1/production-packages/{packageId}", async (string packageId, ProductionPackageService service, CancellationToken token) =>
        {
            var package=await service.ReadHistoricalAsync(packageId,token);
            return package is null ? Results.NotFound() : Results.Ok(ProductionPackageResponse.FromDomain(package));
        });
        endpoints.MapGet("/api/v1/production-packages/{packageId}/artifacts/{artifactId}", HistoricalDownloadAsync);
        endpoints.MapPost("/api/v1/batch-operations/{operationId}/production-package", CreateAsync);
        endpoints.MapGet("/api/v1/batch-operations/{operationId}/production-package", ReadCurrentAsync);
        endpoints.MapGet(
            "/api/v1/batch-operations/{operationId}/production-package/artifacts/{artifactId}",
            DownloadAsync);
    }

    private static async Task<IResult> CreateAsync(
        string operationId,
        string? toolOffsetMode,
        ProductionPackageService service,
        HttpContext context,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryAuthorizeIdentity(context, null, out _, out var userId, out var error))
            return error!;
        try
        {
            var package = await service.CreateAsync(operationId, userId!, toolOffsetMode ?? "MEASURED", token, Selection(context), context.Request.Headers.TryGetValue("Idempotency-Key", out var key) ? key.ToString() : null);
            return Results.Created(
                $"/api/v1/production-packages/{package.ProductionPackageId}",
                ProductionPackageResponse.FromDomain(package));
        }
        catch (ProductionPackageBuildException exception)
        {
            if (exception.Code == "production_package_build_busy")
            {
                context.Response.Headers.RetryAfter = "1";
                return PlanningHttpSupport.Error(429, exception.Code, exception.Message, context);
            }
            return PlanningHttpSupport.Error(exception.Code.StartsWith("production_package_context", StringComparison.Ordinal)
                || exception.Code is "production_package_publication_conflict" or "production_package_request_conflict" ? 409 : 422, exception.Code, exception.Message, context);
        }
    }

    private static async Task<IResult> ReadCurrentAsync(
        string operationId,
        ProductionPackageService service,
        HttpContext context,
        CancellationToken token)
    {
        try
        {
            var package = await service.ReadCurrentAsync(operationId, token, Selection(context));
            return package is null ? Results.NotFound() : Results.Ok(ProductionPackageResponse.FromDomain(package));
        }
        catch (ProductionPackageBuildException exception) { return PlanningHttpSupport.Error(409,exception.Code,exception.Message,context); }
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
            var file = await service.OpenCurrentArtifactAsync(operationId, artifactId, token, Selection(context));
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

    internal static ProductionPackageSelection? Selection(HttpContext context)
    {
        var keys=new[]{"machineAssignmentId","productionRunId","productionRunProgramId","productionRunOutputId"};
        var values=keys.Select(key=>context.Request.Query[key].ToString()).ToArray();
        if (values.All(string.IsNullOrEmpty) && !context.Request.Query.ContainsKey("contextStamp")) return null;
        if (values.Any(string.IsNullOrWhiteSpace)) throw new ProductionPackageBuildException("production_package_context_required",
            "Supply the complete assignment, Run, program and output identity from the preparation queue.");
        return new(values[0],values[1],values[2],values[3],context.Request.Query.TryGetValue("contextStamp",out var stamp)?stamp.ToString():null);
    }

    private static async Task<IResult> HistoricalDownloadAsync(string packageId,string artifactId,ProductionPackageService service,HttpContext context,CancellationToken token)
    {
        try
        {
            var file=await service.OpenCurrentArtifactAsync(string.Empty,artifactId,token,historicalPackageId:packageId);
            if (file is null) return Results.NotFound();
            context.Response.Headers.ETag=$"\"sha256:{file.Value.Hash}\"";
            return Results.File(file.Value.Path,"application/octet-stream",file.Value.FileName,enableRangeProcessing:true);
        }
        catch(ProductionPackageBuildException exception) { return PlanningHttpSupport.Error(409,exception.Code,exception.Message,context); }
    }
}

internal sealed record ProductionPackageResponse(
    string ProductionPackageId,
    int PackageNumber,
    string BatchOperationId,
    string? ProductionRunId,
    string MachineAssignmentId,
    string MachineId,
    string? GCodeReleaseId,
    string ToolTableReleaseId,
    string? OffsetLoaderReleaseId,
    string ExecutionMode,
    string ToolOffsetMode,
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
    IReadOnlyList<ProductionPackageArtifactResponse> Artifacts,
    string? ToolPreparationId, ProductionPackageContext? Context = null)
{
    internal static ProductionPackageResponse FromDomain(ProductionPackageRecord value) => new(
        value.ProductionPackageId, value.PackageNumber, value.BatchOperationId, value.ProductionRunId,
        value.MachineAssignmentId, value.MachineId, value.GCodeReleaseId,
        value.ToolTableReleaseId, value.OffsetLoaderReleaseId, value.ExecutionMode,
        value.ToolOffsetMode,
        value.VerificationEnabled, value.VerificationConfigurationVersion,
        value.VerificationMacroVersion, value.ManifestHash, value.CreatedAt, value.CreatedBy,
        value.SupersedesPackageId, true, value.DirectTransferConfigured,
        value.DirectTransferOnline,
        value.Artifacts.Select(ProductionPackageArtifactResponse.FromDomain).ToArray(),
        value.ToolPreparationId, value.Context);
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
