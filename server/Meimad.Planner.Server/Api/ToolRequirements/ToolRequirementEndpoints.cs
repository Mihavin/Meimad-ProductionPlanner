using System.Globalization;
using Meimad.Planner.Server.Application.ToolRequirements;
using Meimad.Planner.Server.Domain.ToolRequirements;

namespace Meimad.Planner.Server.Api.ToolRequirements;

/// <summary>
/// The tools the plan needs in a period, read-only: <c>GET /api/v1/tool-requirements?from=&amp;to=</c>
/// and its Excel workbook at <c>/api/v1/tool-requirements/export</c>.
/// </summary>
internal static class ToolRequirementEndpoints
{
    internal static void MapToolRequirementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/tool-requirements");
        group.MapGet(string.Empty, ReadAsync);
        group.MapGet("/export", ExportAsync);
    }

    private static async Task<IResult> ReadAsync(
        HttpContext context, ToolRequirementService service, CancellationToken cancellationToken)
    {
        if (!TryReadPeriod(context, out var from, out var to, out var error)) return error!;
        return Results.Ok(ToolRequirementReportResponse.FromDomain(await service.CalculateAsync(from, to, cancellationToken)));
    }

    private static async Task<IResult> ExportAsync(
        HttpContext context, ToolRequirementService service, CancellationToken cancellationToken)
    {
        if (!TryReadPeriod(context, out var from, out var to, out var error)) return error!;
        var report = await service.CalculateAsync(from, to, cancellationToken);
        var name = string.Create(CultureInfo.InvariantCulture,
            $"Meimad-Tool-Requirements-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.xlsx");
        return Results.File(ToolRequirementWorkbookWriter.Write(report), ToolRequirementWorkbookWriter.ContentType, name);
    }

    private static bool TryReadPeriod(HttpContext context, out DateTimeOffset from, out DateTimeOffset to, out IResult? error)
    {
        error = null;
        to = default;
        if (!DateTimeOffset.TryParse(context.Request.Query["from"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out from)
            || !DateTimeOffset.TryParse(context.Request.Query["to"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out to)
            || to <= from)
        {
            error = PlanningHttpSupport.Error(StatusCodes.Status400BadRequest, "invalid_tool_requirement_period",
                "Query parameters 'from' and 'to' must be RFC 3339 instants and 'to' must be after 'from'.", context);
            return false;
        }
        if (to - from > ToolRequirementService.MaximumPeriod)
        {
            error = PlanningHttpSupport.Error(StatusCodes.Status400BadRequest, "tool_requirement_period_too_long",
                $"The period may span at most {ToolRequirementService.MaximumPeriod.TotalDays:0} days.", context);
            return false;
        }
        return true;
    }
}

internal sealed record ToolCopyStopResponse(string MachineId, string MachineLabel, DateTimeOffset From, DateTimeOffset To);

internal sealed record ToolCopyRouteResponse(int CopyNumber, IReadOnlyList<ToolCopyStopResponse> Stops);

internal sealed record ToolUseResponse(
    string OperationId, string WorkOrderNumber, string PartNumber, int OperationNumber, string OperationName,
    string MachineId, string MachineLabel, IReadOnlyList<string> ToolNumbers, int Copies, string? Holder,
    double? Length, string? Material, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

internal sealed record ToolRequirementResponse(
    string MaterialGroup, string ToolType, string ToolTypeSource, double? Diameter, string ToolName,
    int CopiesNeeded, IReadOnlyList<string> Machines, int MachineChanges, DateTimeOffset FirstNeed,
    DateTimeOffset LastNeed, IReadOnlyList<ToolCopyRouteResponse> Routes, IReadOnlyList<ToolUseResponse> Uses);

internal sealed record PlannedOperationWithoutToolsResponse(
    string OperationId, string WorkOrderNumber, string PartNumber, int OperationNumber, string OperationName,
    string MachineLabel, string MaterialGroup, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

internal sealed record ToolRequirementReportResponse(
    DateTimeOffset From, DateTimeOffset To, DateTimeOffset CalculatedAt, string TimeZoneId, int PlannedOperationCount,
    IReadOnlyList<ToolRequirementResponse> Tools, IReadOnlyList<PlannedOperationWithoutToolsResponse> OperationsWithoutToolTable)
{
    internal static ToolRequirementReportResponse FromDomain(ToolRequirementReport report) => new(
        report.From, report.To, report.CalculatedAt, report.TimeZoneId, report.PlannedOperationCount,
        report.Tools.Select(Tool).ToArray(),
        report.OperationsWithoutToolTable.Select(operation => new PlannedOperationWithoutToolsResponse(
            operation.OperationId, operation.WorkOrderNumber, operation.PartNumber, operation.OperationNumber,
            operation.OperationName, operation.MachineLabel, operation.MaterialGroup, operation.StartsAt,
            operation.EndsAt)).ToArray());

    private static ToolRequirementResponse Tool(ToolRequirement tool) => new(
        tool.MaterialGroup, tool.ToolType, tool.ToolTypeSource, tool.Diameter, tool.ToolName, tool.CopiesNeeded,
        tool.Machines, tool.MachineChanges, tool.FirstNeed, tool.LastNeed,
        tool.Routes.Select(route => new ToolCopyRouteResponse(route.CopyNumber, route.Stops
            .Select(stop => new ToolCopyStopResponse(stop.MachineId, stop.MachineLabel, stop.From, stop.To)).ToArray())).ToArray(),
        tool.Uses.Select(use => new ToolUseResponse(
            use.OperationId, use.WorkOrderNumber, use.PartNumber, use.OperationNumber, use.OperationName, use.MachineId,
            use.MachineLabel, use.ToolNumbers, use.Copies, use.Holder, use.Length, use.Material, use.StartsAt,
            use.EndsAt)).ToArray());
}
