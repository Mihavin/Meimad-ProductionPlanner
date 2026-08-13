using Meimad.Planner.Server.Application.Reports;
using Meimad.Planner.Server.Application.EditMode;
using System.Net.Mail;

namespace Meimad.Planner.Server.Api.Reports;

internal static class WeeklyMaterialReportEndpoints
{
    internal static void MapWeeklyMaterialReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/reports/weekly-material-order", GenerateAsync);
        endpoints.MapPost("/api/v1/reports/weekly-material-order/send", SendAsync);
    }

    private static async Task<IResult> GenerateAsync(
        WeeklyMaterialReportService service, CancellationToken token) =>
        Results.Ok(Response(await service.GenerateAsync(token)));

    private static async Task<IResult> SendAsync(
        HttpContext context,
        WeeklyMaterialReportService service,
        EditModeService editMode,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
        {
            return error!;
        }

        var edit = await editMode.GetStatusAsync(authority!.ClientId, token);
        if (edit.CallerState != EditClientState.Editor || edit.Generation != authority.Generation)
        {
            return PlanningHttpSupport.Error(409, "edit_authority_required",
                "The active Server Edit Mode generation is required to send the report.", context);
        }

        try
        {
            return Results.Ok(Response(await service.SendNowAsync(token)));
        }
        catch (WeeklyMaterialReportDeliveryException exception)
        {
            return PlanningHttpSupport.Error(422, "report_delivery_not_configured", exception.Message, context);
        }
        catch (SmtpException exception)
        {
            return PlanningHttpSupport.Error(502, "report_delivery_failed", exception.Message, context);
        }
    }

    private static WeeklyMaterialReportResponse Response(WeeklyMaterialOrderReport report) => new(
        report.Items.Select(item => new WeeklyMaterialReportItemResponse(
            item.PartNumber,
            item.RequiredMaterialPieceQuantity)).ToArray());
}

internal sealed record WeeklyMaterialReportResponse(IReadOnlyList<WeeklyMaterialReportItemResponse> Items);
internal sealed record WeeklyMaterialReportItemResponse(
    string CasePartNumber,
    long RequiredMaterialPieceQuantity);
