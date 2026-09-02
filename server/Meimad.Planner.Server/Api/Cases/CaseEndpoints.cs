using System.Globalization;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.Cases;
using Meimad.Planner.Server.Domain.CaseOperations;
using Microsoft.Extensions.Primitives;
using Meimad.Planner.Server.Application.Kitaron;

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
        cases.MapPatch("/{caseId}/operations/{operationId}", UpdateOperationAsync);
        cases.MapGet("/{caseId}/components", ListComponentsAsync);
        cases.MapGet("/{caseId}/where-used", ListWhereUsedAsync);
        cases.MapGet("/{caseId}/component-demand", PreviewComponentDemandAsync);
        cases.MapGet("/{caseId}/derived-orders", ListDerivedOrdersAsync);
        cases.MapPost("/{caseId}/components", CreateComponentAsync);
        cases.MapPatch("/{caseId}/components/{componentId}", UpdateComponentAsync);
        cases.MapDelete("/{caseId}/components/{componentId}", DeactivateComponentAsync);
        cases.MapGet("/{caseId}/preview", GetPreviewAsync);
        cases.MapPatch("/{caseId}", UpdateAsync);
    }

    private static async Task<IResult> ListAsync(
        string? search,
        string? customer,
        bool? isActive,
        string? sort,
        HttpContext httpContext,
        CaseService service,
        CancellationToken cancellationToken)
    {
        if (!TryParseSort(sort, out var sortOrder))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_case_sort", "sort must be partNumber, closestOrderDeliveryDate, or customerName.", httpContext);
        }
        var items = await service.ListAsync(search, customer, isActive, sortOrder, cancellationToken);
        return Results.Ok(new CaseListResponse(
            items.Select(CaseResponse.FromDomain).ToArray(),
            null));
    }

    private static bool TryParseSort(string? value, out CaseSortOrder sortOrder)
    {
        switch (value)
        {
            case null:
            case "":
            case "partNumber":
                sortOrder = CaseSortOrder.PartNumber;
                return true;
            case "closestOrderDeliveryDate":
                sortOrder = CaseSortOrder.ClosestOrderDeliveryDate;
                return true;
            case "customerName":
                sortOrder = CaseSortOrder.CustomerName;
                return true;
            default:
                sortOrder = CaseSortOrder.PartNumber;
                return false;
        }
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
            SetEntityTag(httpContext.Response, created);
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
        catch (CaseParentOperationsNotAllowedException exception)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "parent_operations_forbidden", exception.Message, httpContext);
        }
        catch (CaseParentBatchesMustBeRemovedException exception)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "parent_batches_must_be_removed", exception.Message, httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return EditModeError(exception, httpContext);
        }
    }

    private static async Task<IResult> ListComponentsAsync(
        string caseId, HttpContext httpContext, CaseService caseService,
        CaseComponentService componentService, CancellationToken cancellationToken)
    {
        if (await caseService.GetByIdAsync(caseId, cancellationToken) is null)
            return Error(StatusCodes.Status404NotFound, "resource_not_found", "The requested Case was not found.", httpContext);
        var items = await componentService.ListComponentsAsync(caseId, cancellationToken);
        return Results.Ok(new CaseComponentListResponse(
            items.Select(CaseComponentResponse.FromApplication).ToArray(), null));
    }

    private static async Task<IResult> ListWhereUsedAsync(
        string caseId, HttpContext httpContext, CaseService caseService,
        CaseComponentService componentService, CancellationToken cancellationToken)
    {
        if (await caseService.GetByIdAsync(caseId, cancellationToken) is null)
            return Error(StatusCodes.Status404NotFound, "resource_not_found", "The requested Case was not found.", httpContext);
        var items = await componentService.ListWhereUsedAsync(caseId, cancellationToken);
        return Results.Ok(new CaseComponentListResponse(
            items.Select(CaseComponentResponse.FromApplication).ToArray(), null));
    }

    private static async Task<IResult> PreviewComponentDemandAsync(
        string caseId, double quantity, HttpContext httpContext,
        CaseComponentService service, CancellationToken cancellationToken)
    {
        try { return Results.Ok(await service.PreviewDemandAsync(caseId, quantity, cancellationToken)); }
        catch (CaseComponentValidationException exception) { return ComponentValidationError(exception, httpContext); }
        catch (CaseComponentNotFoundException)
        {
            return Error(StatusCodes.Status404NotFound, "resource_not_found", "The requested Case was not found.", httpContext);
        }
    }

    private static async Task<IResult> ListDerivedOrdersAsync(
        string caseId, HttpContext httpContext, DerivedCaseOrderService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await service.ListAsync(caseId, cancellationToken);
            return Results.Ok(new DerivedCaseOrderListResponse(items, null));
        }
        catch (CaseComponentNotFoundException)
        {
            return Error(StatusCodes.Status404NotFound, "resource_not_found", "The requested Case was not found.", httpContext);
        }
    }

    private static async Task<IResult> CreateComponentAsync(
        string caseId, CreateCaseComponentRequest request, HttpContext httpContext,
        CaseComponentService service, CancellationToken cancellationToken)
    {
        if (!TryReadEditAuthority(httpContext, out var authority, out var accessError)) return accessError!;
        try
        {
            var created = await service.CreateAsync(
                caseId, request.ChildCaseId ?? string.Empty, request.QuantityPerParent,
                request.SortOrder, request.Notes, authority!, cancellationToken);
            SetEntityTag(httpContext.Response, created);
            return Results.Created(
                $"/api/v1/cases/{caseId}/components/{created.CaseComponentId}",
                CaseComponentResponse.FromApplication(created));
        }
        catch (CaseComponentValidationException exception) { return ComponentValidationError(exception, httpContext); }
        catch (CaseComponentCycleException exception)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "component_cycle", exception.Message, httpContext);
        }
        catch (CaseComponentDuplicateException exception)
        {
            return Error(StatusCodes.Status409Conflict, "component_duplicate", exception.Message, httpContext);
        }
        catch (CaseParentOperationsNotAllowedException exception)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "parent_operations_forbidden", exception.Message, httpContext);
        }
        catch (CaseComponentNotFoundException)
        {
            return Error(StatusCodes.Status404NotFound, "resource_not_found", "The parent or child Case was not found.", httpContext);
        }
        catch (EditModeMutationException exception) { return EditModeError(exception, httpContext); }
    }

    private static async Task<IResult> UpdateComponentAsync(
        string caseId, string componentId, UpdateCaseComponentRequest request, HttpContext httpContext,
        CaseComponentService service, CancellationToken cancellationToken)
    {
        if (!TryReadEditAuthority(httpContext, out var authority, out var accessError)) return accessError!;
        if (!TryReadExpectedVersion(
                httpContext.Request.Headers.IfMatch, "case-component", componentId, out var expectedVersion))
        {
            var missing = StringValues.IsNullOrEmpty(httpContext.Request.Headers.IfMatch);
            return Error(missing ? StatusCodes.Status428PreconditionRequired : StatusCodes.Status412PreconditionFailed,
                missing ? "precondition_required" : "resource_version_stale",
                "A matching Case Component If-Match header is required.", httpContext);
        }
        try
        {
            var updated = await service.UpdateAsync(
                caseId, componentId, request.QuantityPerParent, request.SortOrder,
                request.Notes, request.IsActive, expectedVersion, authority!, cancellationToken);
            SetEntityTag(httpContext.Response, updated);
            return Results.Ok(CaseComponentResponse.FromApplication(updated));
        }
        catch (CaseComponentValidationException exception) { return ComponentValidationError(exception, httpContext); }
        catch (CaseComponentCycleException exception)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "component_cycle", exception.Message, httpContext);
        }
        catch (CaseParentOperationsNotAllowedException exception)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "parent_operations_forbidden", exception.Message, httpContext);
        }
        catch (CaseComponentNotFoundException)
        {
            return Error(StatusCodes.Status404NotFound, "resource_not_found", "The requested Case Component was not found.", httpContext);
        }
        catch (CaseComponentVersionConflictException)
        {
            return Error(StatusCodes.Status412PreconditionFailed, "resource_version_stale", "The Case Component changed after it was read.", httpContext);
        }
        catch (EditModeMutationException exception) { return EditModeError(exception, httpContext); }
    }

    private static async Task<IResult> DeactivateComponentAsync(
        string caseId, string componentId, HttpContext httpContext,
        CaseComponentService service, ICaseComponentRepository repository,
        CancellationToken cancellationToken)
    {
        var current = await repository.GetAsync(componentId, cancellationToken);
        if (current is null || !StringComparer.Ordinal.Equals(current.ParentCaseId, caseId))
            return Error(StatusCodes.Status404NotFound, "resource_not_found", "The requested Case Component was not found.", httpContext);
        return await UpdateComponentAsync(
            caseId, componentId,
            new UpdateCaseComponentRequest(current.QuantityPerParent, current.SortOrder, current.Notes, false),
            httpContext, service, cancellationToken);
    }

    private static async Task<IResult> UpdateOperationAsync(
        string caseId,
        string operationId,
        PatchCaseOperationRequest request,
        HttpContext httpContext,
        CaseService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadEditAuthority(httpContext, out var editAuthority, out var accessError))
        {
            return accessError!;
        }

        if (!TryReadExpectedVersion(
                httpContext.Request.Headers.IfMatch,
                "case-operation",
                operationId,
                out var expectedVersion))
        {
            var missing = StringValues.IsNullOrEmpty(httpContext.Request.Headers.IfMatch);
            return Error(
                missing
                    ? StatusCodes.Status428PreconditionRequired
                    : StatusCodes.Status412PreconditionFailed,
                missing ? "precondition_required" : "resource_version_stale",
                "A matching Case Operation If-Match header is required.",
                httpContext);
        }

        try
        {
            var updated = await service.UpdateOperationAsync(
                caseId,
                operationId,
                expectedVersion,
                request.ToCommand(),
                editAuthority!,
                cancellationToken);
            SetEntityTag(httpContext.Response, updated);
            return Results.Ok(CaseOperationResponse.FromApplication(updated));
        }
        catch (CaseRequestException exception)
        {
            return RequestError(exception, httpContext);
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
        catch (CaseOperationNotFoundException)
        {
            return Error(
                StatusCodes.Status404NotFound,
                "resource_not_found",
                "The requested Case Operation was not found.",
                httpContext);
        }
        catch (CaseOperationVersionConflictException)
        {
            return Error(
                StatusCodes.Status412PreconditionFailed,
                "resource_version_stale",
                "The Case Operation changed after it was read.",
                httpContext);
        }
        catch (CaseParentOperationsNotAllowedException exception)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, "parent_operations_forbidden", exception.Message, httpContext);
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
        ServerFilePathResolver pathResolver,
        CancellationToken cancellationToken)
    {
        var plannerCase = await service.GetByIdAsync(caseId, cancellationToken);
        var previewPath = plannerCase?.PreviewPath is null
            ? null
            : pathResolver.ResolveExistingFile(plannerCase.PreviewPath, plannerCase.WorkingFolderPath);
        if (previewPath is null)
        {
            return Error(
                StatusCodes.Status404NotFound,
                "preview_not_found",
                "No preview is available for the requested Case.",
                httpContext);
        }

        var contentType = Path.GetExtension(previewPath).ToLowerInvariant() switch
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
            previewPath,
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
        catch (KitaronManagedResourceException exception)
        {
            return Error(
                StatusCodes.Status409Conflict,
                "kitaron_managed_read_only",
                exception.Message,
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
        out int version) =>
        TryReadExpectedVersion(ifMatch, "case", caseId, out version);

    private static bool TryReadExpectedVersion(
        StringValues ifMatch,
        string resourceType,
        string resourceId,
        out int version)
    {
        version = 0;
        if (ifMatch.Count != 1)
        {
            return false;
        }

        var value = ifMatch[0];
        var prefix = $"\"{resourceType}:{resourceId}:v";
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

    private static void SetEntityTag(
        HttpResponse response,
        CaseOperationDetails operation)
    {
        response.Headers.ETag =
            $"\"case-operation:{operation.CaseOperationId}:v{operation.Version}\"";
    }

    private static void SetEntityTag(HttpResponse response, CaseComponentDetails component)
    {
        response.Headers.ETag =
            $"\"case-component:{component.CaseComponentId}:v{component.Version}\"";
    }

    private static IResult ComponentValidationError(
        CaseComponentValidationException exception, HttpContext httpContext) =>
        Results.Json(new
        {
            error = new
            {
                code = "validation_failed",
                message = "Case Component validation failed.",
                correlationId = httpContext.TraceIdentifier,
                details = new[] { new { field = exception.Field, code = "invalid_value", message = exception.Message } }
            }
        }, statusCode: StatusCodes.Status422UnprocessableEntity);

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
