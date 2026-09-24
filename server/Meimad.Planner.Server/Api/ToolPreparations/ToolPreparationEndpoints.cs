using Meimad.Planner.Server.Application.ToolPreparations;
using Meimad.Planner.Server.Domain.ToolPreparations;

namespace Meimad.Planner.Server.Api.ToolPreparations;

/// <summary>
/// Tool Room measurements of a Batch Operation on its assigned Machine. Like Production Package
/// creation, saving needs the client identity headers and no Edit Mode; every save appends an
/// immutable version and the expected version guards concurrent Tool Room clients.
/// </summary>
internal static class ToolPreparationEndpoints
{
    internal static void MapToolPreparationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/batch-operations/{operationId}/tool-preparation", ReadAsync);
        endpoints.MapPut("/api/v1/batch-operations/{operationId}/tool-preparation", SaveAsync);
    }

    private static async Task<IResult> ReadAsync(
        string operationId,
        ToolPreparationService service,
        HttpContext context,
        CancellationToken token)
    {
        try
        {
            return Results.Ok(ToolPreparationResponse.FromDomain(await service.ReadAsync(operationId, token)));
        }
        catch (Exception exception) when (TryMapError(exception, context, out var error))
        {
            return error!;
        }
    }

    private static async Task<IResult> SaveAsync(
        string operationId,
        ToolPreparationRequest request,
        ToolPreparationService service,
        HttpContext context,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadClientIdentity(context, out _, out var userId, out var identityError))
            return identityError!;
        try
        {
            var view = await service.SaveAsync(operationId, request.ToDomain(), userId!, token);
            return Results.Ok(ToolPreparationResponse.FromDomain(view));
        }
        catch (Exception exception) when (TryMapError(exception, context, out var error))
        {
            return error!;
        }
    }

    private static bool TryMapError(Exception exception, HttpContext context, out IResult? result)
    {
        result = exception switch
        {
            ToolPreparationNotFoundException => PlanningHttpSupport.Error(
                StatusCodes.Status404NotFound, "resource_not_found", exception.Message, context),
            ToolPreparationConflictException conflict => PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict, conflict.Code, conflict.Message, context),
            ToolPreparationValidationException validation => PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity, validation.Code, validation.Message, context,
                validation.Field is null ? null : [new { field = validation.Field, code = validation.Code, message = validation.Message }]),
            _ => null
        };
        return result is not null;
    }
}

internal sealed record ToolPreparationComponentRequest(
    int Sequence,
    string? ComponentType,
    string? Name,
    string? CatalogNumber,
    double? Length,
    double? Diameter,
    string? Notes)
{
    internal ToolPreparationComponent ToDomain() =>
        new(Sequence, ComponentType ?? string.Empty, Name ?? string.Empty, CatalogNumber, Length, Diameter, Notes);
}

internal sealed record ToolPreparationToolRequest(
    string? ToolIdentifier,
    int? OffsetNumber,
    double? MeasuredLength,
    double? MeasuredDiameter,
    string? ShapeType,
    Dictionary<string, double>? Shape,
    string? Notes,
    IReadOnlyList<ToolPreparationComponentRequest>? Components)
{
    internal ToolPreparationTool ToDomain() => new(
        0, ToolIdentifier ?? string.Empty, OffsetNumber, MeasuredLength, MeasuredDiameter,
        ShapeType ?? ToolShapeTypes.Other, Shape ?? [], Notes,
        (Components ?? []).Select(component => component.ToDomain()).ToArray());
}

internal sealed record ToolPreparationRequest(
    int ExpectedVersion,
    string? ToolTableReleaseId,
    string? Comment,
    IReadOnlyList<ToolPreparationToolRequest>? Tools)
{
    internal ToolPreparationUpdate ToDomain() => new(
        ExpectedVersion, ToolTableReleaseId ?? string.Empty, Comment,
        (Tools ?? []).Select(tool => tool.ToDomain()).ToArray());
}

internal sealed record ToolPreparationComponentResponse(
    int Sequence,
    string ComponentType,
    string Name,
    string? CatalogNumber,
    double? Length,
    double? Diameter,
    string? Notes);

/// <summary>A released tool row merged with its latest measurements, if any.</summary>
internal sealed record ToolPreparationToolResponse(
    int RowNumber,
    string ToolIdentifier,
    string Description,
    bool IsRequired,
    string? MagazinePosition,
    int? OffsetNumber,
    double? MeasuredLength,
    double? MeasuredDiameter,
    string ShapeType,
    IReadOnlyDictionary<string, double> Shape,
    string? Notes,
    IReadOnlyList<ToolPreparationComponentResponse> Components);

internal sealed record ToolPreparationResponse(
    string BatchOperationId,
    string MachineId,
    string MachineNumber,
    string MachineName,
    string ProcessType,
    string NcDialect,
    string ToolDiameterOffsetKind,
    string ToolTableReleaseId,
    int ToolTableRevision,
    string ToolTableFileName,
    int Version,
    string? ToolPreparationId,
    DateTimeOffset? SavedAt,
    string? SavedBy,
    string? Comment,
    string? ContentHash,
    string? SavedForToolTableReleaseId,
    IReadOnlyList<ToolPreparationToolResponse> Tools)
{
    internal static ToolPreparationResponse FromDomain(ToolPreparationView view)
    {
        var saved = (view.Current?.Tools ?? [])
            .ToDictionary(tool => tool.ToolIdentifier.Trim(), tool => tool, StringComparer.OrdinalIgnoreCase);
        var tools = view.ReleasedTools.Select(released =>
        {
            saved.TryGetValue(released.ToolIdentifier.Trim(), out var measured);
            return new ToolPreparationToolResponse(
                released.RowNumber, released.ToolIdentifier, released.Description, released.IsRequired,
                released.MagazinePosition, measured?.OffsetNumber, measured?.MeasuredLength,
                measured?.MeasuredDiameter, measured?.ShapeType ?? ToolShapeTypes.Other,
                measured?.Shape ?? new Dictionary<string, double>(StringComparer.Ordinal), measured?.Notes,
                (measured?.Components ?? []).Select(component => new ToolPreparationComponentResponse(
                    component.Sequence, component.ComponentType, component.Name, component.CatalogNumber,
                    component.Length, component.Diameter, component.Notes)).ToArray());
        }).ToArray();
        return new ToolPreparationResponse(
            view.BatchOperationId, view.MachineId, view.MachineNumber, view.MachineName, view.ProcessType,
            view.NcDialect, view.ToolDiameterOffsetKind, view.ToolTableReleaseId, view.ToolTableRevision,
            view.ToolTableFileName, view.Current?.VersionNumber ?? 0, view.Current?.ToolPreparationId,
            view.Current?.SavedAt, view.Current?.SavedBy, view.Current?.Comment, view.Current?.ContentHash,
            view.Current?.ToolTableReleaseId, tools);
    }
}
