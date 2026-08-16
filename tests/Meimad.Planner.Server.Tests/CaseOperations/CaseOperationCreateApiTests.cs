using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.CaseOperations;

public sealed class CaseOperationCreateApiTests
{
    [Fact]
    public async Task Stores_extended_time_profile_and_snapshots_it_into_a_batch_operation()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var caseId = await CreateCaseAsync(client);
            var operation = await CreateOperationAsync(client, caseId, new
            {
                operationNumber = 10,
                name = "Automated milling",
                requiredMachineType = "five axis",
                setupTimeSeconds = 600,
                cycleTimePerPartSeconds = 120,
                qaTimeAfterSetupSeconds = 300,
                loadUnloadTimeSeconds = 45,
                loadUnloadRequiresWorker = true,
                automaticLoading = true,
                loadUnloadEveryNParts = 5,
                dayShiftOnly = true,
                hasExternalDelay = true,
                externalDelayDescription = "Outside coating",
                externalDelayDuration = 4.0,
                externalDelayDurationUnit = "hours",
                externalDelayCalendarId = (string?)null,
                respectMasterCalendar = true,
                dependencyType = "INDEPENDENT",
                predecessorCaseOperationId = (string?)null,
                simultaneousGroupKey = (string?)null
            });
            Assert.Equal(300, operation.GetProperty("qaTimeAfterSetupSeconds").GetInt32());
            Assert.Equal(45, operation.GetProperty("loadUnloadTimeSeconds").GetInt32());
            Assert.True(operation.GetProperty("loadUnloadRequiresWorker").GetBoolean());
            Assert.True(operation.GetProperty("automaticLoading").GetBoolean());
            Assert.Equal(5, operation.GetProperty("loadUnloadEveryNParts").GetInt32());
            Assert.True(operation.GetProperty("dayShiftOnly").GetBoolean());

            var operationId = operation.GetProperty("caseOperationId").GetString()!;
            using (var delayPatch = PatchRequest(caseId, operationId, 1, new
            {
                externalDelayDescription = "Outside coating and inspection",
                externalDelayDuration = 6.5,
                externalDelayDurationUnit = "days",
                respectMasterCalendar = false
            }))
            using (var delayPatchResponse = await client.SendAsync(delayPatch))
            {
                var delayPatchBody = await delayPatchResponse.Content.ReadAsStringAsync();
                Assert.True(delayPatchResponse.StatusCode == HttpStatusCode.OK, delayPatchBody);
                using var delayPatchDocument = JsonDocument.Parse(
                    delayPatchBody);
                Assert.True(delayPatchDocument.RootElement.GetProperty("hasExternalDelay").GetBoolean());
                Assert.Equal("Outside coating and inspection", delayPatchDocument.RootElement.GetProperty("externalDelayDescription").GetString());
                Assert.Equal(6.5, delayPatchDocument.RootElement.GetProperty("externalDelayDuration").GetDouble());
                Assert.Equal("days", delayPatchDocument.RootElement.GetProperty("externalDelayDurationUnit").GetString());
                Assert.False(delayPatchDocument.RootElement.GetProperty("respectMasterCalendar").GetBoolean());
            }

            using var orderResponse = await client.PostAsJsonAsync("/api/v1/orders", new
            {
                caseId, orderNumber = "SO-TIME", quantity = 10,
                workFinishDate = "2026-09-30", status = "active", notes = (string?)null
            });
            var order = JsonDocument.Parse(await orderResponse.Content.ReadAsStringAsync());
            using var batchResponse = await client.PostAsJsonAsync("/api/v1/batches", new
            {
                caseId, batchNumber = "B-TIME", status = "waiting", plannedQuantity = 10,
                allocations = new[] { new { allocationType = "order", orderId = order.RootElement.GetProperty("orderId").GetString(), quantity = 10 } }
            });
            var batch = JsonDocument.Parse(await batchResponse.Content.ReadAsStringAsync());
            using var snapshotResponse = await client.GetAsync($"/api/v1/batches/{batch.RootElement.GetProperty("batchId").GetString()}/operations");
            var snapshot = JsonDocument.Parse(await snapshotResponse.Content.ReadAsStringAsync());
            var item = Assert.Single(snapshot.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(300, item.GetProperty("qaTimeAfterSetupSeconds").GetInt32());
            Assert.Equal(45, item.GetProperty("loadUnloadTimeSeconds").GetInt32());
            Assert.Equal(5, item.GetProperty("loadUnloadEveryNParts").GetInt32());
            Assert.True(item.GetProperty("dayShiftOnly").GetBoolean());
            Assert.True(item.GetProperty("hasExternalDelay").GetBoolean());
            Assert.Equal("Outside coating and inspection", item.GetProperty("externalDelayDescription").GetString());
            Assert.Equal(6.5, item.GetProperty("externalDelayDuration").GetDouble());
            Assert.Equal("days", item.GetProperty("externalDelayDurationUnit").GetString());
            Assert.False(item.GetProperty("respectMasterCalendar").GetBoolean());

            using var boardResponse = await client.GetAsync("/api/v1/planning-board");
            var board = JsonDocument.Parse(await boardResponse.Content.ReadAsStringAsync());
            var card = Assert.Single(board.RootElement.GetProperty("pool").EnumerateArray());
            Assert.Equal(2190, card.GetProperty("estimatedTimeSeconds").GetInt64());
        });
    }

    [Fact]
    public async Task Creates_ordered_route_and_does_not_retrofit_existing_batch_snapshot()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var caseId = await CreateCaseAsync(client);

            var first = await CreateOperationAsync(client, caseId, new
            {
                operationNumber = 10,
                name = "Saw",
                requiredMachineType = "saw",
                setupTimeSeconds = 60,
                cycleTimePerPartSeconds = 30,
                dependencyType = "INDEPENDENT",
                predecessorCaseOperationId = (string?)null,
                simultaneousGroupKey = (string?)null
            });
            Assert.Equal(0, first.GetProperty("routePosition").GetInt32());
            var firstId = first.GetProperty("caseOperationId").GetString()!;

            using var orderResponse = await client.PostAsJsonAsync("/api/v1/orders", new
            {
                caseId,
                orderNumber = "SO-1",
                quantity = 5,
                workFinishDate = "2026-09-30",
                status = "active",
                notes = (string?)null
            });
            using var orderDocument = JsonDocument.Parse(
                await orderResponse.Content.ReadAsStringAsync());
            var orderId = orderDocument.RootElement.GetProperty("orderId").GetString()!;
            using var batchResponse = await client.PostAsJsonAsync("/api/v1/batches", new
            {
                caseId,
                batchNumber = "B-1",
                status = "waiting",
                plannedQuantity = 5,
                allocations = new[]
                {
                    new { allocationType = "order", orderId, quantity = 5 }
                }
            });
            using var batchDocument = JsonDocument.Parse(
                await batchResponse.Content.ReadAsStringAsync());
            var batchId = batchDocument.RootElement.GetProperty("batchId").GetString()!;

            var second = await CreateOperationAsync(client, caseId, new
            {
                operationNumber = 20,
                name = "Finish mill",
                requiredMachineType = "fiveAxisMill",
                setupTimeSeconds = 120,
                cycleTimePerPartSeconds = 45,
                dependencyType = "SEQUENTIAL",
                predecessorCaseOperationId = firstId,
                simultaneousGroupKey = (string?)null
            });
            Assert.Equal(1, second.GetProperty("routePosition").GetInt32());
            Assert.Equal(firstId, second.GetProperty("predecessorCaseOperationId").GetString());

            using var patchRequest = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/api/v1/cases/{caseId}/operations/{firstId}")
            {
                Content = JsonContent.Create(new
                {
                    name = "Saw updated",
                    setupTimeSeconds = 90
                })
            };
            patchRequest.Headers.TryAddWithoutValidation(
                "If-Match",
                $"\"case-operation:{firstId}:v1\"");
            using var patchResponse = await client.SendAsync(patchRequest);
            Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
            Assert.Equal(
                $"\"case-operation:{firstId}:v2\"",
                patchResponse.Headers.ETag?.ToString());
            using var patchDocument = JsonDocument.Parse(
                await patchResponse.Content.ReadAsStringAsync());
            Assert.Equal("Saw updated", patchDocument.RootElement.GetProperty("name").GetString());
            Assert.Equal(0, patchDocument.RootElement.GetProperty("routePosition").GetInt32());
            Assert.Equal(2, patchDocument.RootElement.GetProperty("version").GetInt32());

            using var routeResponse = await client.GetAsync($"/api/v1/cases/{caseId}/operations");
            using var routeDocument = JsonDocument.Parse(
                await routeResponse.Content.ReadAsStringAsync());
            Assert.Equal(2, routeDocument.RootElement.GetProperty("items").GetArrayLength());

            using var snapshotResponse = await client.GetAsync($"/api/v1/batches/{batchId}/operations");
            using var snapshotDocument = JsonDocument.Parse(
                await snapshotResponse.Content.ReadAsStringAsync());
            var snapshotted = Assert.Single(
                snapshotDocument.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("Saw", snapshotted.GetProperty("name").GetString());
            Assert.Equal(60, snapshotted.GetProperty("setupTimeSeconds").GetInt32());

            using var caseResponse = await client.GetAsync($"/api/v1/cases/{caseId}");
            using var caseDocument = JsonDocument.Parse(
                await caseResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                210,
                caseDocument.RootElement.GetProperty("currentSetupTimeSeconds").GetInt32());
            Assert.Equal(
                75,
                caseDocument.RootElement.GetProperty("currentCycleTimePerPartSeconds").GetInt32());
        });
    }

    [Fact]
    public async Task Operation_patch_requires_current_version_and_valid_complete_graph()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var caseId = await CreateCaseAsync(client);
            var first = await CreateOperationAsync(client, caseId, new
            {
                operationNumber = 10,
                name = "Saw",
                setupTimeSeconds = 60,
                cycleTimePerPartSeconds = 30,
                dependencyType = "INDEPENDENT"
            });
            var firstId = first.GetProperty("caseOperationId").GetString()!;
            var second = await CreateOperationAsync(client, caseId, new
            {
                operationNumber = 20,
                name = "Mill",
                setupTimeSeconds = 120,
                cycleTimePerPartSeconds = 45,
                dependencyType = "SEQUENTIAL",
                predecessorCaseOperationId = firstId
            });
            var secondId = second.GetProperty("caseOperationId").GetString()!;

            using (var duplicateRequest = PatchRequest(
                       caseId,
                       secondId,
                       1,
                       new { operationNumber = 10 }))
            using (var duplicateResponse = await client.SendAsync(duplicateRequest))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicateResponse.StatusCode);
                Assert.Contains(
                    "duplicate_operation_number",
                    await duplicateResponse.Content.ReadAsStringAsync());
            }

            using (var staleRequest = PatchRequest(
                       caseId,
                       secondId,
                       99,
                       new { name = "Stale" }))
            using (var staleResponse = await client.SendAsync(staleRequest))
            {
                Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
            }

            using (var routePositionRequest = PatchRequest(
                       caseId,
                       secondId,
                       1,
                       new { routePosition = 0 }))
            using (var routePositionResponse = await client.SendAsync(routePositionRequest))
            {
                Assert.Equal(HttpStatusCode.BadRequest, routePositionResponse.StatusCode);
                Assert.Contains(
                    "unknown_field",
                    await routePositionResponse.Content.ReadAsStringAsync());
            }

            using var routeResponse = await client.GetAsync($"/api/v1/cases/{caseId}/operations");
            using var routeDocument = JsonDocument.Parse(
                await routeResponse.Content.ReadAsStringAsync());
            var persisted = routeDocument.RootElement.GetProperty("items")
                .EnumerateArray()
                .Single(value => value.GetProperty("caseOperationId").GetString() == secondId);
            Assert.Equal(20, persisted.GetProperty("operationNumber").GetInt32());
            Assert.Equal("Mill", persisted.GetProperty("name").GetString());
            Assert.Equal(1, persisted.GetProperty("version").GetInt32());
        });
    }

    [Fact]
    public async Task Rejects_invalid_reference_and_duplicate_number_without_partial_insert()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var caseId = await CreateCaseAsync(client);
            await CreateOperationAsync(client, caseId, new
            {
                operationNumber = 10,
                name = "Saw",
                dependencyType = "INDEPENDENT"
            });

            using var invalidReference = await client.PostAsJsonAsync(
                $"/api/v1/cases/{caseId}/operations",
                new
                {
                    operationNumber = 20,
                    name = "Invalid predecessor",
                    dependencyType = "SEQUENTIAL",
                    predecessorCaseOperationId = "not-in-this-case"
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidReference.StatusCode);
            Assert.Contains("invalid_reference", await invalidReference.Content.ReadAsStringAsync());

            using var duplicate = await client.PostAsJsonAsync(
                $"/api/v1/cases/{caseId}/operations",
                new
                {
                    operationNumber = 10,
                    name = "Duplicate number",
                    dependencyType = "INDEPENDENT"
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
            Assert.Contains("duplicate_operation_number", await duplicate.Content.ReadAsStringAsync());

            using var routeResponse = await client.GetAsync($"/api/v1/cases/{caseId}/operations");
            using var routeDocument = JsonDocument.Parse(await routeResponse.Content.ReadAsStringAsync());
            Assert.Single(routeDocument.RootElement.GetProperty("items").EnumerateArray());
        });
    }

    [Fact]
    public async Task Requires_active_edit_generation()
    {
        await RunWithServerAsync(async (_, client) =>
        {
            using var response = await client.PostAsJsonAsync(
                "/api/v1/cases/missing/operations",
                new { operationNumber = 10, name = "Saw", dependencyType = "INDEPENDENT" });
            Assert.Equal((HttpStatusCode)428, response.StatusCode);
        });
    }

    private static async Task<JsonElement> CreateOperationAsync(
        HttpClient client,
        string caseId,
        object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/cases/{caseId}/operations",
            body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static HttpRequestMessage PatchRequest(
        string caseId,
        string operationId,
        int version,
        object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/cases/{caseId}/operations/{operationId}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation(
            "If-Match",
            $"\"case-operation:{operationId}:v{version}\"");
        return request;
    }

    private static async Task<string> CreateCaseAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/cases", new
        {
            partNumber = "PN-OP-1",
            name = "Operation API Case",
            workingFolderPath = Path.Combine(Path.GetTempPath(), "PN-OP-1")
        });
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("caseId").GetString()!;
    }

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "operation-api-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task GrantEditModeAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = 'operation-api-client',
                holder_user_id = 'operation-api-user',
                generation = 1,
                acquired_at = '2026-08-11T00:00:00Z',
                version = version + 1,
                updated_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunWithServerAsync(
        Func<WebApplication, HttpClient, Task> test)
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.CaseOperation.Api.Tests",
            Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5099",
                $"--Database:Path={Path.Combine(directoryPath, "api-test.db")}"
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
