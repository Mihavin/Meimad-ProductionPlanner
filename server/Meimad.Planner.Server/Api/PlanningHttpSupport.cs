using System.Globalization;
using Meimad.Planner.Server.Api.Accounts;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.EditMode;
using Microsoft.Extensions.Primitives;

namespace Meimad.Planner.Server.Api;

internal static class PlanningHttpSupport
{
    private const string ClientIdHeader = "X-Meimad-Client-Id";

    /// <summary>The signed-in account of the request, if any.</summary>
    internal static SignedInUser? CurrentUser(HttpContext httpContext) =>
        httpContext.Items.TryGetValue(SignInMiddleware.UserItem, out var value) ? value as SignedInUser : null;

    /// <summary>
    /// The signed-in user when the account holds <paramref name="permission"/> (any signed-in account
    /// when it is null); otherwise 401 <c>sign_in_required</c> or 403 <c>permission_required</c>
    /// naming the permission.
    /// </summary>
    internal static bool TryAuthorize(
        HttpContext httpContext,
        string? permission,
        out SignedInUser? user,
        out IResult? error)
    {
        user = CurrentUser(httpContext);
        error = null;
        if (user is null)
        {
            error = Error(StatusCodes.Status401Unauthorized, "sign_in_required", "Sign in to change data.", httpContext);
            return false;
        }
        if (permission is not null && !user.Has(permission))
        {
            var name = Permissions.All.FirstOrDefault(definition => definition.Code == permission)?.Name ?? permission;
            error = Error(
                StatusCodes.Status403Forbidden,
                "permission_required",
                $"Your account may not do this: it needs the permission \"{name}\". Ask an administrator to give it to one of your user types.",
                httpContext,
                [new { permission, name }]);
            return false;
        }
        return true;
    }

    /// <summary>The change authority of a signed-in user who holds <paramref name="permission"/>.</summary>
    internal static bool TryAuthorizeEdit(
        HttpContext httpContext,
        string permission,
        out EditAuthority? editAuthority,
        out IResult? error)
    {
        editAuthority = null;
        if (!TryAuthorize(httpContext, permission, out var user, out error)) return false;
        var clientId = httpContext.Request.Headers[ClientIdHeader].ToString().Trim();
        editAuthority = new EditAuthority(string.IsNullOrEmpty(clientId) ? user!.SessionId : clientId, 0, user!.UserName);
        return true;
    }

    /// <summary>The client and the signed-in user name of a change that needs <paramref name="permission"/> (null: any account).</summary>
    internal static bool TryAuthorizeIdentity(
        HttpContext httpContext,
        string? permission,
        out string? clientId,
        out string? userId,
        out IResult? error)
    {
        clientId = null;
        userId = null;
        if (!TryAuthorize(httpContext, permission, out var user, out error)) return false;
        var header = httpContext.Request.Headers[ClientIdHeader].ToString().Trim();
        clientId = string.IsNullOrEmpty(header) ? user!.SessionId : header;
        userId = user!.UserName;
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
