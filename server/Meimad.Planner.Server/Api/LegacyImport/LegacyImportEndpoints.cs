using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.LegacyImport;
using Meimad.Planner.Server.Domain.LegacyImport;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Meimad.Planner.Server.Api.LegacyImport;

internal static class LegacyImportEndpoints
{
    private const long RequestLimit = OpenXmlLegacyWorkbookReader.MaximumWorkbookBytes + (1024 * 1024);

    internal static void MapLegacyImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var imports = endpoints.MapGroup("/api/v1/imports/legacy-working-plan");
        imports.MapPost("/preview", PreviewAsync)
            .WithMetadata(
                new RequestSizeLimitAttribute(RequestLimit),
                new RequestFormLimitsAttribute
                {
                    MultipartBodyLengthLimit = RequestLimit,
                    ValueLengthLimit = 1024 * 1024,
                    MultipartHeadersLengthLimit = 16 * 1024
                });
        imports.MapPost("/commit", CommitAsync);
    }

    private static async Task<IResult> PreviewAsync(
        HttpContext httpContext,
        LegacyImportService service,
        CancellationToken cancellationToken)
    {
        if (!httpContext.Request.HasFormContentType
            || !httpContext.Request.ContentType!.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status415UnsupportedMediaType,
                "unsupported_media_type",
                "Preview requires multipart/form-data with one file field named 'workbook'.",
                httpContext);
        }

        try
        {
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            var files = form.Files.GetFiles("workbook");
            if (files.Count != 1 || form.Files.Count != 1)
            {
                return PlanningHttpSupport.Error(
                    StatusCodes.Status400BadRequest,
                    "workbook_required",
                    "Provide exactly one uploaded file in the 'workbook' field.",
                    httpContext);
            }

            var file = files[0];
            if (file.Length > OpenXmlLegacyWorkbookReader.MaximumWorkbookBytes)
            {
                return PlanningHttpSupport.Error(
                    StatusCodes.Status413PayloadTooLarge,
                    "workbook_too_large",
                    "The workbook exceeds the 64 MiB upload limit.",
                    httpContext);
            }

            await using var stream = file.OpenReadStream();
            return Results.Ok(await service.PreviewAsync(
                stream,
                file.FileName,
                cancellationToken));
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status413PayloadTooLarge,
                "workbook_too_large",
                "The multipart request exceeds the 65 MiB endpoint limit.",
                httpContext);
        }
        catch (LegacyWorkbookFormatException exception)
        {
            var status = exception.Code is "workbook_too_large" or "archive_entry_limit_exceeded"
                or "xml_part_too_large" or "xml_expansion_limit_exceeded"
                ? StatusCodes.Status413PayloadTooLarge
                : StatusCodes.Status422UnprocessableEntity;
            return PlanningHttpSupport.Error(status, exception.Code, exception.Message, httpContext);
        }
    }

    private static async Task<IResult> CommitAsync(
        LegacyImportCommitRequest request,
        HttpContext httpContext,
        LegacyImportService service,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryReadEditAuthority(httpContext, out var authority, out var accessError))
        {
            return accessError!;
        }

        try
        {
            return Results.Ok(await service.CommitAsync(request, authority!, cancellationToken));
        }
        catch (LegacyImportTokenExpiredException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status410Gone,
                "import_token_expired",
                exception.Message,
                httpContext);
        }
        catch (LegacyWorkbookAlreadyImportedException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                "workbook_already_imported",
                exception.Message,
                httpContext);
        }
        catch (LegacyImportValidationException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status422UnprocessableEntity,
                "import_validation_failed",
                exception.Message,
                httpContext,
                exception.Issues.Select(LegacyImportIssueResponse.FromDomain));
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(
                StatusCodes.Status409Conflict,
                exception.Code,
                exception.Message,
                httpContext);
        }
    }
}
