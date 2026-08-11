using System.Globalization;
using Meimad.Planner.Server.Application.EditMode;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api;

internal static class PlanningHttpSupport
{
    private const string ClientIdHeader = "X-Meimad-Client-Id";
    private const string UserIdHeader = "X-Meimad-User-Id";
    private const string EditGenerationHeader = "X-Meimad-Edit-Generation";

    internal static bool TryReadEditAuthority(
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

    internal static bool TryReadClientId(
        HttpContext httpContext,
        out string? clientId,
        out IResult? error)
    {
        clientId = httpContext.Request.Headers[ClientIdHeader].ToString().Trim();
        error = null;
        if (!string.IsNullOrEmpty(clientId))
        {
            return true;
        }

        error = Error(
            StatusCodes.Status428PreconditionRequired,
            "precondition_required",
            $"{ClientIdHeader} is required.",
            httpContext);
        return false;
    }

    internal static bool TryReadClientIdentity(
        HttpContext httpContext,
        out string? clientId,
        out string? userId,
        out IResult? error)
    {
        userId = null;
        if (!TryReadClientId(httpContext, out clientId, out error))
        {
            return false;
        }

        userId = httpContext.Request.Headers[UserIdHeader].ToString().Trim();
        if (!string.IsNullOrEmpty(userId))
        {
            return true;
        }

        error = Error(
            StatusCodes.Status428PreconditionRequired,
            "precondition_required",
            $"{UserIdHeader} is required.",
            httpContext);
        return false;
    }

    internal static bool TryReadExpectedVersion(
        StringValues ifMatch,
        string resourceKind,
        string resourceId,
        out int version)
    {
        version = 0;
        if (ifMatch.Count != 1)
        {
            return false;
        }

        var value = ifMatch[0];
        var prefix = $"\"{resourceKind}:{resourceId}:v";
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

    internal static IResult Error(
        int status,
        string code,
        string message,
        HttpContext httpContext,
        IEnumerable<object>? details = null) => Results.Json(
        new
        {
            error = new
            {
                code,
                message,
                correlationId = httpContext.TraceIdentifier,
                details = details ?? []
            }
        },
        statusCode: status);
}
