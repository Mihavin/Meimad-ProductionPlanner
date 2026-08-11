using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.ProductionBatches;
using Meimad.Planner.Server.Domain.ProductionBatches;

namespace Meimad.Planner.Server.Api.ProductionBatches;

internal static class ProductionBatchEndpoints
{
    private const string ClientIdHeader = "X-Meimad-Client-Id";
    private const string EditGenerationHeader = "X-Meimad-Edit-Generation";

    internal static void MapProductionBatchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var batches = endpoints.MapGroup("/api/v1/batches");
        batches.MapPost(string.Empty, CreateAsync);
        batches.MapGet(string.Empty, ListAsync);
        batches.MapGet("/{batchId}", GetByIdAsync);
        batches.MapGet("/{batchId}/operations", GetOperationsAsync);
    }

    private static async Task<IResult> ListAsync(
        string? caseId,
        HttpContext httpContext,
        ProductionBatchService service,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(caseId))
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "The caseId query parameter is required.",
                httpContext);
        }

        var batches = await service.ListByCaseAsync(caseId.Trim(), cancellationToken);
        return Results.Ok(new ProductionBatchListResponse(
            batches.Select(ProductionBatchResponse.FromDomain).ToArray(),
            null));
    }

    private static async Task<IResult> CreateAsync(
        CreateProductionBatchRequest request,
        HttpContext httpContext,
        ProductionBatchService service,
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
                $"/api/v1/batches/{created.BatchId}",
                ProductionBatchResponse.FromDomain(created));
        }
        catch (ProductionBatchValidationException exception)
        {
            return ValidationError(exception, httpContext);
        }
        catch (ProductionBatchCaseNotFoundException exception)
        {
            return Error(
                StatusCodes.Status404NotFound,
                "resource_not_found",
                exception.Message,
                httpContext);
        }
        catch (ProductionBatchNumberConflictException exception)
        {
            return Error(
                StatusCodes.Status409Conflict,
                "batch_number_conflict",
                exception.Message,
                httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return Error(
                StatusCodes.Status409Conflict,
                exception.Code,
                exception.Message,
                httpContext);
        }
    }

    private static async Task<IResult> GetByIdAsync(
        string batchId,
        HttpContext httpContext,
        ProductionBatchService service,
        CancellationToken cancellationToken)
    {
        var batch = await service.GetByIdAsync(batchId, cancellationToken);
        if (batch is null)
        {
            return NotFound(httpContext);
        }

        SetEntityTag(httpContext.Response, batch);
        return Results.Ok(ProductionBatchResponse.FromDomain(batch));
    }

    private static async Task<IResult> GetOperationsAsync(
        string batchId,
        HttpContext httpContext,
        ProductionBatchService service,
        CancellationToken cancellationToken)
    {
        var batch = await service.GetByIdAsync(batchId, cancellationToken);
        if (batch is null)
        {
            return NotFound(httpContext);
        }

        return Results.Ok(new BatchOperationListResponse(
            batch.Operations.Select(BatchOperationResponse.FromDomain).ToArray(),
            null));
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

    private static void SetEntityTag(HttpResponse response, ProductionBatch batch)
    {
        response.Headers.ETag = $"\"batch:{batch.BatchId}:v{batch.Version}\"";
    }

    private static IResult ValidationError(
        ProductionBatchValidationException exception,
        HttpContext httpContext) => Results.Json(
        new
        {
            error = new
            {
                code = "validation_failed",
                message = "Production Batch validation failed.",
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

    private static IResult NotFound(HttpContext httpContext) => Error(
        StatusCodes.Status404NotFound,
        "resource_not_found",
        "The requested Production Batch was not found.",
        httpContext);

    private static IResult Error(
        int status,
        string code,
        string message,
        HttpContext httpContext) => Results.Json(
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
