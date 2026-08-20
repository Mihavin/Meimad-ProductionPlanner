using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Postprocessors;
using Meimad.Planner.Server.Domain.Postprocessors;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.Postprocessors;

internal static class PostprocessorEndpoints
{
    internal static void MapPostprocessorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var values = endpoints.MapGroup("/api/v1/postprocessors");
        values.MapGet(string.Empty, ListAsync);
        values.MapPost(string.Empty, CreateAsync);
        values.MapGet("/{postprocessorId}", GetAsync);
        values.MapPatch("/{postprocessorId}", UpdateAsync);
        values.MapDelete("/{postprocessorId}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(
        PostprocessorService service,
        CancellationToken token)
    {
        var values = await service.ListAsync(token);
        return Results.Ok(new PostprocessorListResponse(
            values.Select(PostprocessorResponse.FromDomain).ToArray(),
            null));
    }

    private static async Task<IResult> GetAsync(
        string postprocessorId,
        HttpContext context,
        PostprocessorService service,
        CancellationToken token)
    {
        var value = await service.GetByIdAsync(postprocessorId, token);
        if (value is null)
        {
            return NotFound(context);
        }

        SetTag(context.Response, value);
        return Results.Ok(PostprocessorResponse.FromDomain(value));
    }

    private static async Task<IResult> CreateAsync(
        CreatePostprocessorRequest request,
        HttpContext context,
        PostprocessorService service,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
        {
            return error!;
        }

        try
        {
            var value = await service.CreateAsync(request.ToCommand(), authority!, token);
            SetTag(context.Response, value);
            return Results.Created(
                $"/api/v1/postprocessors/{value.PostprocessorId}",
                PostprocessorResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> UpdateAsync(
        string postprocessorId,
        PatchPostprocessorRequest request,
        HttpContext context,
        PostprocessorService service,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
        {
            return error!;
        }

        if (!PlanningHttpSupport.TryReadExpectedVersion(
                context.Request.Headers.IfMatch,
                "postprocessor",
                postprocessorId,
                out var version))
        {
            var missing = StringValues.IsNullOrEmpty(context.Request.Headers.IfMatch);
            return PlanningHttpSupport.Error(
                missing ? 428 : 412,
                missing ? "precondition_required" : "resource_version_stale",
                "A matching Postprocessor If-Match header is required.",
                context);
        }

        try
        {
            var value = await service.UpdateAsync(
                postprocessorId,
                version,
                request.ToCommand(),
                authority!,
                token);
            SetTag(context.Response, value);
            return Results.Ok(PostprocessorResponse.FromDomain(value));
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static async Task<IResult> DeleteAsync(
        string postprocessorId,
        HttpContext context,
        PostprocessorService service,
        CancellationToken token)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(context, out var authority, out var error))
        {
            return error!;
        }

        try
        {
            return await service.DeleteAsync(postprocessorId, authority!, token)
                ? Results.NoContent()
                : NotFound(context);
        }
        catch (Exception exception) when (TryMap(exception, context, out var mapped))
        {
            return mapped!;
        }
    }

    private static bool TryMap(Exception exception, HttpContext context, out IResult? result)
    {
        result = exception switch
        {
            PostprocessorRequestException request => PlanningHttpSupport.Error(
                400,
                "invalid_request",
                request.Message,
                context,
                request.Issues.Select(issue => (object)new
                {
                    field = string.IsNullOrEmpty(issue.Field) ? null : issue.Field,
                    code = issue.Code,
                    message = issue.Message
                })),
            PostprocessorValidationException validation => PlanningHttpSupport.Error(
                422,
                "validation_failed",
                validation.Message,
                context,
                validation.Issues.Select(issue => (object)new
                {
                    field = issue.Field,
                    code = issue.Code,
                    message = issue.Message
                })),
            PostprocessorNameConflictException => PlanningHttpSupport.Error(
                409, "postprocessor_name_conflict", exception.Message, context),
            PostprocessorNotFoundException => NotFound(context),
            PostprocessorVersionConflictException => PlanningHttpSupport.Error(
                412, "resource_version_stale", exception.Message, context),
            PostprocessorInUseException => PlanningHttpSupport.Error(
                409, "postprocessor_in_use", exception.Message, context),
            EditModeMutationException edit => PlanningHttpSupport.Error(
                409, edit.Code, edit.Message, context),
            _ => null
        };
        return result is not null;
    }

    private static IResult NotFound(HttpContext context) => PlanningHttpSupport.Error(
        404,
        "resource_not_found",
        "The requested Postprocessor was not found.",
        context);

    private static void SetTag(HttpResponse response, Postprocessor value) =>
        response.Headers.ETag = $"\"postprocessor:{value.PostprocessorId}:v{value.Version}\"";
}
