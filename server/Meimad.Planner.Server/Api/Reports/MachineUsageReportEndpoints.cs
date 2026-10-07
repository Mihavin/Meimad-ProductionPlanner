using System.Globalization;
using Meimad.Planner.Server.Application.Reports;
using Meimad.Planner.Server.Configuration;

namespace Meimad.Planner.Server.Api.Reports;

internal static class MachineUsageReportEndpoints
{
    internal static void MapMachineUsageReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/reports/machine-usage", ReadAsync);
    }

    /// <summary>
    /// GET /api/v1/reports/machine-usage?from=yyyy-MM-dd&amp;to=yyyy-MM-dd&amp;basis=schedule|fullDay:
    /// Machine usage according to the calculated Timeline for whole factory days (default: the last
    /// 7 days up to today).
    /// </summary>
    private static async Task<IResult> ReadAsync(
        string? from, string? to, string? basis, HttpContext context,
        MachineUsageReportService service, TimelineOptions options,
        TimeProvider timeProvider, CancellationToken token)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
            timeProvider.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId)).DateTime);
        DateOnly? Parse(string? value) => string.IsNullOrWhiteSpace(value) ? null
            : DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) ? date : DateOnly.MinValue;
        var end = Parse(to) ?? today;
        var start = Parse(from) ?? end.AddDays(-6);
        if (start == DateOnly.MinValue || end == DateOnly.MinValue)
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, "validation_failed",
                "from and to are dates in the form yyyy-MM-dd.", context);
        try
        {
            return Results.Ok(await service.CalculateAsync(start, end, basis, token));
        }
        catch (MachineUsageValidationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, "validation_failed", exception.Message, context);
        }
    }
}
