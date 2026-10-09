using Meimad.Planner.Server.Application.LocalAi;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Application.Projections;

namespace Meimad.Planner.Server.Api.LocalAi;

internal static class LocalAiEndpoints
{
    internal static void MapLocalAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/local-ai/requests", async (ExplanationRequest request, HttpContext context,
            LocalAiRequestService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out var user, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var work = await service.SubmitAsync(user!.UserId, request, token);
                return Results.Accepted("/api/v1/local-ai/requests/" + work.RequestId, work);
            }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "explanation_invalid", exception.Message, context); }
            catch (LocalAiBusyException) { context.Response.Headers.RetryAfter = "5"; return PlanningHttpSupport.Error(429, "ai_busy", "One active request per user and eight queued requests are allowed. Retry later.", context); }
        });
        endpoints.MapGet("/api/v1/local-ai/requests/{id}", async (string id, HttpContext context,
            LocalAiRequestService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out var user, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            var work = await service.ReadAsync(user!.UserId, id, token);
            return work is null ? Results.NotFound() : Results.Ok(work);
        });
        endpoints.MapDelete("/api/v1/local-ai/requests/{id}", (string id, HttpContext context, LocalAiRequestService service) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out var user, out var error)) return error!;
            return service.Cancel(user!.UserId, id) ? Results.Accepted() : Results.NotFound();
        });
        endpoints.MapGet("/api/v1/local-ai/requests/{id}/evidence/{evidenceId}", async (string id, string evidenceId,
            HttpContext context, LocalAiRequestService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out var user, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            var work = await service.ReadAsync(user!.UserId, id, token);
            var fact = work?.Result?.Evidence.Facts.SingleOrDefault(x => x.EvidenceId == evidenceId);
            return fact is null ? Results.NotFound() : Results.Ok(new EvidenceInspection(fact,
                work!.Result!.Currency, work.Result.CheckedAt, work.Result.Evidence.AsOfUtc));
        });
        endpoints.MapPost("/api/v1/local-ai/explanations", async (ExplanationRequest request, HttpContext context,
            LocalEvidenceService evidence, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out _, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var bundle = await evidence.ReadAsync(request, token);
                return Results.Ok(new LocalExplanation(Guid.NewGuid().ToString("N"), "DETERMINISTIC", bundle, bundle.Claims, "Deterministic explanation requested."));
            }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "explanation_invalid", exception.Message, context); }
            catch (ProductionRunNotFoundException exception) { return PlanningHttpSupport.Error(404, "resource_not_found", exception.Message, context); }
            catch (ProjectionChangedException exception) { return PlanningHttpSupport.Error(409, "projection_changed", exception.Message, context); }
            catch (LocalEvidenceBusyException) { context.Response.Headers.RetryAfter = "2"; return PlanningHttpSupport.Error(429, "evidence_busy", "Evidence reads are busy; retry shortly.", context); }
        });
    }
    private sealed record EvidenceInspection(EvidenceFact Fact, string Currency, DateTimeOffset? CheckedAt, DateTimeOffset SampledAt);
}
