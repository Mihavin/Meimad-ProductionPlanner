using System.Globalization;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Cases;
using Meimad.Planner.Server.Domain.CaseOperations;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.Cases;

internal static class CaseEndpoints
{
    private const string ClientIdHeader = "X-Meimad-Client-Id";
    private const string EditGenerationHeader = "X-Meimad-Edit-Generation";

    internal static void MapCaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var cases = endpoints.MapGroup("/api/v1/cases");
        cases.MapPost(string.Empty, CreateAsync);
        cases.MapGet(string.Empty, ListAsync);
        cases.MapGet("/{caseId}", GetByIdAsync);
        cases.MapGet("/{caseId}/operations", ListOperationsAsync);
        cases.MapPost("/{caseId}/operations", CreateOperationAsync);
        cases.MapGet("/{caseId}/preview", GetPreviewAsync);
        cases.MapPatch("/{caseId}", UpdateAsync);
    }

    private static async Task<IResult> ListAsync(
        string? search,
        string? customer,
        bool? isActive,
        CaseService service,
        CancellationToken cancellationToken)
    {
        var items = await service.ListAsync(search, customer, isActive, cancellationToken);
        return Results.Ok(new CaseListResponse(
            items.Select(CaseResponse.FromDomain).ToArray(),
            null));
    }

    private static async Task<IResult> CreateAsync(
        CreateCaseRequest request,
        HttpContext httpContext,
        CaseService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadEditAuthority(httpContext, out var editAuthority, out var accessError))
        {
            return accessError!;
        }

        try
        {
            var created = await service.CreateAsync(
                request.ToCommand(),
                editAuthority!,
                cancellationToken);
            SetEntityTag(httpContext.Response, created);
            return Results.Created(
                $"/api/v1/cases/{created.CaseId}",
                CaseResponse.FromDomain(created));
        }
        catch (CaseValidationException exception)
        {
            return ValidationError(exception, httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return EditModeError(exception, httpContext);
        }
    }

    private static async Task<IResult> GetByIdAsync(
        string caseId,
        HttpContext httpContext,
        CaseService service,
        CancellationToken cancellationToken)
    {
        var plannerCase = await service.GetByIdAsync(caseId, cancellationToken);
        if (plannerCase is null)
        {
            return Error(
                StatusCodes.Status404NotFound,
                "resource_not_found",
                "The requested Case was not found.",
                httpContext);
        }

        SetEntityTag(httpContext.Response, plannerCase);
        return Results.Ok(CaseResponse.FromDomain(plannerCase));
    }

    private static async Task<IResult> ListOperationsAsync(
        string caseId,
        HttpContext httpContext,
        CaseService service,
        CancellationToken cancellationToken)
    {
        if (await service.GetByIdAsync(caseId, cancellationToken) is null)
        {
            return Error(
                StatusCodes.Status404NotFound,
                "resource_not_found",
                "The requested Case was not found.",
                httpContext);
        }

        var operations = await service.ListOperationsAsync(caseId, cancellationToken);
        return Results.Ok(new CaseOperationListResponse(
            operations.Select(CaseOperationResponse.FromApplication).ToArray(),
            null));
    }

    private static async Task<IResult> CreateOperationAsync(
        string caseId,
        CreateCaseOperationRequest request,
        HttpContext httpContext,
        CaseService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadEditAuthority(httpContext, out var editAuthority, out var accessError))
        {
            return accessError!;
        }

        try
        {
            var created = await service.CreateOperationAsync(
                caseId,
                request.ToCommand(),
                editAuthority!,
                cancellationToken);
            return Results.Created(
                $"/api/v1/cases/{caseId}/operations",
                CaseOperationResponse.FromApplication(created));
        }
        catch (CaseOperationValidationException exception)
        {
            return CaseOperationValidationError(
                exception.Issues.Select(issue => new
                {
                    field = issue.Field,
                    code = issue.Code,
                    message = issue.Message
                }),
                httpContext);
        }
        catch (CaseOperationGraphValidationException exception)
        {
            return CaseOperationValidationError(
                exception.Issues.Select(issue => new
                {
                    field = issue.Field,
                    code = issue.Code,
                    message = issue.Message
                }),
                httpContext);
        }
        catch (CaseNotFoundException)
        {
            return Error(
                StatusCodes.Status404NotFound,
                "resource_not_found",
                "The requested Case was not found.",
                httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return EditModeError(exception, httpContext);
        }
    }

    private static async Task<IResult> GetPreviewAsync(
        string caseId,
        HttpContext httpContext,
        CaseService service,
        CancellationToken cancellationToken)
    {
        var plannerCase = await service.GetByIdAsync(caseId, cancellationToken);
        if (plannerCase?.PreviewPath is null || !File.Exists(plannerCase.PreviewPath))
        {
            return Error(
                StatusCodes.Status404NotFound,
                "preview_not_found",
                "No preview is available for the requested Case.",
                httpContext);
        }

        var contentType = Path.GetExtension(plannerCase.PreviewPath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            _ => null
        };
        if (contentType is null)
        {
            return Error(
                StatusCodes.Status415UnsupportedMediaType,
                "preview_format_unsupported",
                "The Case preview format is not supported.",
                httpContext);
        }

        return Results.File(
            plannerCase.PreviewPath,
            contentType,
            enableRangeProcessing: true);
    }

    private static async Task<IResult> UpdateAsync(
        string caseId,
        PatchCaseRequest request,
        HttpContext httpContext,
        CaseService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadEditAuthority(httpContext, out var editAuthority, out var accessError))
        {
            return accessError!;
        }

        if (!TryReadExpectedVersion(httpContext.Request.Headers.IfMatch, caseId, out var expectedVersion))
        {
            var code = StringValues.IsNullOrEmpty(httpContext.Request.Headers.IfMatch)
                ? "precondition_required"
                : "resource_version_stale";
            var status = StringValues.IsNullOrEmpty(httpContext.Request.Headers.IfMatch)
                ? StatusCodes.Status428PreconditionRequired
                : StatusCodes.Status412PreconditionFailed;
            return Error(
                status,
                code,
                "A matching Case If-Match header is required.",
                httpContext);
        }

        try
        {
            var updated = await service.UpdateAsync(
                caseId,
                expectedVersion,
                request.ToCommand(),
                editAuthority!,
                cancellationToken);
            SetEntityTag(httpContext.Response, updated);
            return Results.Ok(CaseResponse.FromDomain(updated));
        }
        catch (CaseRequestException exception)
        {
            return RequestError(exception, httpContext);
        }
        catch (CaseValidationException exception)
        {
            return ValidationError(exception, httpContext);
        }
        catch (CaseNotFoundException)
        {
            return Error(
                StatusCodes.Status404NotFound,
                "resource_not_found",
                "The requested Case was not found.",
                httpContext);
        }
        catch (CaseVersionConflictException)
        {
            return Error(
                StatusCodes.Status412PreconditionFailed,
                "resource_version_stale",
                "The Case changed after it was read.",
                httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return EditModeError(exception, httpContext);
        }
    }

    private static bool TryReadEditAuthority(
        HttpContext httpContext,
        out EditAuthority? editAuthority,
        out IResult? error)
    {
        editAuthority = null;
        error = null;
        var clientId = httpContext.Request.Headers[ClientIdHeader].ToString();
        var generationValue = httpContext.Request.Headers[EditGenerationHeader].ToString();
        if (string.IsNullOrWhiteSpace(clientId)
            || !long.TryParse(
                generationValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var generation)
            || generation < 0)
        {
            error = Error(
                StatusCodes.Status428PreconditionRequired,
                "precondition_required",
                $"{ClientIdHeader} and a valid {EditGenerationHeader} are required.",
                httpContext);
            return false;
        }

        editAuthority = new EditAuthority(clientId, generation);
        return true;
    }

    private static bool TryReadExpectedVersion(
        StringValues ifMatch,
        string caseId,
        out int version)
    {
        version = 0;
        if (ifMatch.Count != 1)
        {
            return false;
        }

        var value = ifMatch[0];
        var prefix = $"\"case:{caseId}:v";
        if (value is null
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || !value.EndsWith('"'))
        {
            return false;
        }

        return int.TryParse(
            value.AsSpan(prefix.Length, value.Length - prefix.Length - 1),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out version)
            && version > 0;
    }

    private static void SetEntityTag(HttpResponse response, PlannerCase plannerCase)
    {
        response.Headers.ETag = $"\"case:{plannerCase.CaseId}:v{plannerCase.Version}\"";
    }

    private static IResult ValidationError(
        CaseValidationException exception,
        HttpContext httpContext)
    {
        return Results.Json(
            new
            {
                error = new
                {
                    code = "validation_failed",
                    message = "Case validation failed.",
                    correlationId = httpContext.TraceIdentifier,
                    details = exception.Issues.Select(issue => new
                    {
                        field = issue.Field,
                        code = issue.Code,
                        message = issue.Message
                    })
                }
            },
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    private static IResult CaseOperationValidationError(
        IEnumerable<object> details,
        HttpContext httpContext)
    {
        return Results.Json(
            new
            {
                error = new
                {
                    code = "validation_failed",
                    message = "Case Operation validation failed.",
                    correlationId = httpContext.TraceIdentifier,
                    details
                }
            },
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    private static IResult RequestError(CaseRequestException exception, HttpContext httpContext)
    {
        return Results.Json(
            new
            {
                error = new
                {
                    code = "invalid_request",
                    message = "The Case patch is invalid.",
                    correlationId = httpContext.TraceIdentifier,
                    details = exception.Issues.Select(issue => new
                    {
                        field = string.IsNullOrEmpty(issue.Field) ? null : issue.Field,
                        code = issue.Code,
                        message = issue.Message
                    })
                }
            },
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static IResult EditModeError(
        EditModeMutationException exception,
        HttpContext httpContext)
    {
        return Error(
            StatusCodes.Status409Conflict,
            exception.Code,
            exception.Message,
            httpContext);
    }

    private static IResult Error(
        int status,
        string code,
        string message,
        HttpContext httpContext)
    {
        return Results.Json(
            new
            {
                error = new
                {
                    code,
                    message,
                    correlationId = httpContext.TraceIdentifier,
                    details = Array.Empty<object>()
                }
            },
            statusCode: status);
    }
}
