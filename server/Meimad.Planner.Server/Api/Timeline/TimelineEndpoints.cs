using System.Globalization;
using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Application.Projections;

namespace Meimad.Planner.Server.Api.Timeline;

internal static class TimelineEndpoints
{
    private static readonly SemaphoreSlim ReportGate = new(1, 1);
    internal static void MapTimelineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/timeline", ReadAsync);
        endpoints.MapGet("/api/v1/timeline/report", (HttpContext context, TimelineProjectionService service,
            TimelineOptions options, ProjectionResponseCache responses, CancellationToken token) => ReadCoreAsync(context, service, options, responses, token, report: true));
        endpoints.MapPut("/api/v1/timeline/auxiliary-pins", SetPinAsync);
        endpoints.MapDelete("/api/v1/timeline/auxiliary-pins/{batchOperationId}/{requirementId}", ClearPinAsync);
    }

    /// <summary>
    /// Pins one auxiliary work item (Batch Operation plus requirement) to a Workstation and/or
    /// Employee and optionally to its start. The pin is a constraint for the next calculation; it
    /// never moves Machine assignments or backlog order.
    /// </summary>
    private static async Task<IResult> SetPinAsync(
        TimelineAuxiliaryPinRequest request,
        HttpContext context,
        ITimelineAuxiliaryPinRepository repository,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(context, Permissions.PlanMachines, out var authority, out var error)) return error!;
        if (string.IsNullOrWhiteSpace(request.BatchOperationId) || string.IsNullOrWhiteSpace(request.RequirementId))
        {
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, "validation_failed",
                "batchOperationId and requirementId are required.", context);
        }
        var userId = authority!.UserId ?? string.Empty;
        if (request.ExpectedVersion is null or < 0)
            return MissingVersion(context);
        try
        {
            var pin = await repository.SetAsync(new TimelineAuxiliaryPin(
                request.BatchOperationId.Trim(), request.RequirementId.Trim(),
                Blank(request.WorkstationId), Blank(request.EmployeeId),
                request.PlannedStartsAt, request.PlannedEndsAt, request.PinStart, Blank(request.Reason), request.ExpectedVersion.Value),
                authority!, string.IsNullOrEmpty(userId) ? null : userId, cancellationToken);
            return Results.Ok(new TimelineAuxiliaryPinResponse(
                pin.BatchOperationId, pin.RequirementId, pin.WorkstationId, pin.EmployeeId, pin.StartsAt, pin.Version));
        }
        catch (TimelineAuxiliaryPinConflictException exception)
        {
            return Conflict(exception, context);
        }
        catch (TimelineAuxiliaryPinException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, exception.Code, exception.Message, context);
        }
        catch (Application.EditMode.EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, context);
        }
    }

    private static async Task<IResult> ClearPinAsync(
        string batchOperationId,
        string requirementId,
        HttpContext context,
        ITimelineAuxiliaryPinRepository repository,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(context, Permissions.PlanMachines, out var authority, out var error)) return error!;
        if (!long.TryParse(context.Request.Query["expectedVersion"], NumberStyles.None, CultureInfo.InvariantCulture, out var expectedVersion))
            return MissingVersion(context);
        try
        {
            return await repository.ClearAsync(batchOperationId, requirementId, expectedVersion, authority!, cancellationToken)
                ? Results.NoContent()
                : PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "auxiliary_pin_not_found",
                    "No pin exists for that Batch Operation and requirement.", context);
        }
        catch (TimelineAuxiliaryPinConflictException exception)
        {
            return Conflict(exception, context);
        }
        catch (Application.EditMode.EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, context);
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult MissingVersion(HttpContext context) => PlanningHttpSupport.Error(
        StatusCodes.Status428PreconditionRequired, "auxiliary_pin_version_required",
        "An observed expectedVersion is required. Upgrade the Windows client and refresh the Timeline before editing pins.", context);

    private static IResult Conflict(TimelineAuxiliaryPinConflictException exception, HttpContext context) =>
        Results.Json(new
        {
            error = new
            {
                code = "auxiliary_pin_version_conflict", message = exception.Message, correlationId = context.TraceIdentifier,
                details = new[] { new { currentVersion = exception.CurrentVersion, isPinned = exception.IsPinned, changedBy = exception.ChangedBy, changedAt = exception.ChangedAt } },
                conflict = new
                {
                    resource = "auxiliary pin", changedBy = exception.ChangedBy, changedAt = exception.ChangedAt,
                    advice = "Your selection was not saved. Refresh the Timeline, review the current pin, and apply your selection again."
                }
            }
        }, statusCode: StatusCodes.Status412PreconditionFailed);

    private static async Task<IResult> ReadAsync(
        HttpContext context,
        TimelineProjectionService service,
        TimelineOptions options,
        ProjectionResponseCache responses,
        CancellationToken cancellationToken)
        => await ReadCoreAsync(context, service, options, responses, cancellationToken, report: false);

    private static async Task<IResult> ReadCoreAsync(HttpContext context, TimelineProjectionService service,
        TimelineOptions options, ProjectionResponseCache responses, CancellationToken cancellationToken, bool report)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!TryReadInstant(context.Request.Query["from"], out var from)
            || !TryReadInstant(context.Request.Query["to"], out var to)
            || to <= from)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status400BadRequest,
                "invalid_timeline_horizon",
                "Query parameters 'from' and 'to' must be RFC 3339 instants and 'to' must be after 'from'.",
                context);
        }

        var maximumDays = report ? options.MaximumReportDays : options.MaximumInteractiveDays;
        if ((to - from).TotalDays > maximumDays)
            return PlanningHttpSupport.Error(422, "timeline_horizon_limit",
                $"The requested horizon exceeds {maximumDays} days. " + (report ? "Narrow the report range." : "Choose Long-range report for an explicit longer calculation."), context);

        DateTimeOffset? asOf = null;
        if (context.Request.Query.TryGetValue("asOf", out var asOfValues))
        {
            if (!TryReadInstant(asOfValues, out var parsedAsOf))
            {
                return PlanningHttpSupport.Error(
                    StatusCodes.Status400BadRequest,
                    "invalid_timeline_as_of",
                    "Optional query parameter 'asOf' must be an RFC 3339 instant.",
                    context);
            }
            asOf = parsedAsOf;
            if (asOf < from || asOf >= to)
            {
                return PlanningHttpSupport.Error(
                    StatusCodes.Status400BadRequest,
                    "timeline_as_of_outside_horizon",
                    "Optional query parameter 'asOf' must fall inside the requested Timeline horizon.",
                    context);
            }
        }

        if (context.Request.Query.ContainsKey("mode"))
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status400BadRequest,
                "timeline_mode_is_assignment_owned",
                "Timeline planning mode is configured per Machine assignment and cannot be supplied as a global query parameter.",
                context);
        }

        if (report && !await ReportGate.WaitAsync(0, cancellationToken))
        {
            context.Response.Headers.RetryAfter = "1";
            return PlanningHttpSupport.Error(429, "timeline_report_busy", "Another long-range report is running. Retry later.", context);
        }
        try
        {
            var key = $"timeline:{from.UtcTicks}:{to.UtcTicks}:{asOf?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "live"}:{report}";
            var json = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
            var bytes = await responses.ReadAsync(key, cacheCompleted: asOf is not null,
                async token => PlannerPerformanceMetrics.Serialize(await service.CalculateAsync(from, to, asOf, token, recordDiagnostics: true), json), cancellationToken);
            return Results.Bytes(bytes, "application/json; charset=utf-8");
        }
        finally { if (report) ReportGate.Release(); }
    }

    private static bool TryReadInstant(string? value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out instant);
}

internal sealed record TimelineAuxiliaryPinRequest(
    string? BatchOperationId,
    string? RequirementId,
    string? WorkstationId,
    string? EmployeeId,
    DateTimeOffset PlannedStartsAt,
    DateTimeOffset PlannedEndsAt,
    bool PinStart,
    string? Reason,
    long? ExpectedVersion = null);

internal sealed record TimelineAuxiliaryPinResponse(
    string BatchOperationId,
    string RequirementId,
    string? WorkstationId,
    string? EmployeeId,
    DateTimeOffset? StartsAt,
    long Version);
