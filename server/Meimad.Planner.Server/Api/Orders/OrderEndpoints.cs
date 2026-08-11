using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Orders;
using Meimad.Planner.Server.Domain.Orders;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api.Orders;

internal static class OrderEndpoints
{
    private const string ClientIdHeader = "X-Meimad-Client-Id";
    private const string EditGenerationHeader = "X-Meimad-Edit-Generation";

    internal static void MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var orders = endpoints.MapGroup("/api/v1/orders");
        orders.MapPost(string.Empty, CreateAsync);
        orders.MapGet(string.Empty, ListAsync);
        orders.MapGet("/{orderId}", GetByIdAsync);
        orders.MapPatch("/{orderId}", UpdateAsync);
    }

    private static async Task<IResult> CreateAsync(
        CreateOrderRequest request,
        HttpContext httpContext,
        OrderService service,
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
                $"/api/v1/orders/{created.OrderId}",
                OrderResponse.FromDomain(created));
        }
        catch (OrderValidationException exception)
        {
            return ValidationError(exception, httpContext);
        }
        catch (OrderCaseNotFoundException exception)
        {
            return Error(
                StatusCodes.Status404NotFound,
                "resource_not_found",
                exception.Message,
                httpContext);
        }
        catch (EditModeMutationException exception)
        {
            return EditModeError(exception, httpContext);
        }
    }

    private static async Task<IResult> ListAsync(
        string? caseId,
        HttpContext httpContext,
        OrderService service,
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

        var orders = await service.ListByCaseAsync(caseId.Trim(), cancellationToken);
        return Results.Ok(new OrderListResponse(
            orders.Select(OrderResponse.FromDomain).ToArray(),
            null));
    }

    private static async Task<IResult> GetByIdAsync(
        string orderId,
        HttpContext httpContext,
        OrderService service,
        CancellationToken cancellationToken)
    {
        var order = await service.GetByIdAsync(orderId, cancellationToken);
        if (order is null)
        {
            return Error(
                StatusCodes.Status404NotFound,
                "resource_not_found",
                "The requested Order was not found.",
                httpContext);
        }

        SetEntityTag(httpContext.Response, order);
        return Results.Ok(OrderResponse.FromDomain(order));
    }

    private static async Task<IResult> UpdateAsync(
        string orderId,
        PatchOrderRequest request,
        HttpContext httpContext,
        OrderService service,
        CancellationToken cancellationToken)
    {
        if (!TryReadEditAuthority(httpContext, out var editAuthority, out var accessError))
        {
            return accessError!;
        }

        if (!TryReadExpectedVersion(
                httpContext.Request.Headers.IfMatch,
                orderId,
                out var expectedVersion))
        {
            var missing = StringValues.IsNullOrEmpty(httpContext.Request.Headers.IfMatch);
            return Error(
                missing
                    ? StatusCodes.Status428PreconditionRequired
                    : StatusCodes.Status412PreconditionFailed,
                missing ? "precondition_required" : "resource_version_stale",
                "A matching Order If-Match header is required.",
                httpContext);
        }

        try
        {
            var updated = await service.UpdateAsync(
                orderId,
                expectedVersion,
                request.ToCommand(),
                editAuthority!,
                cancellationToken);
            SetEntityTag(httpContext.Response, updated);
            return Results.Ok(OrderResponse.FromDomain(updated));
        }
        catch (OrderRequestException exception)
        {
            return RequestError(exception, httpContext);
        }
        catch (OrderValidationException exception)
        {
            return ValidationError(exception, httpContext);
        }
        catch (OrderNotFoundException)
        {
            return Error(
                StatusCodes.Status404NotFound,
                "resource_not_found",
                "The requested Order was not found.",
                httpContext);
        }
        catch (OrderVersionConflictException)
        {
            return Error(
                StatusCodes.Status412PreconditionFailed,
                "resource_version_stale",
                "The Order changed after it was read.",
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
        string orderId,
        out int version)
    {
        version = 0;
        if (ifMatch.Count != 1)
        {
            return false;
        }

        var value = ifMatch[0];
        var prefix = $"\"order:{orderId}:v";
        return value is not null
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && value.EndsWith('"')
            && int.TryParse(
                value.AsSpan(prefix.Length, value.Length - prefix.Length - 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out version)
            && version > 0;
    }

    private static void SetEntityTag(HttpResponse response, PlannerOrder order)
    {
        response.Headers.ETag = $"\"order:{order.OrderId}:v{order.Version}\"";
    }

    private static IResult ValidationError(
        OrderValidationException exception,
        HttpContext httpContext) => Results.Json(
        new
        {
            error = new
            {
                code = "validation_failed",
                message = "Order validation failed.",
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

    private static IResult RequestError(
        OrderRequestException exception,
        HttpContext httpContext) => Results.Json(
        new
        {
            error = new
            {
                code = "invalid_request",
                message = "The Order patch is invalid.",
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

    private static IResult EditModeError(
        EditModeMutationException exception,
        HttpContext httpContext) => Error(
        StatusCodes.Status409Conflict,
        exception.Code,
        exception.Message,
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
