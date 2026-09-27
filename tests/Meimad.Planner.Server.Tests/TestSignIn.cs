using Meimad.Planner.Server.Api.Accounts;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests;

/// <summary>
/// The test server with every request that carries no <c>Authorization</c> header signed in as an
/// administrator, so the API tests written before accounts keep exercising their endpoints. The
/// user name is the <c>X-Meimad-User-Id</c> header, else the user the test seeded as the former
/// Edit Mode holder (<c>edit_tokens.holder_user_id</c>), else <c>test-admin</c>. Account and
/// permission tests use the plain <see cref="WebHostBuilderExtensions.UseTestServer(IWebHostBuilder)"/>.
/// </summary>
internal static class TestSignIn
{
    internal const string DefaultUserName = "test-admin";
    private const string PermissionsHeader = "X-Test-Permissions";

    internal static IWebHostBuilder UseSignedInTestServer(this IWebHostBuilder builder) =>
        builder.UseTestServer().ConfigureServices(services =>
            services.AddTransient<IStartupFilter, SignInAsAdministratorFilter>());

    /// <summary>Until disposed, the client's requests come from a user who holds only <paramref name="permissions"/>.</summary>
    internal static IDisposable SignedInWithOnly(this HttpClient client, params string[] permissions)
    {
        client.DefaultRequestHeaders.Remove(PermissionsHeader);
        client.DefaultRequestHeaders.TryAddWithoutValidation(PermissionsHeader, "-," + string.Join(',', permissions));
        return new Restore(() => client.DefaultRequestHeaders.Remove(PermissionsHeader));
    }

    /// <summary>Until disposed, the client's requests carry a session token the Server does not know.</summary>
    internal static IDisposable SignedOut(this HttpClient client)
    {
        var previous = client.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-session");
        return new Restore(() => client.DefaultRequestHeaders.Authorization = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private sealed class SignInAsAdministratorFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, following) =>
            {
                if (string.IsNullOrEmpty(context.Request.Headers.Authorization.ToString()))
                {
                    var userName = await UserNameAsync(context);
                    var clientId = context.Request.Headers["X-Meimad-Client-Id"].ToString().Trim();
                    var restricted = context.Request.Headers[PermissionsHeader].ToString();
                    var permissions = restricted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(Permissions.IsKnown).ToHashSet(StringComparer.Ordinal);
                    context.Items[SignInMiddleware.UserItem] = new SignedInUser(
                        "test-user-" + userName, userName, userName, restricted.Length == 0,
                        permissions, false,
                        string.IsNullOrEmpty(clientId) ? "test-session" : clientId);
                }
                await following(context);
            });
            next(application);
        };

        private static async Task<string> UserNameAsync(HttpContext context)
        {
            var header = context.Request.Headers["X-Meimad-User-Id"].ToString().Trim();
            if (!string.IsNullOrEmpty(header)) return header;
            try
            {
                var database = context.RequestServices.GetRequiredService<SqliteDatabase>();
                await using var connection = await database.OpenConnectionAsync(context.RequestAborted);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT holder_user_id FROM edit_tokens WHERE id = 1;";
                if (await command.ExecuteScalarAsync(context.RequestAborted) is string holder
                    && !string.IsNullOrWhiteSpace(holder))
                {
                    return holder;
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // Before the migrations ran there is no holder to borrow.
            }
            return DefaultUserName;
        }
    }
}
