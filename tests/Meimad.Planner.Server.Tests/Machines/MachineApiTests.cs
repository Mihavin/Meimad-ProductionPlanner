using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Machines;

public sealed class MachineApiTests
{
    [Fact]
    public async Task Machine_catalog_and_assignment_commands_work_over_http()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedCalendarAndOperationsAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);

            var machineId = await CreateMachineAsync(client, "M-API-1", "mill", ["probe"]);
            using var listResponse = await client.GetAsync("/api/v1/machines");
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            using var listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
            Assert.Single(listDocument.RootElement.GetProperty("items").EnumerateArray());

            using var assignA = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-a/assignment",
                new { machineId, backlogPosition = 0 });
            Assert.Equal(HttpStatusCode.Created, assignA.StatusCode);
            using var assignB = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-b/assignment",
                new { machineId, backlogPosition = 1 });
            Assert.Equal(HttpStatusCode.Created, assignB.StatusCode);

            using var moveB = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-b/assignment",
                new { machineId, backlogPosition = 0 });
            Assert.Equal(HttpStatusCode.OK, moveB.StatusCode);

            using var backlogResponse = await client.GetAsync($"/api/v1/machines/{machineId}/backlog");
            Assert.Equal(HttpStatusCode.OK, backlogResponse.StatusCode);
            using var backlogDocument = JsonDocument.Parse(
                await backlogResponse.Content.ReadAsStringAsync());
            var items = backlogDocument.RootElement.GetProperty("items");
            Assert.Equal("op-b", items[0].GetProperty("assignment").GetProperty("batchOperationId").GetString());
            Assert.Equal("op-a", items[1].GetProperty("assignment").GetProperty("batchOperationId").GetString());

            using var boardResponse = await client.GetAsync("/api/v1/planning-board");
            Assert.Equal(HttpStatusCode.OK, boardResponse.StatusCode);
            using var boardDocument = JsonDocument.Parse(
                await boardResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                "unavailable",
                boardDocument.RootElement.GetProperty("conflictCalculationStatus").GetString());
            Assert.Empty(boardDocument.RootElement.GetProperty("conflicts").EnumerateArray());
            Assert.Equal(
                "op-laser",
                boardDocument.RootElement.GetProperty("pool")[0]
                    .GetProperty("batchOperationId").GetString());
            var machineBacklog = boardDocument.RootElement.GetProperty("machines")[0]
                .GetProperty("backlog");
            Assert.Equal("op-b", machineBacklog[0].GetProperty("batchOperationId").GetString());
            Assert.Equal("op-a", machineBacklog[1].GetProperty("batchOperationId").GetString());

            using var unassign = await client.DeleteAsync("/api/v1/batch-operations/op-b/assignment");
            Assert.Equal(HttpStatusCode.NoContent, unassign.StatusCode);

            using var machineResponse = await client.GetAsync($"/api/v1/machines/{machineId}");
            using var machineDocument = JsonDocument.Parse(
                await machineResponse.Content.ReadAsStringAsync());
            Assert.Equal(1, machineDocument.RootElement.GetProperty("backlogCount").GetInt32());
            Assert.True(machineDocument.RootElement.GetProperty("isActive").GetBoolean());
            Assert.True(machineDocument.RootElement.GetProperty("displayEnabled").GetBoolean());
        });
    }

    [Fact]
    public async Task Assignment_api_rejects_incompatible_machine_without_mutating_backlog()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedCalendarAndOperationsAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var machineId = await CreateMachineAsync(client, "M-MILL", "mill", []);

            using var response = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-laser/assignment",
                new { machineId, backlogPosition = 0 });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            using var backlog = await client.GetAsync($"/api/v1/machines/{machineId}/backlog");
            using var document = JsonDocument.Parse(await backlog.Content.ReadAsStringAsync());
            Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
        });
    }

    [Fact]
    public async Task Machine_picture_path_is_stored_as_text_and_served_through_api()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedCalendarAndOperationsAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var picturePath = Path.Combine(
                Path.GetTempPath(),
                $"meimad-machine-{Guid.NewGuid():N}.png");
            var pictureBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            await File.WriteAllBytesAsync(picturePath, pictureBytes);
            try
            {
                using var create = await client.PostAsJsonAsync(
                    "/api/v1/machines",
                    new
                    {
                        number = "M-PICTURE",
                        name = "Picture Machine",
                        processType = "mill",
                        axisType = "3-axis",
                        capabilities = new[] { "probe" },
                        workingCalendarId = "calendar-1",
                        isActive = true,
                        displayEnabled = true,
                        picturePath
                    });
                Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
                var machineId = created.RootElement.GetProperty("machineId").GetString()!;
                Assert.Equal(picturePath, created.RootElement.GetProperty("picturePath").GetString());

                using var picture = await client.GetAsync($"/api/v1/machines/{machineId}/picture");
                Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
                Assert.Equal("image/png", picture.Content.Headers.ContentType?.MediaType);
                Assert.Equal(pictureBytes, await picture.Content.ReadAsByteArrayAsync());

                var database = application.Services.GetRequiredService<SqliteDatabase>();
                await using var connection = await database.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT picture_reference, typeof(picture_reference) FROM machines WHERE id = $id;";
                command.Parameters.AddWithValue("$id", machineId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(picturePath, reader.GetString(0));
                Assert.Equal("text", reader.GetString(1));
            }
            finally
            {
                File.Delete(picturePath);
            }
        });
    }

    [Fact]
    public async Task Missing_machine_picture_returns_not_found_without_requiring_file_at_create_time()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedCalendarAndOperationsAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            using var create = await client.PostAsJsonAsync(
                "/api/v1/machines",
                new
                {
                    number = "M-MISSING-PICTURE",
                    name = "Missing Picture Machine",
                    processType = "mill",
                    axisType = (string?)null,
                    capabilities = Array.Empty<string>(),
                    workingCalendarId = "calendar-1",
                    isActive = true,
                    displayEnabled = true,
                    picturePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.png")
                });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var machineId = created.RootElement.GetProperty("machineId").GetString()!;

            using var picture = await client.GetAsync($"/api/v1/machines/{machineId}/picture");
            Assert.Equal(HttpStatusCode.NotFound, picture.StatusCode);
        });
    }

    private static async Task<string> CreateMachineAsync(
        HttpClient client,
        string number,
        string processType,
        string[] capabilities)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/machines",
            new
            {
                number,
                name = $"Machine {number}",
                processType,
                axisType = (string?)null,
                capabilities,
                workingCalendarId = "calendar-1",
                isActive = true,
                displayEnabled = true
            });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("machineId").GetString()!;
    }

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "machine-api-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static async Task SeedCalendarAndOperationsAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id)
            VALUES ('calendar-1', 'Factory', 'Asia/Jerusalem');

            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-1', 'PN-API-M', 'Machine API', 'C:\Cases\PN-API-M');

            INSERT INTO production_batches (
                id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-1', 'case-1', 'B-API-M', 'planned', 1);

            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name, required_machine_type)
            VALUES
                ('case-op-a', 'case-1', 10, 0, 'A', 'mill'),
                ('case-op-b', 'case-1', 20, 1, 'B', 'mill'),
                ('case-op-laser', 'case-1', 30, 2, 'Laser', 'laser');

            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, required_machine_type, status)
            VALUES
                ('op-a', 'batch-1', 'case-op-a', 10, 0, 'A', 'mill', 'not_started'),
                ('op-b', 'batch-1', 'case-op-b', 20, 1, 'B', 'mill', 'not_started'),
                ('op-laser', 'batch-1', 'case-op-laser', 30, 2, 'Laser', 'laser', 'not_started');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task GrantEditModeAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = 'machine-api-client',
                holder_user_id = 'machine-api-user',
                generation = 1,
                acquired_at = '2026-08-11T00:00:00Z',
                version = version + 1,
                updated_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.MachineApi.Tests",
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
