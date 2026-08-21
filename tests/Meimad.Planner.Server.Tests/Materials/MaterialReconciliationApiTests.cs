using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Application.Timeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Materials;

public sealed class MaterialReconciliationApiTests
{
    [Fact]
    public async Task Verified_receipts_and_explicit_reservations_drive_batch_operation_material_readiness()
    {
        await RunAsync(async (app, client) =>
        {
            await SeedAsync(app.Services);
            AddEditHeaders(client);
            var batch = await CreateBatchAsync(client, "B-MAT", 10);
            var batchId = batch.GetProperty("batchId").GetString()!;
            var operationId = (await ReadAsync(client, $"/api/v1/batches/{batchId}/operations"))
                .GetProperty("items")[0].GetProperty("batchOperationId").GetString()!;
            using (var assign = await client.PutAsJsonAsync(
                       $"/api/v1/batch-operations/{operationId}/assignment",
                       new { machineId = "machine-material", backlogPosition = 0 }))
                assign.EnsureSuccessStatusCode();

            var initial = await ReadAsync(client, $"/api/v1/batches/{batchId}/material");
            Assert.Equal("MISSING", initial.GetProperty("state").GetString());
            Assert.Equal(10, initial.GetProperty("shortageQuantity").GetInt32());
            using (var blockedStart = await client.PostAsync(
                       $"/api/v1/batch-operations/{operationId}/start", null))
            {
                Assert.Equal(HttpStatusCode.Conflict, blockedStart.StatusCode);
                using var error = JsonDocument.Parse(await blockedStart.Content.ReadAsStringAsync());
                Assert.Equal("material_missing",
                    error.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            var first = await CreateReceiptAsync(client, 6, "LOT-6");
            var firstId = first.GetProperty("receiptId").GetString()!;
            var partial = await ReplaceReservationsAsync(client, batchId,
                new[] { new { receiptId = firstId, quantity = 6 } });
            Assert.Equal("MISSING", partial.GetProperty("state").GetString());
            Assert.Equal(4, partial.GetProperty("shortageQuantity").GetInt32());

            var second = await CreateReceiptAsync(client, 4, "LOT-4");
            var secondId = second.GetProperty("receiptId").GetString()!;
            var availableButUnreserved = await ReadAsync(client, $"/api/v1/batches/{batchId}/material");
            Assert.Equal("UNVERIFIED", availableButUnreserved.GetProperty("state").GetString());

            var ready = await ReplaceReservationsAsync(client, batchId, new object[]
            {
                new { receiptId = firstId, quantity = 6 },
                new { receiptId = secondId, quantity = 4 }
            });
            Assert.Equal("READY", ready.GetProperty("state").GetString());
            Assert.Equal(10, ready.GetProperty("reservedQuantity").GetInt32());

            var readiness = await ReadAsync(client, $"/api/v1/batch-operations/{operationId}/readiness");
            var material = readiness.GetProperty("components").EnumerateArray()
                .Single(component => component.GetProperty("key").GetString() == "material");
            Assert.Equal("READY", material.GetProperty("state").GetString());

            using var manual = await client.PutAsJsonAsync(
                $"/api/v1/batch-operations/{operationId}/readiness-inputs",
                new
                {
                    selectedGCodeReleaseId = (string?)null,
                    materialStatus = "MISSING",
                    materialComment = "manual override",
                    toolOffsetStatus = "UNVERIFIED",
                    toolOffsetComment = (string?)null
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, manual.StatusCode);
            using var manualJson = JsonDocument.Parse(await manual.Content.ReadAsStringAsync());
            Assert.Contains(manualJson.RootElement.GetProperty("error").GetProperty("details").EnumerateArray(),
                detail => detail.GetProperty("code").GetString() == "material_reconciliation_required");

            using var started = await client.PostAsync(
                $"/api/v1/batch-operations/{operationId}/start", null);
            started.EnsureSuccessStatusCode();
        });
    }

    [Fact]
    public async Task Shortage_decisions_remain_explicit_and_receipts_cannot_be_over_reserved()
    {
        await RunAsync(async (app, client) =>
        {
            await SeedAsync(app.Services);
            AddEditHeaders(client);
            var original = await CreateBatchAsync(client, "B-ORIGINAL", 10);
            var originalId = original.GetProperty("batchId").GetString()!;
            var originalOperationId = (await ReadAsync(client, $"/api/v1/batches/{originalId}/operations"))
                .GetProperty("items")[0].GetProperty("batchOperationId").GetString()!;
            var receipt = await CreateReceiptAsync(client, 10, "LOT-SPLIT");
            var receiptId = receipt.GetProperty("receiptId").GetString()!;

            using (var reduce = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/batches/{originalId}"))
            {
                reduce.Headers.TryAddWithoutValidation("If-Match", original.GetProperty("version").GetInt32() is var version
                    ? $"\"batch:{originalId}:v{version}\"" : string.Empty);
                reduce.Content = JsonContent.Create(new
                {
                    batchNumber = "B-READY",
                    plannedQuantity = 4,
                    allocations = new[] { new { allocationType = "stock", orderId = (string?)null, quantity = 4 } }
                });
                using var response = await client.SendAsync(reduce);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var waiting = await CreateBatchAsync(client, "B-WAITING", 6);
            var waitingId = waiting.GetProperty("batchId").GetString()!;
            Assert.Equal(originalOperationId,
                (await ReadAsync(client, $"/api/v1/batches/{originalId}/operations"))
                    .GetProperty("items")[0].GetProperty("batchOperationId").GetString());

            Assert.Equal("READY", (await ReplaceReservationsAsync(client, originalId,
                new[] { new { receiptId, quantity = 4 } })).GetProperty("state").GetString());
            Assert.Equal("READY", (await ReplaceReservationsAsync(client, waitingId,
                new[] { new { receiptId, quantity = 6 } })).GetProperty("state").GetString());

            var timelineSource = app.Services.GetRequiredService<ITimelineSourceRepository>();
            var timeline = await timelineSource.ReadAsync(
                DateTimeOffset.Parse("2026-08-20T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-21T00:00:00Z"), default);
            Assert.Equal(4, timeline.Operations.Single(item => item.BatchId == originalId).PlannedQuantity);
            Assert.Equal(6, timeline.Operations.Single(item => item.BatchId == waitingId).PlannedQuantity);

            var extra = await CreateBatchAsync(client, "B-EXTRA", 1);
            var extraId = extra.GetProperty("batchId").GetString()!;
            using var over = await client.PutAsJsonAsync(
                $"/api/v1/batches/{extraId}/material/reservations",
                new { reservations = new[] { new { receiptId, quantity = 1 } } });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        });
    }

    private static async Task<JsonElement> CreateBatchAsync(HttpClient client, string number, int quantity)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/batches", new
        {
            caseId = "case-material",
            batchNumber = number,
            status = "waiting",
            plannedQuantity = quantity,
            allocations = new[] { new { allocationType = "stock", orderId = (string?)null, quantity } }
        });
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> CreateReceiptAsync(HttpClient client, int quantity, string reference)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/material-receipts", new
        {
            caseId = "case-material",
            quantity,
            receivedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            externalReference = reference,
            comment = "Physically counted locally"
        });
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> ReplaceReservationsAsync(
        HttpClient client, string batchId, object reservations)
    {
        using var response = await client.PutAsJsonAsync(
            $"/api/v1/batches/{batchId}/material/reservations", new { reservations });
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "material-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id)
            VALUES ('calendar-material', 'Material test calendar', 'UTC');
            INSERT INTO machines (
                id, number, name, machine_type, working_calendar_id, status, is_active,
                execution_mode, machine_time_factor)
            VALUES ('machine-material', 'M-MAT', 'Material test Machine', 'manual',
                    'calendar-material', 'active', 1, 'MANUAL', 1.0);
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-material', 'PN-MATERIAL', 'Material Case', 'C:\Cases\Material');
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
            VALUES ('case-material-op', 'case-material', 10, 0, 'Cut');
            UPDATE edit_tokens
            SET holder_client_id = 'material-client', holder_user_id = 'material-user',
                generation = 1, acquired_at = '2026-08-20T00:00:00Z',
                version = version + 1, updated_at = '2026-08-20T00:00:00Z'
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.Material.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "test.db")}"],
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
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
