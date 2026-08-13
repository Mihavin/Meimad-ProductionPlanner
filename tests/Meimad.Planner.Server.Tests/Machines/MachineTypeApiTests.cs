using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Machines;

public sealed class MachineTypeApiTests
{
    [Fact]
    public async Task Rename_is_blocked_when_an_unassigned_operation_requires_the_current_type_name()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await SeedCalendarAndEditModeAsync(database);
            AddEditHeaders(client);

            using var createType = await client.PostAsJsonAsync("/api/v1/machine-types", new
            {
                name = "5-axis milling",
                capabilities = Array.Empty<string>()
            });
            createType.EnsureSuccessStatusCode();
            using var typeJson = JsonDocument.Parse(await createType.Content.ReadAsStringAsync());
            var typeId = typeJson.RootElement.GetProperty("machineTypeId").GetString()!;
            var typeTag = createType.Headers.ETag!.Tag;

            await SeedRequiredOperationAsync(database, "5-axis milling");
            using var rename = PatchType(typeId, typeTag, new { name = "Five-axis milling" });
            using var response = await client.SendAsync(rename);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(
                "machine_type_name_in_use",
                error.RootElement.GetProperty("error").GetProperty("code").GetString());

            using var delete = await client.DeleteAsync($"/api/v1/machine-types/{typeId}");
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        });
    }

    [Fact]
    public async Task Machine_types_are_reusable_versioned_and_protect_linked_work()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await SeedCalendarAndEditModeAsync(database);
            AddEditHeaders(client);

            using var createType = await client.PostAsJsonAsync("/api/v1/machine-types", new
            {
                name = "5-axis milling",
                capabilities = new[] { "probe", "high-speed" }
            });
            Assert.Equal(HttpStatusCode.Created, createType.StatusCode);
            using var typeJson = JsonDocument.Parse(await createType.Content.ReadAsStringAsync());
            var typeId = typeJson.RootElement.GetProperty("machineTypeId").GetString()!;
            var typeTag = createType.Headers.ETag?.Tag;
            Assert.NotNull(typeTag);

            using var createMachine = await client.PostAsJsonAsync("/api/v1/machines", new
            {
                number = "M-TYPE",
                name = "Typed Machine",
                processType = "legacy value",
                machineTypeId = typeId,
                axisType = "5-axis",
                capabilities = Array.Empty<string>(),
                workingCalendarId = "calendar-type",
                isActive = true,
                displayEnabled = false
            });
            Assert.Equal(HttpStatusCode.Created, createMachine.StatusCode);
            using var machineJson = JsonDocument.Parse(await createMachine.Content.ReadAsStringAsync());
            var machineId = machineJson.RootElement.GetProperty("machineId").GetString()!;
            Assert.Equal(typeId, machineJson.RootElement.GetProperty("machineTypeId").GetString());
            Assert.Equal("5-axis milling", machineJson.RootElement.GetProperty("processType").GetString());

            await SeedRequiredOperationAsync(database, "probe");
            using var assign = await client.PutAsJsonAsync("/api/v1/batch-operations/typed-op/assignment", new
            {
                machineId,
                backlogPosition = 0
            });
            Assert.Equal(HttpStatusCode.Created, assign.StatusCode);

            using var incompatiblePatch = PatchType(typeId, typeTag!, new
            {
                capabilities = new[] { "high-speed" }
            });
            using var incompatible = await client.SendAsync(incompatiblePatch);
            Assert.Equal(HttpStatusCode.Conflict, incompatible.StatusCode);

            using var renamePatch = PatchType(typeId, typeTag!, new
            {
                name = "Five-axis milling",
                capabilities = new[] { "probe", "high-speed" }
            });
            using var renamed = await client.SendAsync(renamePatch);
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

            using var machine = await client.GetAsync($"/api/v1/machines/{machineId}");
            machine.EnsureSuccessStatusCode();
            using var updatedMachine = JsonDocument.Parse(await machine.Content.ReadAsStringAsync());
            Assert.Equal("Five-axis milling", updatedMachine.RootElement.GetProperty("processType").GetString());

            using var usedDelete = await client.DeleteAsync($"/api/v1/machine-types/{typeId}");
            Assert.Equal(HttpStatusCode.Conflict, usedDelete.StatusCode);

            using var createUnused = await client.PostAsJsonAsync("/api/v1/machine-types", new
            {
                name = "Conventional",
                capabilities = Array.Empty<string>()
            });
            createUnused.EnsureSuccessStatusCode();
            using var unusedJson = JsonDocument.Parse(await createUnused.Content.ReadAsStringAsync());
            var unusedId = unusedJson.RootElement.GetProperty("machineTypeId").GetString();
            using var unusedDelete = await client.DeleteAsync($"/api/v1/machine-types/{unusedId}");
            Assert.Equal(HttpStatusCode.NoContent, unusedDelete.StatusCode);

            using var list = await client.GetAsync("/api/v1/machine-types");
            list.EnsureSuccessStatusCode();
            using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
        });
    }

    private static HttpRequestMessage PatchType(string typeId, string entityTag, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/machine-types/{typeId}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        return request;
    }

    private static async Task SeedCalendarAndEditModeAsync(SqliteDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES ('calendar-type', 'Type day', 'UTC',
                    '{"weeklySchedule":{"workdays":["monday"],"shiftStartsAtLocal":"06:00","shiftEndsAtLocal":"18:00"}}');
            UPDATE edit_tokens
            SET holder_client_id = 'machine-type-client', holder_user_id = 'planner',
                generation = 1, acquired_at = '2026-08-11T00:00:00Z', version = version + 1
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedRequiredOperationAsync(
        SqliteDatabase database,
        string requiredMachineType)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('typed-case', 'PN-TYPE', 'Typed case', 'C:\Cases\PN-TYPE');
            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name, required_machine_type)
            VALUES ('typed-case-op', 'typed-case', 10, 0, 'Probe', $requiredMachineType);
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
            VALUES ('typed-batch', 'typed-case', 'B-TYPE', 'waiting', 1);
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, required_machine_type, status)
            VALUES ('typed-op', 'typed-batch', 'typed-case-op', 10, 0, 'Probe', $requiredMachineType, 'not_started');
            """;
        command.Parameters.AddWithValue("$requiredMachineType", requiredMachineType);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "machine-type-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MeimadPlanner.MachineType.Tests", Guid.NewGuid().ToString("N"));
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
