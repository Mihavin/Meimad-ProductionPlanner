using Meimad.Planner.Server.Application.ToolCatalog;
using Meimad.Planner.Server.Domain.ToolCatalog;
using Meimad.Planner.Server.Domain.ToolPreparations;

namespace Meimad.Planner.Server.Api.ToolCatalog;

/// <summary>
/// The tool catalog: tool definitions with a stable Meimad internal id and their ids in other
/// systems. Reads are open; writes need the client identity headers (no Edit Mode, like the Tool
/// Room's measurements) and replace a tool at its expected version.
/// </summary>
internal static class ToolCatalogEndpoints
{
    internal static void MapToolCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var catalog = endpoints.MapGroup("/api/v1/tool-catalog");
        catalog.MapGet(string.Empty, ListAsync);
        catalog.MapGet("/types", Types);
        catalog.MapPost(string.Empty, CreateAsync);
        catalog.MapGet("/{toolId}", GetAsync);
        catalog.MapPut("/{toolId}", UpdateAsync);
        catalog.MapDelete("/{toolId}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(
        string? query, string? type, bool? includeInactive, ToolCatalogService service, CancellationToken token)
    {
        var tools = await service.ListAsync(query, type, includeInactive ?? false, token);
        return Results.Ok(new ToolCatalogListResponse(tools.Select(ToolCatalogToolResponse.FromDomain).ToArray()));
    }

    /// <summary>The tool types with their family, whether they are handed, and the dimension keys.</summary>
    private static IResult Types() =>
        Results.Ok(new ToolTypeListResponse(
            ToolShapeTypes.All.Select(type => new ToolTypeResponse(type, ToolShapeTypes.Family(type), ToolShapeTypes.IsTurning(type))).ToArray(),
            ToolShapeDimensions.Keys,
            ToolHands.All,
            CatalogToolAttributes.Keys));

    private static async Task<IResult> GetAsync(string toolId, ToolCatalogService service, HttpContext context, CancellationToken token)
    {
        try
        {
            return Results.Ok(ToolCatalogToolResponse.FromDomain(await service.GetAsync(toolId, token)));
        }
        catch (Exception exception) when (TryMapError(exception, context, out var error)) { return error!; }
    }

    private static async Task<IResult> CreateAsync(
        ToolCatalogToolRequest request, ToolCatalogService service, HttpContext context, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadClientIdentity(context, out _, out var userId, out var identityError))
            return identityError!;
        try
        {
            var created = await service.CreateAsync(request.ToDomain(), userId!, token);
            return Results.Created($"/api/v1/tool-catalog/{created.CatalogToolId}", ToolCatalogToolResponse.FromDomain(created));
        }
        catch (Exception exception) when (TryMapError(exception, context, out var error)) { return error!; }
    }

    private static async Task<IResult> UpdateAsync(
        string toolId, ToolCatalogToolRequest request, ToolCatalogService service, HttpContext context, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadClientIdentity(context, out _, out var userId, out var identityError))
            return identityError!;
        if (request.ExpectedVersion is null or < 1)
            return PlanningHttpSupport.Error(StatusCodes.Status400BadRequest, "tool_catalog_expected_version_required",
                "expectedVersion (the version being replaced) is required.", context);
        try
        {
            var updated = await service.UpdateAsync(toolId, request.ExpectedVersion.Value, request.ToDomain(), userId!, token);
            return Results.Ok(ToolCatalogToolResponse.FromDomain(updated));
        }
        catch (Exception exception) when (TryMapError(exception, context, out var error)) { return error!; }
    }

    private static async Task<IResult> DeleteAsync(string toolId, ToolCatalogService service, HttpContext context, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadClientIdentity(context, out _, out _, out var identityError))
            return identityError!;
        try
        {
            return await service.DeleteAsync(toolId, token)
                ? Results.NoContent()
                : PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "resource_not_found", "The catalog tool was not found.", context);
        }
        catch (Exception exception) when (TryMapError(exception, context, out var error)) { return error!; }
    }

    private static bool TryMapError(Exception exception, HttpContext context, out IResult? result)
    {
        result = exception switch
        {
            ToolCatalogNotFoundException => PlanningHttpSupport.Error(
                StatusCodes.Status404NotFound, "resource_not_found", exception.Message, context),
            ToolCatalogConflictException conflict => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict, conflict.Code, conflict.Message, context),
            ToolCatalogValidationException validation => PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity, validation.Code, validation.Message, context,
                validation.Field is null ? null : [new { field = validation.Field, code = validation.Code, message = validation.Message }]),
            _ => null
        };
        return result is not null;
    }
}

internal sealed record ToolCatalogExternalIdRequest(string? System, string? Value);

/// <summary>Create or replace a catalog tool; <c>expectedVersion</c> is required on a replace.</summary>
internal sealed record ToolCatalogToolRequest(
    string? Name,
    string? ToolType,
    string? Hand,
    string? Description,
    Dictionary<string, double>? Shape,
    Dictionary<string, string>? Attributes,
    IReadOnlyList<ToolCatalogExternalIdRequest>? ExternalIds,
    bool? IsActive,
    int? ExpectedVersion)
{
    internal CatalogToolUpdate ToDomain() => new(
        Name, ToolType, Hand, Description, Shape, Attributes,
        (ExternalIds ?? []).Select(entry => new CatalogToolExternalId(entry.System ?? string.Empty, entry.Value ?? string.Empty)).ToArray(),
        IsActive ?? true);
}

internal sealed record ToolCatalogExternalIdResponse(string System, string Value);

internal sealed record ToolCatalogToolResponse(
    string CatalogToolId,
    int InternalNumber,
    string InternalCode,
    string Name,
    string ToolType,
    string Family,
    string? Hand,
    string? Description,
    IReadOnlyDictionary<string, double> Shape,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<ToolCatalogExternalIdResponse> ExternalIds,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string UpdatedBy)
{
    internal static ToolCatalogToolResponse FromDomain(CatalogTool tool) => new(
        tool.CatalogToolId, tool.InternalNumber, tool.InternalCode, tool.Name, tool.ToolType, tool.Family, tool.Hand,
        tool.Description, tool.Shape, tool.Attributes,
        tool.ExternalIds.Select(entry => new ToolCatalogExternalIdResponse(entry.System, entry.Value)).ToArray(),
        tool.IsActive, tool.Version, tool.CreatedAt, tool.UpdatedAt, tool.UpdatedBy);
}

internal sealed record ToolCatalogListResponse(IReadOnlyList<ToolCatalogToolResponse> Items);

internal sealed record ToolTypeResponse(string Code, string Family, bool Handed);

internal sealed record ToolTypeListResponse(
    IReadOnlyList<ToolTypeResponse> Types,
    IReadOnlyList<string> DimensionKeys,
    IReadOnlyList<string> Hands,
    IReadOnlyList<string> AttributeKeys);
