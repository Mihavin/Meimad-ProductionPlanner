using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Accounts;

public sealed class AccountApiTests
{
    private const string AdminPassword = "admin-secret";

    [Fact]
    public async Task A_fresh_server_asks_for_the_first_administrator_and_refuses_everything_else()
    {
        await RunAsync(async client =>
        {
            Assert.False((await JsonAsync(await client.GetAsync("/api/v1/auth/state"))).GetProperty("hasAccounts").GetBoolean());

            using var cases = await client.GetAsync("/api/v1/cases");
            Assert.Equal(HttpStatusCode.Unauthorized, cases.StatusCode);
            Assert.Equal("sign_in_required", await CodeAsync(cases));
            // The read-only TV dashboard keeps working without an account.
            using var dashboard = await client.GetAsync("/api/v1/tv-dashboard");
            Assert.NotEqual(HttpStatusCode.Unauthorized, dashboard.StatusCode);

            using var tooShort = await client.PostAsJsonAsync("/api/v1/auth/first-administrator",
                new { userName = "owner", displayName = "Owner", password = "123" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, tooShort.StatusCode);

            var admin = await CreateFirstAdministratorAsync(client);
            Assert.True(admin.GetProperty("user").GetProperty("isAdministrator").GetBoolean());
            Assert.Contains(Permissions.ManageUsers,
                admin.GetProperty("user").GetProperty("permissions").EnumerateArray().Select(value => value.GetString()));

            using var second = await client.PostAsJsonAsync("/api/v1/auth/first-administrator",
                new { userName = "intruder", displayName = "Intruder", password = "intruder-secret" });
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal("accounts_exist", await CodeAsync(second));
            Assert.True((await JsonAsync(await client.GetAsync("/api/v1/auth/state"))).GetProperty("hasAccounts").GetBoolean());
        });
    }

    [Fact]
    public async Task A_new_user_changes_the_temporary_password_then_may_view_but_only_do_what_the_types_allow()
    {
        await RunAsync(async client =>
        {
            var adminToken = (await CreateFirstAdministratorAsync(client)).GetProperty("token").GetString()!;
            await CreateUserAsync(client, adminToken, "dana", "Dana QC", "user-type-qc");

            var signIn = await SignInAsync(client, "dana", "temporary-1");
            Assert.True(signIn.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
            var token = signIn.GetProperty("token").GetString()!;

            using (var blocked = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", token))
            {
                Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
                Assert.Equal("password_change_required", await CodeAsync(blocked));
            }
            using (var wrongCurrent = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/password", token,
                       new { currentPassword = "wrong", newPassword = "dana-secret" }))
                Assert.NotEqual(HttpStatusCode.NoContent, wrongCurrent.StatusCode);
            using (var changed = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/password", token,
                       new { currentPassword = "temporary-1", newPassword = "dana-secret" }))
                Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

            using (var cases = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", token))
                Assert.Equal(HttpStatusCode.OK, cases.StatusCode);
            using (var board = await SendAsync(client, HttpMethod.Get, "/api/v1/planning-board", token))
                Assert.Equal(HttpStatusCode.OK, board.StatusCode);
            using (var createCase = await SendAsync(client, HttpMethod.Post, "/api/v1/cases", token, new { partNumber = "P-1" }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, createCase.StatusCode);
                Assert.Equal("permission_required", await CodeAsync(createCase));
                Assert.Contains("Edit cases and operations", await createCase.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            }
            using (var users = await SendAsync(client, HttpMethod.Get, "/api/v1/users", token))
                Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);

            var me = await JsonAsync(await SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", token));
            Assert.Equal("Dana QC", me.GetProperty("displayName").GetString());
            Assert.Equal([Permissions.DecideQc], me.GetProperty("permissions").EnumerateArray().Select(value => value.GetString()));

            using (var signOut = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/sign-out", token))
                Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
            using (var afterSignOut = await SendAsync(client, HttpMethod.Get, "/api/v1/cases", token))
                Assert.Equal(HttpStatusCode.Unauthorized, afterSignOut.StatusCode);
        });
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_and_an_inactive_account_cannot_sign_in()
    {
        await RunAsync(async client =>
        {
            var adminToken = (await CreateFirstAdministratorAsync(client)).GetProperty("token").GetString()!;
            await CreateUserAsync(client, adminToken, "ron", "Ron", "user-type-planning");

            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var failed = await client.PostAsJsonAsync("/api/v1/auth/sign-in", new { userName = "ron", password = "nope" });
                Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            }
            using (var locked = await client.PostAsJsonAsync("/api/v1/auth/sign-in", new { userName = "ron", password = "temporary-1" }))
            {
                Assert.Equal((HttpStatusCode)423, locked.StatusCode);
                Assert.Equal("account_locked", await CodeAsync(locked));
            }

            var tal = await CreateUserAsync(client, adminToken, "tal", "Tal", "user-type-planning");
            using (var deactivate = await SendAsync(client, HttpMethod.Put, $"/api/v1/users/{tal.GetProperty("userId").GetString()}", adminToken,
                       new { displayName = "Tal", isActive = false, userTypeIds = new[] { "user-type-planning" }, expectedVersion = tal.GetProperty("version").GetInt32() }))
                Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
            using var inactive = await client.PostAsJsonAsync("/api/v1/auth/sign-in", new { userName = "tal", password = "temporary-1" });
            Assert.Equal(HttpStatusCode.Forbidden, inactive.StatusCode);
            Assert.Equal("account_inactive", await CodeAsync(inactive));
        });
    }

    [Fact]
    public async Task The_last_administrator_cannot_be_removed_and_a_reset_ends_the_users_sessions()
    {
        await RunAsync(async client =>
        {
            var admin = await CreateFirstAdministratorAsync(client);
            var adminToken = admin.GetProperty("token").GetString()!;
            var adminId = admin.GetProperty("user").GetProperty("userId").GetString()!;

            using (var demote = await SendAsync(client, HttpMethod.Put, $"/api/v1/users/{adminId}", adminToken,
                       new { displayName = "Owner", isActive = true, userTypeIds = new[] { "user-type-planning" }, expectedVersion = 1 }))
            {
                Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
                Assert.Equal("last_administrator", await CodeAsync(demote));
            }

            var user = await CreateUserAsync(client, adminToken, "moshe", "Moshe", "user-type-programmer");
            var userId = user.GetProperty("userId").GetString()!;
            var userToken = (await SignInAsync(client, "moshe", "temporary-1")).GetProperty("token").GetString()!;
            using (var reset = await SendAsync(client, HttpMethod.Post, $"/api/v1/users/{userId}/password", adminToken, new { password = "temporary-2" }))
                Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
            using (var ended = await SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", userToken))
                Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
            var again = await SignInAsync(client, "moshe", "temporary-2");
            Assert.True(again.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        });
    }

    [Fact]
    public async Task Administrators_manage_user_types_and_a_stale_edit_names_who_changed_the_type()
    {
        await RunAsync(async client =>
        {
            var adminToken = (await CreateFirstAdministratorAsync(client)).GetProperty("token").GetString()!;

            var types = (await JsonAsync(await SendAsync(client, HttpMethod.Get, "/api/v1/user-types", adminToken)))
                .GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(
                ["Administrator", "Planning", "Programmer", "QC", "Technologist", "Tool Room manager"],
                types.Select(type => type.GetProperty("name").GetString()).Order(StringComparer.Ordinal));

            using (var unknown = await SendAsync(client, HttpMethod.Post, "/api/v1/user-types", adminToken,
                       new { name = "Shift lead", permissions = new[] { "launch.rockets" } }))
                Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
            var created = await JsonAsync(await SendAsync(client, HttpMethod.Post, "/api/v1/user-types", adminToken,
                new { name = "Shift lead", description = "Runs the floor", permissions = new[] { Permissions.RunOperations, Permissions.DecideQc } }));
            var typeId = created.GetProperty("userTypeId").GetString()!;

            using (var first = await SendAsync(client, HttpMethod.Put, $"/api/v1/user-types/{typeId}", adminToken,
                       new { name = "Shift lead", permissions = new[] { Permissions.RunOperations }, expectedVersion = 1 }))
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using (var stale = await SendAsync(client, HttpMethod.Put, $"/api/v1/user-types/{typeId}", adminToken,
                       new { name = "Shift leader", permissions = new[] { Permissions.DecideQc }, expectedVersion = 1 }))
            {
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
                var error = (await JsonAsync(stale)).GetProperty("error");
                Assert.Equal("edit_conflict", error.GetProperty("code").GetString());
                var conflict = error.GetProperty("conflict");
                Assert.Equal("Owner (admin)", conflict.GetProperty("changedBy").GetString());
                Assert.NotEqual(JsonValueKind.Null, conflict.GetProperty("changedAt").ValueKind);
                Assert.False(string.IsNullOrWhiteSpace(conflict.GetProperty("advice").GetString()));
            }

            using (var fixedType = await SendAsync(client, HttpMethod.Put, "/api/v1/user-types/user-type-administrator", adminToken,
                       new { name = "Boss", permissions = Array.Empty<string>(), expectedVersion = 1 }))
                Assert.Equal(HttpStatusCode.Conflict, fixedType.StatusCode);

            await CreateUserAsync(client, adminToken, "lea", "Lea", typeId);
            using (var inUse = await SendAsync(client, HttpMethod.Delete, $"/api/v1/user-types/{typeId}?expectedVersion=2", adminToken))
            {
                Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
                Assert.Equal("user_type_in_use", await CodeAsync(inUse));
            }

            // The new type's permission reaches its users' sessions.
            var leaToken = (await SignInAsync(client, "lea", "temporary-1")).GetProperty("token").GetString()!;
            using (await SendAsync(client, HttpMethod.Post, "/api/v1/auth/password", leaToken,
                       new { currentPassword = "temporary-1", newPassword = "lea-secret" })) { }
            var me = await JsonAsync(await SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", leaToken));
            Assert.Equal([Permissions.RunOperations], me.GetProperty("permissions").EnumerateArray().Select(value => value.GetString()));
        });
    }

    [Fact]
    public async Task Only_the_Server_PC_itself_reads_Machines_without_an_account()
    {
        await RunAsync(async client =>
        {
            await CreateFirstAdministratorAsync(client);

            // The upgrade script on the Server PC compares the CNC verification gate.
            using (var local = await client.GetAsync("/api/v1/machines"))
                Assert.Equal(HttpStatusCode.OK, local.StatusCode);

            using var remote = new HttpRequestMessage(HttpMethod.Get, "/api/v1/machines");
            remote.Headers.Add(RemoteAddressHeader, "192.168.0.20");
            using (var refused = await client.SendAsync(remote))
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

            using (var localWrite = await client.PostAsJsonAsync("/api/v1/machines", new { number = "M-9" }))
                Assert.Equal(HttpStatusCode.Unauthorized, localWrite.StatusCode);
        });
    }

    private const string RemoteAddressHeader = "X-Test-Remote-Address";

    /// <summary>The test server has no network: a header stands in for a request from another PC.</summary>
    private sealed class RemoteAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, following) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteAddressHeader, out var address))
                    context.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
                await following(context);
            });
            next(application);
        };
    }

    private static async Task<JsonElement> CreateFirstAdministratorAsync(HttpClient client) =>
        await JsonAsync(await client.PostAsJsonAsync("/api/v1/auth/first-administrator",
            new { userName = "admin", displayName = "Owner", password = AdminPassword, clientId = "test-client" }));

    private static async Task<JsonElement> CreateUserAsync(
        HttpClient client, string adminToken, string userName, string displayName, string userTypeId) =>
        await JsonAsync(await SendAsync(client, HttpMethod.Post, "/api/v1/users", adminToken,
            new { userName, displayName, password = "temporary-1", userTypeIds = new[] { userTypeId } }));

    private static async Task<JsonElement> SignInAsync(HttpClient client, string userName, string password) =>
        await JsonAsync(await client.PostAsJsonAsync("/api/v1/auth/sign-in", new { userName, password, clientId = "test-client" }));

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode || (int)response.StatusCode >= 400, text);
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static async Task RunAsync(Func<HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.Accounts.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "test.db")}"],
            builder => builder.UseTestServer().ConfigureServices(services =>
                services.AddTransient<IStartupFilter, RemoteAddressFilter>()));
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            await test(client);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
