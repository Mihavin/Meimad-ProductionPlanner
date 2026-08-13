using System.Net.Mail;
using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Reports;

namespace Meimad.Planner.Server.Api.Reports;

internal static class WeeklyEmployeeEfficiencyReportEndpoints
{
    internal static void MapWeeklyEmployeeEfficiencyReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/employee-work-measurements", RecordMeasurementAsync);
        endpoints.MapGet("/api/v1/reports/weekly-employee-efficiency", GenerateAsync);
        endpoints.MapPost("/api/v1/reports/weekly-employee-efficiency/send", SendAsync);
    }

    private static async Task<IResult> RecordMeasurementAsync(
        EmployeeWorkMeasurementRequest request, HttpContext context,
        WeeklyEmployeeEfficiencyReportService service, EditModeService editMode, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)
            || !PlanningHttpSupport.TryReadClientIdentity(context, out _, out var userId, out error)) return error!;
        var edit = await editMode.GetStatusAsync(authority!.ClientId, token);
        if (edit.CallerState != EditClientState.Editor || edit.Generation != authority.Generation)
            return PlanningHttpSupport.Error(409, "edit_authority_required", "The active Server Edit Mode generation is required to record employee work.", context);
        try
        {
            var value = await service.RecordAsync(new(request.EmployeeResourceId, request.WorkDate,
                request.PlannedSeconds, request.ActualSeconds, request.SourceReference, request.Notes), userId!, token);
            return Results.Created($"/api/v1/employee-work-measurements/{value.MeasurementId}", new EmployeeWorkMeasurementResponse(
                value.MeasurementId,value.EmployeeResourceId,value.WorkDate,value.PlannedSeconds,value.ActualSeconds,
                value.SourceReference,value.Notes,value.RecordedBy,value.RecordedAt));
        }
        catch (EmployeeWorkMeasurementValidationException exception)
        { return PlanningHttpSupport.Error(422, "invalid_employee_work_measurement", $"{exception.Field}: {exception.Message}", context); }
    }

    private static async Task<IResult> GenerateAsync(WeeklyEmployeeEfficiencyReportService service, CancellationToken token) =>
        Results.Ok(Response(await service.GenerateAsync(token)));

    private static async Task<IResult> SendAsync(
        HttpContext context, WeeklyEmployeeEfficiencyReportService service,
        EditModeService editMode, CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        var edit = await editMode.GetStatusAsync(authority!.ClientId, token);
        if (edit.CallerState != EditClientState.Editor || edit.Generation != authority.Generation)
            return PlanningHttpSupport.Error(409, "edit_authority_required", "The active Server Edit Mode generation is required to send the report.", context);
        try { return Results.Ok(Response(await service.SendNowAsync(token))); }
        catch (EmployeeEfficiencyReportDeliveryException exception)
        { return PlanningHttpSupport.Error(422, "report_delivery_not_configured", exception.Message, context); }
        catch (SmtpException exception)
        { return PlanningHttpSupport.Error(502, "report_delivery_failed", exception.Message, context); }
    }

    private static WeeklyEmployeeEfficiencyResponse Response(WeeklyEmployeeEfficiencyReport report) => new(
        report.WeekStart, report.WeekEnd, report.Employees.Select(item => new WeeklyEmployeeEfficiencyItemResponse(
            item.EmployeeResourceId,item.EmployeeNumber,item.FirstName,item.LastName,item.Role,
            item.PlannedSeconds,item.ActualSeconds,item.DifferenceSeconds,item.PercentageDifference,
            item.AvailableCapacitySeconds,item.PlannedCapacityPercent,item.ActualCapacityPercent)).ToArray());
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EmployeeWorkMeasurementRequest(
    string? EmployeeResourceId, DateOnly? WorkDate, long PlannedSeconds, long ActualSeconds,
    string? SourceReference, string? Notes);
internal sealed record EmployeeWorkMeasurementResponse(
    string MeasurementId, string EmployeeResourceId, DateOnly WorkDate, long PlannedSeconds,
    long ActualSeconds, string? SourceReference, string? Notes, string RecordedBy, DateTimeOffset RecordedAt);
internal sealed record WeeklyEmployeeEfficiencyResponse(
    DateOnly WeekStart, DateOnly WeekEnd, IReadOnlyList<WeeklyEmployeeEfficiencyItemResponse> Employees);
internal sealed record WeeklyEmployeeEfficiencyItemResponse(
    string EmployeeResourceId, string EmployeeNumber, string FirstName, string LastName, string Role,
    long PlannedSeconds, long ActualSeconds, long DifferenceSeconds, decimal? PercentageDifference,
    long AvailableCapacitySeconds, decimal? PlannedCapacityPercent, decimal? ActualCapacityPercent);
