using Meimad.Planner.Server.Application.Accounts;

namespace Meimad.Planner.Server.Api.Accounts;

/// <summary>
/// Every Windows-client API call needs a signed-in account (owner decision 2026-09-27): the
/// <c>Authorization: Bearer</c> session token names the user, whose permissions the endpoints check.
/// Devices keep their own access: the E-Ink tablets by TabletID, the read-only TV dashboard, the
/// client installer for updates, and the Kitaron setup page, which the Server serves only to itself.
/// The upgrade script on the Server PC may also read the Machines without an account.
/// </summary>
internal sealed class SignInMiddleware(RequestDelegate next)
{
    internal const string UserItem = "meimad.signed-in-user";

    private static readonly string[] OpenPrefixes =
    [
        "/api/v1/auth/",
        "/api/v1/client-installer",
        "/api/tablet/",
        "/api/tablets/",
        "/api/v1/eink/tablets/",
        "/api/v1/tv-dashboard",
        "/api/v1/kitaron/connection",
        "/api/v1/kitaron/mapping",
        "/api/v1/kitaron/sync"
    ];

    // Reads the Server upgrade script makes on the Server PC itself to compare the CNC verification
    // gate before and after an installation; it runs without an account.
    private static readonly string[] LocalReadPrefixes =
    [
        "/api/v1/machines"
    ];

    public async Task InvokeAsync(HttpContext context, AccountService accounts)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (!context.Items.ContainsKey(UserItem) && BearerToken(context) is { } token
            && await accounts.ResolveAsync(token, context.RequestAborted) is { } user)
        {
            context.Items[UserItem] = user;
        }

        if (OpenPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || (HttpMethods.IsGet(context.Request.Method)
                && Kitaron.KitaronConnectionEndpoints.IsLocalRequest(context)
                && LocalReadPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            await next(context);
            return;
        }

        if (context.Items[UserItem] is not SignedInUser signedIn)
        {
            await PlanningHttpSupport.Error(StatusCodes.Status401Unauthorized, "sign_in_required",
                    "Sign in to the Meimad Planner. Your session may have ended after 12 hours without use.", context)
                .ExecuteAsync(context);
            return;
        }

        if (signedIn.MustChangePassword)
        {
            await PlanningHttpSupport.Error(StatusCodes.Status403Forbidden, "password_change_required",
                    "Choose a new password before you continue; an administrator set a temporary one.", context)
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    internal static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header.Length > 7
            ? header[7..].Trim()
            : null;
    }
}
