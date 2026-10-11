using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.Cases;

/// <summary>
/// <c>/api/v1/cases/{caseId}/picture</c> (schema v115): the Case picture stored on the Server. Every signed-in user reads
/// its details; PUT (raw PNG/JPEG/BMP/GIF body ≤ 20 MB, <c>?name=</c>) and DELETE need <c>cases.edit</c> and the Case's
/// <c>If-Match</c>, change the Case's version and return the Case. The image itself is served by <c>GET …/preview</c>,
/// which prefers the stored picture over the Case's picture link.
/// </summary>
internal static class CasePictureEndpoints
{
    internal static void MapCasePictureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var route = endpoints.MapGroup("/api/v1/cases/{caseId}/picture");
        route.MapGet(string.Empty, GetAsync);
        route.MapPut(string.Empty, PutAsync)
            .WithMetadata(new RequestSizeLimitAttribute(CasePictureStore.MaximumBytes + 1024 * 1024));
        route.MapDelete(string.Empty, DeleteAsync);
    }

    private static async Task<IResult> GetAsync(
        string caseId, HttpContext httpContext, CaseService cases, SqliteCasePictureRepository repository, CancellationToken cancellationToken)
    {
        if (await cases.GetByIdAsync(caseId, cancellationToken) is null) return CaseNotFound(httpContext);
        var picture = await repository.GetAsync(caseId, cancellationToken);
        return picture is null
            ? PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "picture_not_stored", "This Case has no picture stored on the Server.", httpContext)
            : Results.Ok(Response(picture));
    }

    private static async Task<IResult> PutAsync(
        string caseId,
        string? name,
        HttpContext httpContext,
        CaseService cases,
        SqliteCasePictureRepository repository,
        CasePictureStore store,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(httpContext, Permissions.EditCases, out var authority, out var accessError))
            return accessError!;
        if (!TryReadVersion(httpContext, caseId, out var expectedVersion, out var preconditionError)) return preconditionError!;
        if (httpContext.Request.ContentLength > CasePictureStore.MaximumBytes)
            return PlanningHttpSupport.Error(StatusCodes.Status413PayloadTooLarge, "picture_too_large",
                $"A Case picture may be at most {CasePictureStore.MaximumBytes / (1024 * 1024)} MB.", httpContext);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await httpContext.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > CasePictureStore.MaximumBytes)
                return PlanningHttpSupport.Error(StatusCodes.Status413PayloadTooLarge, "picture_too_large",
                    $"A Case picture may be at most {CasePictureStore.MaximumBytes / (1024 * 1024)} MB.", httpContext);
            buffer.Write(chunk, 0, read);
        }
        try
        {
            var actor = SignedInActor.Require(authority!);
            var now = timeProvider.GetUtcNow();
            if (await cases.GetByIdAsync(caseId, cancellationToken) is null) return CaseNotFound(httpContext);
            var picture = await store.WriteAsync(caseId, name, buffer.ToArray(), null, actor, now, cancellationToken);
            return await ResultAsync(await repository.SaveAsync(picture, expectedVersion, actor, now, cancellationToken),
                caseId, httpContext, cases, cancellationToken);
        }
        catch (CasePictureException exception)
        {
            return PlanningHttpSupport.Error(
                exception.Code == "picture_too_large" ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status422UnprocessableEntity,
                exception.Code, exception.Message, httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    private static async Task<IResult> DeleteAsync(
        string caseId,
        HttpContext httpContext,
        CaseService cases,
        SqliteCasePictureRepository repository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(httpContext, Permissions.EditCases, out var authority, out var accessError))
            return accessError!;
        if (!TryReadVersion(httpContext, caseId, out var expectedVersion, out var preconditionError)) return preconditionError!;
        try
        {
            var outcome = await repository.DeleteAsync(caseId, expectedVersion, SignedInActor.Require(authority!), timeProvider.GetUtcNow(), cancellationToken);
            return outcome == CasePictureOutcome.PictureNotFound
                ? PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "picture_not_stored", "This Case has no picture stored on the Server.", httpContext)
                : await ResultAsync(outcome, caseId, httpContext, cases, cancellationToken);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    private static async Task<IResult> ResultAsync(
        CasePictureOutcome outcome, string caseId, HttpContext httpContext, CaseService cases, CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case CasePictureOutcome.CaseNotFound:
                return CaseNotFound(httpContext);
            case CasePictureOutcome.Stale:
                return PlanningHttpSupport.Error(StatusCodes.Status412PreconditionFailed, "resource_version_stale",
                    "The Case changed after it was read, so the picture was not changed. Refresh the Case and try again.", httpContext);
            default:
                var updated = await cases.GetByIdAsync(caseId, cancellationToken);
                if (updated is null) return CaseNotFound(httpContext);
                httpContext.Response.Headers.ETag = $"\"case:{updated.CaseId}:v{updated.Version}\"";
                return Results.Ok(CaseResponse.FromDomain(updated));
        }
    }

    private static bool TryReadVersion(HttpContext httpContext, string caseId, out int version, out IResult? error)
    {
        error = null;
        var ifMatch = httpContext.Request.Headers.IfMatch;
        if (StringValues.IsNullOrEmpty(ifMatch))
        {
            version = 0;
            error = PlanningHttpSupport.Error(StatusCodes.Status428PreconditionRequired, "precondition_required",
                "A matching Case If-Match header is required.", httpContext);
            return false;
        }
        if (PlanningHttpSupport.TryReadExpectedVersion(ifMatch, "case", caseId, out version)) return true;
        error = PlanningHttpSupport.Error(StatusCodes.Status412PreconditionFailed, "resource_version_stale",
            "The Case changed after it was read, so the picture was not changed. Refresh the Case and try again.", httpContext);
        return false;
    }

    private static object Response(CasePicture picture) => new
    {
        caseId = picture.CaseId,
        fileName = picture.FileName,
        mediaType = picture.MediaType,
        sizeBytes = picture.SizeBytes,
        sha256 = picture.Sha256,
        sourceReference = picture.SourceReference,
        createdAt = picture.CreatedAt,
        createdBy = picture.CreatedBy
    };

    private static IResult CaseNotFound(HttpContext httpContext) => PlanningHttpSupport.Error(
        StatusCodes.Status404NotFound, "resource_not_found", "The requested Case was not found.", httpContext);
}
