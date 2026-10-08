using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.Kitaron.Push;

namespace Meimad.Planner.Server.Api.Kitaron;

/// <summary>
/// Setup → Kitaron Push: every signed-in user may read the settings and the run log; changing them,
/// previewing and pushing need the Setup permission.
/// </summary>
internal static class KitaronPushEndpoints
{
    internal static void MapKitaronPushEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/kitaron/push", GetAsync);
        endpoints.MapPut("/api/v1/kitaron/push", UpdateAsync);
        endpoints.MapPost("/api/v1/kitaron/push/preview", PreviewAsync);
        endpoints.MapPost("/api/v1/kitaron/push/run", RunAsync);
        endpoints.MapGet("/api/v1/kitaron/push/runs/{runId}/changes", ChangesAsync);
    }

    private static async Task<IResult> GetAsync(KitaronPushService service, CancellationToken cancellationToken)
    {
        var settings = await service.GetSettingsAsync(cancellationToken);
        var runs = await service.ListRunsAsync(cancellationToken);
        return Results.Ok(KitaronPushResponse.From(settings, runs));
    }

    private static async Task<IResult> UpdateAsync(
        KitaronPushSettingsRequest request, HttpContext context, KitaronPushService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageSetup, out var user, out var error)) return error!;
        try
        {
            var settings = await service.UpdateSettingsAsync(
                request.Enabled, request.IntervalMinutes,
                request.Mappings?.Select(mapping => new KitaronPushMapping(
                    mapping.KitaronColumn ?? string.Empty, mapping.PlannerValue ?? string.Empty, mapping.Enabled)).ToArray(),
                request.ExpectedVersion, user!.UserName, cancellationToken);
            return Results.Ok(KitaronPushResponse.From(settings, await service.ListRunsAsync(cancellationToken)));
        }
        catch (KitaronPushValidationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, "validation_failed",
                exception.Message, context, [new { field = exception.Field }]);
        }
    }

    private static async Task<IResult> PreviewAsync(
        HttpContext context, KitaronPushService service, CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageSetup, out _, out var error)) return error!;
        try
        {
            return Results.Ok(await service.PreviewAsync(cancellationToken));
        }
        catch (KitaronPushBlockedException exception)
        {
            return Blocked(exception, context);
        }
    }

    private static async Task<IResult> RunAsync(
        HttpContext context, KitaronPushService service, CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageSetup, out var user, out var error)) return error!;
        try
        {
            return Results.Ok(await service.RunAsync("manual", user!.UserName, cancellationToken,
                context.Request.Headers.TryGetValue("If-Kitaron-Preview-Match", out var stamp) ? stamp.ToString() : null));
        }
        catch (KitaronPushBlockedException exception)
        {
            return Blocked(exception, context);
        }
    }

    private static async Task<IResult> ChangesAsync(
        string runId, KitaronPushService service, CancellationToken cancellationToken) =>
        Results.Ok(new { items = await service.ListChangesAsync(runId, cancellationToken) });

    private static IResult Blocked(KitaronPushBlockedException exception, HttpContext context) =>
        PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception is KitaronPushConflictException ? "kitaron_push_conflict" : "kitaron_push_blocked", exception.Message, context);
}

internal sealed record KitaronPushSettingsRequest(
    bool Enabled,
    int IntervalMinutes,
    IReadOnlyList<KitaronPushMappingRequest>? Mappings,
    int ExpectedVersion);

internal sealed record KitaronPushMappingRequest(string? KitaronColumn, string? PlannerValue, bool Enabled);

internal sealed record KitaronPushResponse(
    bool Enabled,
    int IntervalMinutes,
    IReadOnlyList<KitaronPushMapping> Mappings,
    int Version,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy,
    IReadOnlyList<KitaronPushTarget> KitaronColumns,
    IReadOnlyList<KitaronPushSource> PlannerValues,
    IReadOnlyList<KitaronPushRunSummary> Runs)
{
    internal static KitaronPushResponse From(KitaronPushSettings settings, IReadOnlyList<KitaronPushRunSummary> runs) => new(
        settings.Enabled, settings.IntervalMinutes, settings.Mappings, settings.Version, settings.UpdatedAt,
        settings.UpdatedBy, KitaronPushCatalog.Targets, KitaronPushCatalog.Sources, runs);
}
