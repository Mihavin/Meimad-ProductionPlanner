using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.TvDashboard;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.Cases;

/// <summary>
/// <c>/api/v1/cases/{caseId}/operations/{operationId}/thumbnails</c> (schema v118): the part pictures of a Case Operation
/// that the TV Dashboard shows when the Case has no picture of its own. Two slots, <c>package</c> (the finished-part
/// thumbnail of the Cimatron simulation package) and <c>snapshot</c> (a screenshot or preview). Every signed-in user reads
/// them; PUT (raw PNG/JPEG/BMP/GIF body of at most 10 MB, <c>?name=</c>, optional <c>&amp;reference=</c> naming where it
/// came from) and DELETE need <c>cases.edit</c>. PUT creates with <c>If-None-Match: *</c> and replaces with
/// <c>If-Match</c>; DELETE needs <c>If-Match</c>. A change based on an older version is refused with 412 and names who
/// changed the slot and when.
/// </summary>
internal static class CaseOperationThumbnailEndpoints
{
    private const string EntityTagKind = "case-operation-thumbnail";

    internal static void MapCaseOperationThumbnailEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var route = endpoints.MapGroup("/api/v1/cases/{caseId}/operations/{operationId}/thumbnails");
        route.MapGet(string.Empty, ListAsync);
        route.MapGet("/{source}/image", ImageAsync);
        route.MapPut("/{source}", PutAsync)
            .WithMetadata(new RequestSizeLimitAttribute(OperationThumbnailStore.MaximumBytes + 1024 * 1024));
        route.MapDelete("/{source}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(
        string caseId, string operationId, HttpContext httpContext, SqliteOperationThumbnailRepository repository,
        CancellationToken cancellationToken)
    {
        var set = await repository.ListAsync(caseId, operationId, cancellationToken);
        if (!set.OperationExists) return OperationNotFound(httpContext);
        var tvSource = set.CaseHasPicture ? "case_picture" : set.Thumbnails.FirstOrDefault()?.Source ?? "placeholder";
        return Results.Ok(new
        {
            caseOperationId = operationId,
            tvSource,
            items = set.Thumbnails.Select(Response).ToArray()
        });
    }

    private static async Task<IResult> ImageAsync(
        string caseId, string operationId, string source, HttpContext httpContext,
        SqliteOperationThumbnailRepository repository, OperationThumbnailStore store, CancellationToken cancellationToken)
    {
        if (!OperationThumbnailSources.IsKnown(source)) return UnknownSource(httpContext);
        var thumbnail = await repository.GetAsync(caseId, operationId, source, cancellationToken);
        if (thumbnail is null || store.PathOf(thumbnail.StoredRelativePath) is not { } path) return ThumbnailNotFound(httpContext);
        httpContext.Response.Headers.ETag = EntityTag(thumbnail);
        return Results.File(path, thumbnail.MediaType);
    }

    private static async Task<IResult> PutAsync(
        string caseId,
        string operationId,
        string source,
        string? name,
        string? reference,
        HttpContext httpContext,
        SqliteOperationThumbnailRepository repository,
        OperationThumbnailStore store,
        IAccountRepository accounts,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(httpContext, Permissions.EditCases, out var authority, out var accessError))
            return accessError!;
        if (!OperationThumbnailSources.IsKnown(source)) return UnknownSource(httpContext);

        var ifMatch = httpContext.Request.Headers.IfMatch;
        var ifNoneMatch = httpContext.Request.Headers.IfNoneMatch;
        int? expectedVersion;
        if (!StringValues.IsNullOrEmpty(ifMatch))
        {
            if (!StringValues.IsNullOrEmpty(ifNoneMatch))
                return PreconditionRequired(httpContext, "Send either If-None-Match: * to create or If-Match to replace, not both.");
            if (!PlanningHttpSupport.TryReadExpectedVersion(ifMatch, EntityTagKind, SlotId(operationId, source), out var version))
                return await StaleAsync(httpContext, null, accounts, cancellationToken);
            expectedVersion = version;
        }
        else if (ifNoneMatch.Count == 1 && ifNoneMatch[0]?.Trim() == "*")
        {
            expectedVersion = null;
        }
        else
        {
            return PreconditionRequired(httpContext,
                $"Send If-None-Match: * to add the thumbnail or If-Match: \"{EntityTagKind}:{SlotId(operationId, source)}:v<version>\" to replace it.");
        }

        if (httpContext.Request.ContentLength > OperationThumbnailStore.MaximumBytes) return TooLarge(httpContext);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await httpContext.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > OperationThumbnailStore.MaximumBytes) return TooLarge(httpContext);
            buffer.Write(chunk, 0, read);
        }

        try
        {
            var actor = SignedInActor.Require(authority!);
            var thumbnail = await store.WriteAsync(operationId, source, name, buffer.ToArray(), reference, actor,
                timeProvider.GetUtcNow(), cancellationToken);
            var result = await repository.SaveAsync(caseId, thumbnail, expectedVersion, cancellationToken);
            switch (result.Outcome)
            {
                case OperationThumbnailOutcome.Created:
                    httpContext.Response.Headers.ETag = EntityTag(result.Thumbnail!);
                    return Results.Created(
                        $"/api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(operationId)}/thumbnails/{source}",
                        Response(result.Thumbnail!));
                case OperationThumbnailOutcome.Updated:
                    httpContext.Response.Headers.ETag = EntityTag(result.Thumbnail!);
                    return Results.Ok(Response(result.Thumbnail!));
                case OperationThumbnailOutcome.OperationNotFound:
                    return OperationNotFound(httpContext);
                default:
                    // AlreadyExists, ThumbnailNotFound (removed meanwhile) and Stale: the change was based on old data.
                    return await StaleAsync(httpContext, result.Thumbnail, accounts, cancellationToken);
            }
        }
        catch (CasePictureException exception)
        {
            return PlanningHttpSupport.Error(
                exception.Code == "thumbnail_too_large" ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status422UnprocessableEntity,
                exception.Code, exception.Message, httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    private static async Task<IResult> DeleteAsync(
        string caseId,
        string operationId,
        string source,
        HttpContext httpContext,
        SqliteOperationThumbnailRepository repository,
        IAccountRepository accounts,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(httpContext, Permissions.EditCases, out var authority, out var accessError))
            return accessError!;
        if (!OperationThumbnailSources.IsKnown(source)) return UnknownSource(httpContext);
        var ifMatch = httpContext.Request.Headers.IfMatch;
        if (StringValues.IsNullOrEmpty(ifMatch))
            return PreconditionRequired(httpContext, $"If-Match: \"{EntityTagKind}:{SlotId(operationId, source)}:v<version>\" is required.");
        if (!PlanningHttpSupport.TryReadExpectedVersion(ifMatch, EntityTagKind, SlotId(operationId, source), out var expectedVersion))
            return await StaleAsync(httpContext, null, accounts, cancellationToken);

        try
        {
            var result = await repository.DeleteAsync(caseId, operationId, source, expectedVersion,
                SignedInActor.Require(authority!), timeProvider.GetUtcNow(), cancellationToken);
            return result.Outcome switch
            {
                OperationThumbnailOutcome.Deleted => Results.NoContent(),
                OperationThumbnailOutcome.OperationNotFound => OperationNotFound(httpContext),
                OperationThumbnailOutcome.ThumbnailNotFound => ThumbnailNotFound(httpContext),
                _ => await StaleAsync(httpContext, result.Thumbnail, accounts, cancellationToken)
            };
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, httpContext);
        }
    }

    /// <summary>412 <c>resource_version_stale</c>, naming who saved the current thumbnail and when when it is known.</summary>
    private static async Task<IResult> StaleAsync(
        HttpContext httpContext, OperationThumbnail? current, IAccountRepository accounts, CancellationToken cancellationToken)
    {
        const string message = "The Operation thumbnail changed since you opened it, so your change was not saved.";
        if (current is null)
            return PlanningHttpSupport.Error(StatusCodes.Status412PreconditionFailed, "resource_version_stale", message, httpContext);

        var changedBy = current.UpdatedBy;
        if (!string.IsNullOrWhiteSpace(changedBy)
            && await accounts.FindDisplayNameAsync(changedBy, cancellationToken) is { } displayName
            && !string.Equals(displayName, changedBy, StringComparison.OrdinalIgnoreCase))
        {
            changedBy = $"{displayName} ({changedBy})";
        }

        return Results.Json(
            new
            {
                error = new
                {
                    code = "resource_version_stale",
                    message,
                    correlationId = httpContext.TraceIdentifier,
                    details = new[] { new { currentVersion = current.Version, currentEntityTag = EntityTag(current) } },
                    conflict = new
                    {
                        resource = "Operation thumbnail",
                        changedBy,
                        changedAt = current.UpdatedAt,
                        advice = "Someone saved this Operation thumbnail after you opened it, so your change was not saved. " +
                                 "Refresh to see the current thumbnail, then make your change again."
                    }
                }
            },
            statusCode: StatusCodes.Status412PreconditionFailed);
    }

    private static object Response(OperationThumbnail thumbnail) => new
    {
        caseOperationId = thumbnail.CaseOperationId,
        source = thumbnail.Source,
        fileName = thumbnail.FileName,
        mediaType = thumbnail.MediaType,
        sizeBytes = thumbnail.SizeBytes,
        sha256 = thumbnail.Sha256,
        sourceReference = thumbnail.SourceReference,
        version = thumbnail.Version,
        entityTag = EntityTag(thumbnail),
        updatedAt = thumbnail.UpdatedAt,
        updatedBy = thumbnail.UpdatedBy
    };

    private static string SlotId(string operationId, string source) => $"{operationId}:{source}";

    private static string EntityTag(OperationThumbnail thumbnail) =>
        $"\"{EntityTagKind}:{SlotId(thumbnail.CaseOperationId, thumbnail.Source)}:v{thumbnail.Version}\"";

    private static IResult TooLarge(HttpContext httpContext) => PlanningHttpSupport.Error(
        StatusCodes.Status413PayloadTooLarge, "thumbnail_too_large",
        $"An Operation thumbnail may be at most {OperationThumbnailStore.MaximumBytes / (1024 * 1024)} MB.", httpContext);

    private static IResult PreconditionRequired(HttpContext httpContext, string message) =>
        PlanningHttpSupport.Error(StatusCodes.Status428PreconditionRequired, "precondition_required", message, httpContext);

    private static IResult UnknownSource(HttpContext httpContext) => PlanningHttpSupport.Error(
        StatusCodes.Status400BadRequest, "invalid_thumbnail_source",
        "The thumbnail source must be package or snapshot.", httpContext);

    private static IResult OperationNotFound(HttpContext httpContext) => PlanningHttpSupport.Error(
        StatusCodes.Status404NotFound, "resource_not_found", "The requested Case Operation was not found in this Case.", httpContext);

    private static IResult ThumbnailNotFound(HttpContext httpContext) => PlanningHttpSupport.Error(
        StatusCodes.Status404NotFound, "thumbnail_not_found", "This Operation has no thumbnail of that kind.", httpContext);
}
