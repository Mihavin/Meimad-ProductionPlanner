using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Orders;

public sealed class OrderLifecycleApiTests
{
    [Fact]
    public async Task Operation_transitions_derive_order_status_across_allocation_shapes_and_guard_edits()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services.GetRequiredService<SqliteDatabase>());
            AddEditHeaders(client);

            Assert.Equal("in_progress", await ExecuteAsync(client, "shared-op-1", "start"));
            Assert.Equal("in_production", await ReadStatusAsync(client, "order-multi"));
            Assert.Equal("in_production", await ReadStatusAsync(client, "order-shared"));
            Assert.Equal(
                ("cancelled", "\"order:order-cancelled:v1\""),
                await ReadOrderAsync(client, "order-cancelled"));
            Assert.Equal("suspended", await ExecuteAsync(client, "shared-op-1", "suspend"));
            Assert.Equal("in_production", await ReadStatusAsync(client, "order-multi"));
            Assert.Equal("in_production", await ReadStatusAsync(client, "order-shared"));
            Assert.Equal("in_progress", await ExecuteAsync(client, "shared-op-1", "start"));

            Assert.Equal("completed", await ExecuteAsync(client, "shared-op-1", "finish"));
            Assert.Equal("in_production", await ReadStatusAsync(client, "order-shared"));
            Assert.Equal("in_progress", await ExecuteAsync(client, "shared-op-2", "start"));
            Assert.Equal("completed", await ExecuteAsync(client, "shared-op-2", "finish"));
            Assert.Equal("complete", await ReadStatusAsync(client, "order-shared"));
            Assert.Equal("in_production", await ReadStatusAsync(client, "order-multi"));
            Assert.Equal("cancelled", await ReadStatusAsync(client, "order-cancelled"));

            var cancelled = await ReadOrderAsync(client, "order-cancelled");
            using var resume = PatchOrder(
                "order-cancelled",
                cancelled.EntityTag,
                new { status = "complete" });
            using var resumed = await client.SendAsync(resume);
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
            using var resumedJson = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync());
            Assert.Equal("complete", resumedJson.RootElement.GetProperty("status").GetString());

            Assert.Equal("in_progress", await ExecuteAsync(client, "single-op", "start"));
            Assert.Equal("in_production", await ReadStatusAsync(client, "order-single"));
            Assert.Equal("completed", await ExecuteAsync(client, "single-op", "finish"));
            Assert.Equal("complete", await ReadStatusAsync(client, "order-single"));

            Assert.Equal("in_progress", await ExecuteAsync(client, "partial-op", "start"));
            Assert.Equal("completed", await ExecuteAsync(client, "partial-op", "finish"));
            Assert.Equal("in_production", await ReadStatusAsync(client, "order-partial"));

            Assert.Equal("in_progress", await ExecuteAsync(client, "second-op", "start"));
            Assert.Equal("completed", await ExecuteAsync(client, "second-op", "finish"));
            Assert.Equal("complete", await ReadStatusAsync(client, "order-multi"));

            var current = await ReadOrderAsync(client, "order-multi");
            using var increase = PatchOrder("order-multi", current.EntityTag, new { quantity = 12 });
            using var increased = await client.SendAsync(increase);
            Assert.Equal(HttpStatusCode.OK, increased.StatusCode);
            using var increasedJson = JsonDocument.Parse(await increased.Content.ReadAsStringAsync());
            Assert.Equal("in_production", increasedJson.RootElement.GetProperty("status").GetString());
            var increasedTag = increased.Headers.ETag!.Tag;

            using var belowAllocation = PatchOrder("order-multi", increasedTag, new { quantity = 9 });
            using var rejectedQuantity = await client.SendAsync(belowAllocation);
            Assert.Equal(HttpStatusCode.Conflict, rejectedQuantity.StatusCode);
            Assert.Equal("order_quantity_below_allocated", await ErrorCodeAsync(rejectedQuantity));

            using var manualStatus = PatchOrder("order-multi", increasedTag, new { status = "active" });
            using var rejectedStatus = await client.SendAsync(manualStatus);
            Assert.Equal(HttpStatusCode.Conflict, rejectedStatus.StatusCode);
            Assert.Equal("order_status_derived", await ErrorCodeAsync(rejectedStatus));

            using var restore = PatchOrder("order-multi", increasedTag, new { quantity = 10 });
            using var restored = await client.SendAsync(restore);
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            using var restoredJson = JsonDocument.Parse(await restored.Content.ReadAsStringAsync());
            Assert.Equal("complete", restoredJson.RootElement.GetProperty("status").GetString());
        });
    }

    [Fact]
    public async Task Manual_production_status_is_rejected_for_new_and_unallocated_orders()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedAsync(application.Services.GetRequiredService<SqliteDatabase>());
            AddEditHeaders(client);

            using var create = await client.PostAsJsonAsync("/api/v1/orders", new
            {
                caseId = "order-case",
                orderNumber = "SO-MANUAL",
                quantity = 1,
                workFinishDate = "2026-09-01",
                status = "in_production",
                notes = (string?)null
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
            Assert.Equal("order_status_server_owned", await ErrorCodeAsync(create));

            var current = await ReadOrderAsync(client, "order-unallocated");
            using var patch = PatchOrder("order-unallocated", current.EntityTag, new { status = "complete" });
            using var response = await client.SendAsync(patch);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("order_status_server_owned", await ErrorCodeAsync(response));
        });
    }

    private static async Task<(string Status, string EntityTag)> ReadOrderAsync(HttpClient client, string orderId)
    {
        using var response = await client.GetAsync($"/api/v1/orders/{orderId}");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (document.RootElement.GetProperty("status").GetString()!, response.Headers.ETag!.Tag);
    }

    private static async Task<string> ReadStatusAsync(HttpClient client, string orderId) =>
        (await ReadOrderAsync(client, orderId)).Status;

    private static async Task<string> ExecuteAsync(HttpClient client, string operationId, string action)
    {
        using var response = action == "suspend"
            ? await client.PostAsJsonAsync($"/api/v1/batch-operations/{operationId}/{action}",
                new { reasonType = "other", comment = "Lifecycle test pause" })
            : await client.PostAsync($"/api/v1/batch-operations/{operationId}/{action}", null);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("status").GetString()!;
    }

    private static HttpRequestMessage PatchOrder(string orderId, string entityTag, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/orders/{orderId}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        return request;
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static async Task SeedAsync(SqliteDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id)
            VALUES ('order-calendar', 'Order day', 'UTC');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES
                ('machine-shared', 'M-1', 'Shared', 'mill', 'order-calendar', 'active', 1),
                ('machine-second', 'M-2', 'Second', 'mill', 'order-calendar', 'active', 1),
                ('machine-single', 'M-3', 'Single', 'mill', 'order-calendar', 'active', 1),
                ('machine-partial', 'M-4', 'Partial', 'mill', 'order-calendar', 'active', 1);
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('order-case', 'PN-ORDER-LIFE', 'Lifecycle case', 'C:\Cases\PN-ORDER-LIFE');
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name)
            VALUES
                ('case-shared-1', 'order-case', 10, 0, 'Shared one'),
                ('case-shared-2', 'order-case', 20, 1, 'Shared two'),
                ('case-second', 'order-case', 30, 2, 'Second batch'),
                ('case-single', 'order-case', 40, 3, 'Single batch'),
                ('case-partial', 'order-case', 50, 4, 'Partial batch');
            INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status)
            VALUES
                ('order-multi', 'order-case', 'SO-MULTI', 10, '2026-09-01', 'active'),
                ('order-shared', 'order-case', 'SO-SHARED', 5, '2026-09-01', 'active'),
                ('order-single', 'order-case', 'SO-SINGLE', 3, '2026-09-01', 'active'),
                ('order-partial', 'order-case', 'SO-PARTIAL', 10, '2026-09-01', 'active'),
                ('order-cancelled', 'order-case', 'SO-CANCELLED', 2, '2026-09-01', 'cancelled'),
                ('order-unallocated', 'order-case', 'SO-UNALLOCATED', 1, '2026-09-01', 'active');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
            VALUES
                ('batch-shared', 'order-case', 'B-SHARED', 'waiting', 12),
                ('batch-second', 'order-case', 'B-SECOND', 'waiting', 5),
                ('batch-single', 'order-case', 'B-SINGLE', 'waiting', 3),
                ('batch-partial', 'order-case', 'B-PARTIAL', 'waiting', 5);
            INSERT INTO batch_allocations (id, production_batch_id, allocation_type, order_id, quantity)
            VALUES
                ('allocation-multi-shared', 'batch-shared', 'order', 'order-multi', 5),
                ('allocation-order-shared', 'batch-shared', 'order', 'order-shared', 5),
                ('allocation-order-cancelled', 'batch-shared', 'order', 'order-cancelled', 2),
                ('allocation-multi-second', 'batch-second', 'order', 'order-multi', 5),
                ('allocation-single', 'batch-single', 'order', 'order-single', 3),
                ('allocation-partial', 'batch-partial', 'order', 'order-partial', 5);
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, status)
            VALUES
                ('shared-op-1', 'batch-shared', 'case-shared-1', 10, 0, 'Shared one', 'not_started'),
                ('shared-op-2', 'batch-shared', 'case-shared-2', 20, 1, 'Shared two', 'not_started'),
                ('second-op', 'batch-second', 'case-second', 30, 0, 'Second batch', 'not_started'),
                ('single-op', 'batch-single', 'case-single', 40, 0, 'Single batch', 'not_started'),
                ('partial-op', 'batch-partial', 'case-partial', 50, 0, 'Partial batch', 'not_started');
            INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
            VALUES
                ('assignment-shared-1', 'shared-op-1', 'machine-shared', 0),
                ('assignment-shared-2', 'shared-op-2', 'machine-shared', 1),
                ('assignment-second', 'second-op', 'machine-second', 0),
                ('assignment-single', 'single-op', 'machine-single', 0),
                ('assignment-partial', 'partial-op', 'machine-partial', 0);
            UPDATE edit_tokens
            SET holder_client_id = 'order-lifecycle-client', holder_user_id = 'planner',
                generation = 1, acquired_at = '2026-08-11T00:00:00Z', version = version + 1
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "order-lifecycle-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.OrderLifecycle.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "test.db")}"],
            builder => builder.UseTestServer());
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
