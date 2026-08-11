using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.EditMode;

public sealed class EditModeApiTests
{
    [Fact]
    public async Task Background_worker_transfers_without_client_polling()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            using var firstRequest = CreateRequest(HttpMethod.Post, "/api/v1/edit-mode/requests", "client-a", "user-a");
            using var firstResponse = await client.SendAsync(firstRequest);
            Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

            using var secondRequest = CreateRequest(HttpMethod.Post, "/api/v1/edit-mode/requests", "client-b", "user-b");
            using var secondResponse = await client.SendAsync(secondRequest);
            Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50);
                var database = application.Services.GetRequiredService<SqliteDatabase>();
                await using var connection = await database.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT holder_client_id FROM edit_tokens WHERE id = 1;";
                if (string.Equals(
                        await command.ExecuteScalarAsync() as string,
                        "client-b",
                        StringComparison.Ordinal))
                {
                    return;
                }
            }

            Assert.Fail("The server timeout worker did not transfer Edit Mode within the test deadline.");
        }, timeoutSeconds: 1);
    }

    [Fact]
    public async Task Simulated_clients_request_release_and_use_only_new_generation_for_changes()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var firstRequest = CreateRequest(HttpMethod.Post, "/api/v1/edit-mode/requests", "client-a", "user-a");
            using var firstResponse = await client.SendAsync(firstRequest);
            Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
            var first = await ReadJsonAsync(firstResponse);
            Assert.Equal("editor", first.RootElement.GetProperty("state").GetString());
            var generation = first.RootElement.GetProperty("generation").GetInt64();

            using var secondRequest = CreateRequest(HttpMethod.Post, "/api/v1/edit-mode/requests", "client-b", "user-b");
            using var secondResponse = await client.SendAsync(secondRequest);
            Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
            var second = await ReadJsonAsync(secondResponse);
            Assert.Equal("requestingEdit", second.RootElement.GetProperty("state").GetString());
            var requestId = second.RootElement
                .GetProperty("pendingRequest")
                .GetProperty("requestId")
                .GetString();

            using var decision = CreateRequest(
                HttpMethod.Post,
                $"/api/v1/edit-mode/requests/{requestId}/decision",
                "client-a",
                generation: generation,
                content: JsonContent.Create(new { decision = "release" }));
            using var decisionResponse = await client.SendAsync(decision);
            Assert.Equal(HttpStatusCode.OK, decisionResponse.StatusCode);
            var decisionBody = await ReadJsonAsync(decisionResponse);
            Assert.Equal("client-b", decisionBody.RootElement.GetProperty("holder").GetProperty("clientId").GetString());
            var newGeneration = decisionBody.RootElement.GetProperty("generation").GetInt64();

            using var staleMutation = CreateRequest(
                HttpMethod.Post,
                "/api/v1/cases",
                "client-a",
                generation: generation,
                content: JsonContent.Create(ValidCase("PN-STALE")));
            using var staleResponse = await client.SendAsync(staleMutation);
            Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
            var staleBody = await ReadJsonAsync(staleResponse);
            Assert.Equal(
                "edit_generation_stale",
                staleBody.RootElement.GetProperty("error").GetProperty("code").GetString());

            using var currentMutation = CreateRequest(
                HttpMethod.Post,
                "/api/v1/cases",
                "client-b",
                generation: newGeneration,
                content: JsonContent.Create(ValidCase("PN-CURRENT")));
            using var currentResponse = await client.SendAsync(currentMutation);
            Assert.Equal(HttpStatusCode.Created, currentResponse.StatusCode);
        });
    }

    [Fact]
    public async Task Edit_routes_require_client_identity_and_valid_decision()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var missingIdentity = await client.GetAsync("/api/v1/edit-mode");
            Assert.Equal((HttpStatusCode)428, missingIdentity.StatusCode);

            using var acquire = CreateRequest(HttpMethod.Post, "/api/v1/edit-mode/requests", "client-a", "user-a");
            using var acquireResponse = await client.SendAsync(acquire);
            var acquired = await ReadJsonAsync(acquireResponse);
            var generation = acquired.RootElement.GetProperty("generation").GetInt64();

            using var request = CreateRequest(HttpMethod.Post, "/api/v1/edit-mode/requests", "client-b", "user-b");
            using var requestResponse = await client.SendAsync(request);
            var pending = await ReadJsonAsync(requestResponse);
            var requestId = pending.RootElement.GetProperty("pendingRequest").GetProperty("requestId").GetString();

            using var invalidDecision = CreateRequest(
                HttpMethod.Post,
                $"/api/v1/edit-mode/requests/{requestId}/decision",
                "client-a",
                generation: generation,
                content: JsonContent.Create(new { decision = "take" }));
            using var invalidResponse = await client.SendAsync(invalidDecision);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidResponse.StatusCode);
        });
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string clientId,
        string? userId = null,
        long? generation = null,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-Meimad-Client-Id", clientId);
        if (userId is not null)
        {
            request.Headers.Add("X-Meimad-User-Id", userId);
        }

        if (generation is not null)
        {
            request.Headers.Add("X-Meimad-Edit-Generation", generation.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return request;
    }

    private static object ValidCase(string partNumber) => new
    {
        partNumber,
        name = "Edit Mode API Case",
        workingFolderPath = Path.Combine(Path.GetTempPath(), partNumber)
    };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task RunWithServerAsync(
        Func<WebApplication, HttpClient, Task> test,
        int timeoutSeconds = 30)
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.EditMode.Api.Tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directoryPath, "api-test.db");
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5099",
                $"--EditMode:TransferTimeoutSeconds={timeoutSeconds}",
                $"--Database:Path={databasePath}"
            ],
            webHost => webHost.UseTestServer());

        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            await test(application, client);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
