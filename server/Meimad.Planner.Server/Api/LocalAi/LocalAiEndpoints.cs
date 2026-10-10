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
        // Free-text assistant questions (owner request 2026-10-09). Read-only; answers stay in Server memory.
        endpoints.MapPost("/api/v1/local-ai/chat", (ChatRequest request, HttpContext context, LocalAiChatService chat) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out var user, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var work = chat.Submit(user!.UserId, request);
                return Results.Accepted("/api/v1/local-ai/chat/" + work.ChatId, work);
            }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "chat_invalid", exception.Message, context); }
            catch (LocalAiUnavailableException exception) { return PlanningHttpSupport.Error(409, "ai_unavailable", exception.Message, context); }
            catch (LocalAiBusyException) { context.Response.Headers.RetryAfter = "5"; return PlanningHttpSupport.Error(429, "ai_busy", "One active question per user and eight queued questions are allowed. Retry shortly.", context); }
        });
        endpoints.MapGet("/api/v1/local-ai/chat/{id}", (string id, HttpContext context, LocalAiChatService chat) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out var user, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            return chat.Read(user!.UserId, id) is { } work ? Results.Ok(work) : Results.NotFound();
        });
        endpoints.MapDelete("/api/v1/local-ai/chat/{id}", (string id, HttpContext context, LocalAiChatService chat) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out var user, out var error)) return error!;
            return chat.Cancel(user!.UserId, id) ? Results.Accepted() : Results.NotFound();
        });
        // Simulation data of an Operation's NC program (owner request 2026-10-10): read-only suggestions the
        // planner confirms; the local model ranks rule-found files and the rules alone answer when it is off.
        endpoints.MapPost("/api/v1/local-ai/simulation-data", (SimulationDataSearchRequest request, HttpContext context, SimulationDataSearchService search) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out var user, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var work = search.Submit(user!.UserId, request);
                return Results.Accepted("/api/v1/local-ai/simulation-data/" + work.SearchId, work);
            }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "simulation_data_invalid", exception.Message, context); }
            catch (LocalAiBusyException) { context.Response.Headers.RetryAfter = "5"; return PlanningHttpSupport.Error(429, "ai_busy", "One active search per user and eight queued searches are allowed. Retry shortly.", context); }
        });
        endpoints.MapGet("/api/v1/local-ai/simulation-data/{id}", (string id, HttpContext context, SimulationDataSearchService search) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out var user, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            return search.Read(user!.UserId, id) is { } work ? Results.Ok(work) : Results.NotFound();
        });
        endpoints.MapDelete("/api/v1/local-ai/simulation-data/{id}", (string id, HttpContext context, SimulationDataSearchService search) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out var user, out var error)) return error!;
            return search.Cancel(user!.UserId, id) ? Results.Accepted() : Results.NotFound();
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
