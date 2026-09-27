using Meimad.Planner.Server.Application.Accounts;

namespace Meimad.Planner.Server.Api.Accounts;

/// <summary>
/// Sign-in and the user administration. <c>/api/v1/auth/*</c> works before signing in; users, user
/// types and resets need the <see cref="Permissions.ManageUsers"/> permission.
/// </summary>
internal static class AccountEndpoints
{
    internal static void MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/v1/auth");
        auth.MapGet("/state", async (AccountService accounts, CancellationToken token) =>
            Results.Ok(new { hasAccounts = await accounts.AnyAccountAsync(token) }));
        auth.MapPost("/sign-in", async (SignInRequest request, AccountService accounts, CancellationToken token) =>
            Results.Ok(SignInResponse.From(await accounts.SignInAsync(request.UserName, request.Password, request.ClientId, token))));
        auth.MapPost("/first-administrator", async (FirstAdministratorRequest request, AccountService accounts, CancellationToken token) =>
            Results.Ok(SignInResponse.From(await accounts.CreateFirstAdministratorAsync(
                request.UserName, request.DisplayName, request.Password, request.ClientId, token))));
        auth.MapPost("/sign-out", async (HttpContext context, AccountService accounts, CancellationToken token) =>
        {
            if (SignInMiddleware.BearerToken(context) is { } bearer) await accounts.SignOutAsync(bearer, token);
            return Results.NoContent();
        });
        auth.MapGet("/me", (HttpContext context) =>
            PlanningHttpSupport.CurrentUser(context) is { } user
                ? Results.Ok(SignedInUserResponse.From(user))
                : SignInRequired(context));
        auth.MapPost("/password", async (HttpContext context, ChangePasswordRequest request, AccountService accounts, CancellationToken token) =>
        {
            if (PlanningHttpSupport.CurrentUser(context) is not { } user) return SignInRequired(context);
            await accounts.ChangeOwnPasswordAsync(user, request.CurrentPassword, request.NewPassword, token);
            return Results.NoContent();
        });

        endpoints.MapGet("/api/v1/permissions", () => Results.Ok(new
        {
            items = Permissions.All.Select(permission => new { permission.Code, permission.Name, permission.Description })
        }));

        var users = endpoints.MapGroup("/api/v1/users");
        users.MapGet(string.Empty, async (HttpContext context, AccountService accounts, CancellationToken token) =>
            !PlanningHttpSupport.TryAuthorize(context, Permissions.ManageUsers, out _, out var error)
                ? error!
                : Results.Ok(new { items = (await accounts.ListUsersAsync(token)).Select(UserResponse.From) }));
        users.MapPost(string.Empty, async (HttpContext context, CreateUserRequest request, AccountService accounts, CancellationToken token) =>
            !PlanningHttpSupport.TryAuthorize(context, Permissions.ManageUsers, out var actor, out var error)
                ? error!
                : Results.Created($"/api/v1/users", UserResponse.From(await accounts.CreateUserAsync(
                    request.UserName, request.DisplayName, request.IsActive ?? true, request.UserTypeIds, request.Password, actor!, token))));
        users.MapPut("/{userId}", async (string userId, HttpContext context, UpdateUserRequest request, AccountService accounts, CancellationToken token) =>
            !PlanningHttpSupport.TryAuthorize(context, Permissions.ManageUsers, out var actor, out var error)
                ? error!
                : Results.Ok(UserResponse.From(await accounts.UpdateUserAsync(
                    userId, request.ExpectedVersion, request.DisplayName, request.IsActive, request.UserTypeIds, actor!, token))));
        users.MapPost("/{userId}/password", async (string userId, HttpContext context, ResetPasswordRequest request, AccountService accounts, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageUsers, out var actor, out var error)) return error!;
            await accounts.ResetPasswordAsync(userId, request.Password, actor!, token);
            return Results.NoContent();
        });

        var types = endpoints.MapGroup("/api/v1/user-types");
        types.MapGet(string.Empty, async (HttpContext context, AccountService accounts, CancellationToken token) =>
            !PlanningHttpSupport.TryAuthorize(context, Permissions.ManageUsers, out _, out var error)
                ? error!
                : Results.Ok(new { items = await accounts.ListTypesAsync(token) }));
        types.MapPost(string.Empty, async (HttpContext context, UserTypeRequest request, AccountService accounts, CancellationToken token) =>
            !PlanningHttpSupport.TryAuthorize(context, Permissions.ManageUsers, out var actor, out var error)
                ? error!
                : Results.Created("/api/v1/user-types", await accounts.CreateTypeAsync(request.Name, request.Description, request.Permissions, actor!, token)));
        types.MapPut("/{userTypeId}", async (string userTypeId, HttpContext context, UserTypeRequest request, AccountService accounts, CancellationToken token) =>
            !PlanningHttpSupport.TryAuthorize(context, Permissions.ManageUsers, out var actor, out var error)
                ? error!
                : Results.Ok(await accounts.UpdateTypeAsync(userTypeId, request.ExpectedVersion ?? 0, request.Name, request.Description, request.Permissions, actor!, token)));
        types.MapDelete("/{userTypeId}", async (string userTypeId, int expectedVersion, HttpContext context, AccountService accounts, CancellationToken token) =>
        {
            if (!PlanningHttpSupport.TryAuthorize(context, Permissions.ManageUsers, out _, out var error)) return error!;
            await accounts.DeleteTypeAsync(userTypeId, expectedVersion, token);
            return Results.NoContent();
        });
    }

    private static IResult SignInRequired(HttpContext context) =>
        PlanningHttpSupport.Error(StatusCodes.Status401Unauthorized, "sign_in_required", "Sign in to the Meimad Planner.", context);
}

internal sealed record SignInRequest(string? UserName, string? Password, string? ClientId);

internal sealed record FirstAdministratorRequest(string? UserName, string? DisplayName, string? Password, string? ClientId);

internal sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

internal sealed record CreateUserRequest(string? UserName, string? DisplayName, string? Password, bool? IsActive, IReadOnlyList<string>? UserTypeIds);

internal sealed record UpdateUserRequest(string? DisplayName, bool IsActive, IReadOnlyList<string>? UserTypeIds, int ExpectedVersion);

internal sealed record ResetPasswordRequest(string? Password);

internal sealed record UserTypeRequest(string? Name, string? Description, IReadOnlyList<string>? Permissions, int? ExpectedVersion);

internal sealed record SignedInUserResponse(
    string UserId, string UserName, string DisplayName, bool IsAdministrator, IReadOnlyList<string> Permissions, bool MustChangePassword)
{
    internal static SignedInUserResponse From(SignedInUser user) => new(
        user.UserId, user.UserName, user.DisplayName, user.IsAdministrator,
        user.IsAdministrator
            ? Application.Accounts.Permissions.All.Select(permission => permission.Code).ToArray()
            : user.Permissions.Order(StringComparer.Ordinal).ToArray(),
        user.MustChangePassword);
}

internal sealed record SignInResponse(string Token, DateTimeOffset ExpiresAt, SignedInUserResponse User)
{
    internal static SignInResponse From(SignInResult result) => new(result.Token, result.ExpiresAt, SignedInUserResponse.From(result.User));
}

internal sealed record UserResponse(
    string UserId, string UserName, string DisplayName, bool IsActive, bool MustChangePassword, DateTimeOffset? LastSignInAt,
    IReadOnlyList<UserTypeSummary> Types, int Version, DateTimeOffset UpdatedAt, string? UpdatedBy)
{
    internal static UserResponse From(UserAccount user) => new(
        user.UserId, user.UserName, user.DisplayName, user.IsActive, user.MustChangePassword, user.LastSignInAt, user.Types,
        user.Version, user.UpdatedAt, user.UpdatedBy);
}
