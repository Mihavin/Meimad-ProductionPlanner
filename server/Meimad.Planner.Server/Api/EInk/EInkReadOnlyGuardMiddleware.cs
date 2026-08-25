namespace Meimad.Planner.Server.Api.EInk;

internal sealed class EInkReadOnlyGuardMiddleware
{
    private readonly RequestDelegate next;

    public EInkReadOnlyGuardMiddleware(RequestDelegate next)
    {
        this.next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var bearerToken = ReadBearerToken(context.Request.Headers.Authorization.ToString());
        var isDeviceCredential = bearerToken?.StartsWith(
            "mp_eink_",
            StringComparison.Ordinal) == true;
        var isDeviceRead = HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/api/v1/eink/devices");
        var isAuthenticatedBootstrap = HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.Equals("/api/tablet/ping");
        if (isDeviceCredential && !isDeviceRead && !isAuthenticatedBootstrap)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = "device_read_only",
                    message = "E-Ink device credentials can access only assigned read-only resources and tablet bootstrap.",
                    correlationId = context.TraceIdentifier,
                    details = Array.Empty<object>()
                }
            });
            return;
        }

        await next(context);
    }

    private static string? ReadBearerToken(string authorization)
    {
        var separator = authorization.IndexOfAny([' ', '\t']);
        if (separator <= 0
            || !authorization[..separator].Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization[(separator + 1)..].Trim();
        return token.Length == 0 ? null : token;
    }
}
