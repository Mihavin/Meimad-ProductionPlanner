using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Kitaron;

namespace Meimad.Planner.Server.Api.Kitaron;

/// <summary>
/// The Kitaron station lookup is planning master data (Machine Types, Workstation types and
/// External Resources are chosen here), so unlike the connector settings it is read by every
/// Windows client and decided by the Single Edit Mode holder rather than only from the Server PC.
/// </summary>
internal static class KitaronStationEndpoints
{
    internal static void MapKitaronStationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/kitaron/stations", ListAsync);
        endpoints.MapPut("/api/v1/kitaron/stations/{kitaronStationId:int}", DecideAsync);
    }

    private static async Task<IResult> ListAsync(KitaronStationService service, CancellationToken cancellationToken) =>
        Results.Ok(new KitaronStationListResponse(
            (await service.ListAsync(cancellationToken)).Select(KitaronStationResponse.From).ToArray()));

    private static async Task<IResult> DecideAsync(
        int kitaronStationId,
        KitaronStationDecisionRequest request,
        HttpContext context,
        KitaronStationService service,
        KitaronSyncService syncService,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error)) return error!;
        var userId = context.Request.Headers["X-Meimad-User-Id"].ToString().Trim();
        try
        {
            var value = await service.DecideAsync(
                kitaronStationId, request.ImportRole, request.MachineType, request.WorkstationTypeId,
                request.ExternalResourceId, request.DefaultMinutesPerPart, request.DefaultMinutesPerBatch,
                request.CapacityRequired, request.Notes, request.ExpectedVersion, authority!,
                string.IsNullOrEmpty(userId) ? null : userId, cancellationToken);
            // The decision reaches the Case Operations and steps with the next synchronization; ask
            // for it now instead of waiting for the periodic interval.
            syncService.RequestRun();
            return Results.Ok(KitaronStationResponse.From(value));
        }
        catch (KitaronStationValidationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, "validation_failed",
                exception.Message, context, [new { field = exception.Field }]);
        }
        catch (KitaronStationNotFoundException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "kitaron_station_not_found", exception.Message, context);
        }
        catch (KitaronStationVersionConflictException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status412PreconditionFailed, "kitaron_station_stale", exception.Message, context);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, context);
        }
    }
}

internal sealed record KitaronStationDecisionRequest(
    string? ImportRole,
    string? MachineType,
    string? WorkstationTypeId,
    string? ExternalResourceId,
    double DefaultMinutesPerPart,
    double DefaultMinutesPerBatch,
    int CapacityRequired,
    string? Notes,
    int ExpectedVersion);

internal sealed record KitaronStationListResponse(IReadOnlyList<KitaronStationResponse> Items);

internal sealed record KitaronStationResponse(
    int KitaronStationId,
    string StationName,
    string? StationType,
    bool Retired,
    int RouteRows,
    int PlannedRows,
    int SupplierRows,
    string SuggestedRole,
    string ImportRole,
    string? MachineType,
    string? WorkstationTypeId,
    string? ExternalResourceId,
    double DefaultMinutesPerPart,
    double DefaultMinutesPerBatch,
    int CapacityRequired,
    string? Notes,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? DecidedAt,
    string? DecidedBy,
    int Version,
    DateTimeOffset UpdatedAt)
{
    internal static KitaronStationResponse From(KitaronStationRecord value) => new(
        value.KitaronStationId, value.StationName, value.StationType, value.Retired, value.RouteRows,
        value.PlannedRows, value.SupplierRows, value.SuggestedRole, value.ImportRole, value.MachineType,
        value.WorkstationTypeId, value.ExternalResourceId, value.DefaultMinutesPerPart, value.DefaultMinutesPerBatch,
        value.CapacityRequired, value.Notes, value.FirstSeenAt, value.LastSeenAt, value.DecidedAt, value.DecidedBy,
        value.Version, value.UpdatedAt);
}
