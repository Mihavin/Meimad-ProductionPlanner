using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.LocalAi;
namespace Meimad.Planner.Server.Api.LocalAi;

internal static class LocalCorpusEndpoints
{
    internal static void MapLocalCorpusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/local-ai/corpus", async (HttpContext context, LocalCorpusService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageSetup, out _, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.ReadAsync(token));
        });
        endpoints.MapPut("/api/v1/local-ai/corpus", async (CorpusUpdate request, HttpContext context, LocalCorpusService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageSetup, out var user, out var error)) return error!;
            try { return Results.Ok(await service.UpdateAsync(request, user!.UserName, token)); }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "corpus_invalid", exception.Message, context); }
            catch (InvalidOperationException exception) { return PlanningHttpSupport.Error(409, "corpus_changed", exception.Message, context); }
        });
        endpoints.MapPost("/api/v1/local-ai/corpus/reindex", async (CorpusReindex request, HttpContext context, LocalCorpusService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageSetup, out _, out var error)) return error!;
            try { return Results.Ok(await service.IndexAsync(request.ExpectedVersion, token)); }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "corpus_invalid", exception.Message, context); }
            catch (InvalidOperationException exception) { return PlanningHttpSupport.Error(409, "corpus_changed", exception.Message, context); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            { return PlanningHttpSupport.Error(422, "corpus_unavailable", "An approved folder cannot be read. Check Server access; the previous index is retained.", context); }
        });
        endpoints.MapGet("/api/v1/local-ai/documents/search", async (string query, HttpContext context, LocalCorpusService service, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, null, out _, out var error)) return error!;
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await service.SearchAsync(query, token)); }
            catch (ArgumentException exception) { return PlanningHttpSupport.Error(400, "corpus_invalid", exception.Message, context); }
            catch (InvalidOperationException exception) { return PlanningHttpSupport.Error(409, "corpus_changed", exception.Message, context); }
        });
    }
    private sealed record CorpusReindex(int ExpectedVersion);
}
