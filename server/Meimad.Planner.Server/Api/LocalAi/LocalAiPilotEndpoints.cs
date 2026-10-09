using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.LocalAi;
namespace Meimad.Planner.Server.Api.LocalAi;

internal static class LocalAiPilotEndpoints
{
    internal static void MapLocalAiPilotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/local-ai/control", async (HttpContext context, LocalAiRequestService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageSetup, out _, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.ReadControlAsync(token));
        });
        endpoints.MapPut("/api/v1/local-ai/control", async (LocalAiControlUpdate request, HttpContext context, LocalAiRequestService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageSetup, out var user, out var error)) return error!;
            try { return Results.Ok(await service.UpdateControlAsync(request, user!.UserName, token)); }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "ai_configuration_required", exception.Message, context); }
            catch (InvalidOperationException exception) { return PlanningHttpSupport.Error(409, "ai_control_changed", exception.Message, context); }
        });
        endpoints.MapGet("/api/v1/local-ai/settings", async (HttpContext context, LocalAiRequestService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageSetup, out _, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.ReadSettingsAsync(token));
        });
        endpoints.MapPut("/api/v1/local-ai/settings", async (LocalAiSettingsUpdate request, HttpContext context, LocalAiRequestService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageSetup, out var user, out var error)) return error!;
            try { return Results.Ok(await service.UpdateSettingsAsync(request, user!.UserName, token)); }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "ai_settings_invalid", exception.Message, context); }
            catch (InvalidOperationException exception) { return PlanningHttpSupport.Error(409, "ai_settings_changed", exception.Message, context); }
        });
        endpoints.MapGet("/api/v1/local-ai/runtime", async (HttpContext context, LocalAiRequestService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageSetup, out _, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.ProbeRuntimeAsync(token));
        });
        endpoints.MapGet("/api/v1/local-ai/requests/{id}/feedback", async (string id, HttpContext context, LocalAiRequestService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out var user, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            if (await service.ReadAsync(user!.UserId, id, token) is null) return Results.NotFound();
            return Results.Ok(await service.ReadFeedbackAsync(user.UserId, id, token));
        });
        endpoints.MapPut("/api/v1/local-ai/requests/{id}/feedback", async (string id, LocalAiFeedbackUpdate request, HttpContext context, LocalAiRequestService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out var user, out var error)) return error!;
            try
            {
                var saved = await service.SaveFeedbackAsync(user!.UserId, id, request, token);
                return saved is null ? Results.NotFound() : Results.Ok(saved);
            }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "ai_feedback_invalid", exception.Message, context); }
            catch (InvalidOperationException exception) { return PlanningHttpSupport.Error(409, "ai_feedback_changed", exception.Message, context); }
        });
    }
}
