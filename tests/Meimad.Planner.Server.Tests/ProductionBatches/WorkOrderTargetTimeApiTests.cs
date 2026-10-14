using System.Net;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.ProductionBatches;

public sealed class WorkOrderTargetTimeApiTests
{
    private const string Route = "/api/v1/batches/b-1/target-time";
    private const string Tag1 = "\"work-order-target-time:b-1:v1\"";
    private const string Tag2 = "\"work-order-target-time:b-1:v2\"";
    private const string Body = """{"partPriceNis":540,"materialTotalNis":1400,"notes":"  quote 2026-10  "}""";

    [Fact]
    public async Task Each_resource_of_the_route_gets_the_whole_margin_at_its_own_hourly_price()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using (var overview = await client.GetAsync(Route))
            {
                var text = await overview.Content.ReadAsStringAsync();
                Assert.True(overview.StatusCode == HttpStatusCode.OK, text);
                using var json = JsonDocument.Parse(text);
                var root = json.RootElement;
                Assert.Equal(("B-100", 10), (root.GetProperty("batchNumber").GetString(), root.GetProperty("quantity").GetInt32()));
                // Allocated 6 at 500 and 4 at 600: the quantity-weighted unit price.
                Assert.Equal(540, root.GetProperty("suggestedPartPriceNis").GetDouble());
                Assert.Equal((240d, 100d), (root.GetProperty("machineDefaultHourlyRateNis").GetDouble(), root.GetProperty("workstationDefaultHourlyRateNis").GetDouble()));
                Assert.Equal(JsonValueKind.Null, root.GetProperty("saved").ValueKind);
                var resources = root.GetProperty("resources").EnumerateArray().ToArray();
                Assert.Equal(["M-1 Mill 1", "Lathe", "Deburring"], resources.Select(resource => resource.GetProperty("name").GetString()));
                Assert.Equal(["MACHINE", "MACHINE_TYPE", "WORKSTATION_TYPE"], resources.Select(resource => resource.GetProperty("kind").GetString()));
                Assert.Equal([300d, 240d, 100d], resources.Select(resource => resource.GetProperty("hourlyRateNis").GetDouble()));
                Assert.Equal([false, true, true], resources.Select(resource => resource.GetProperty("defaultRate").GetBoolean()));
            }

            using (var calculated = await SendAsync(client, HttpMethod.Post, Route + "/calculate", """{"partPriceNis":540,"materialTotalNis":1400}"""))
            {
                var text = await calculated.Content.ReadAsStringAsync();
                Assert.True(calculated.StatusCode == HttpStatusCode.OK, text);
                using var json = JsonDocument.Parse(text);
                var root = json.RootElement;
                Assert.Equal(4000, root.GetProperty("marginNis").GetDouble());
                var resources = root.GetProperty("resources").EnumerateArray().ToArray();
                // margin ÷ hourly price; per part: × 60 ÷ quantity; planned: setup + cycle × quantity (+ station times).
                Assert.Equal([13.333, 16.667, 40], resources.Select(resource => resource.GetProperty("targetHours").GetDouble()));
                Assert.Equal([80, 100, 240], resources.Select(resource => resource.GetProperty("targetMinutesPerPart").GetDouble()));
                Assert.Equal([2.667, 1.5, 0.333], resources.Select(resource => resource.GetProperty("plannedHours").GetDouble()));
                Assert.Equal([10, 20, 30], resources.Select(resource => resource.GetProperty("operationNumbers")[0].GetInt32()));
            }
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM work_order_target_times;"));

            using (var created = await SendAsync(client, HttpMethod.Put, Route, Body, ifNoneMatch: "*"))
            {
                var text = await created.Content.ReadAsStringAsync();
                Assert.True(created.StatusCode == HttpStatusCode.Created, text);
                Assert.Equal(Tag1, created.Headers.ETag?.Tag);
                using var json = JsonDocument.Parse(text);
                Assert.Equal("quote 2026-10", json.RootElement.GetProperty("notes").GetString());
                Assert.Equal(4000, json.RootElement.GetProperty("calculation").GetProperty("marginNis").GetDouble());
                Assert.Equal("planner-user", json.RootElement.GetProperty("updatedBy").GetString());
            }

            // A changed hourly price changes the next calculation, not the saved snapshot.
            await ExecuteAsync(application.Services, "UPDATE machines SET hourly_rate_nis = 400 WHERE id = 'machine-1';");
            using (var overview = await client.GetAsync(Route))
            {
                using var json = JsonDocument.Parse(await overview.Content.ReadAsStringAsync());
                Assert.Equal(Tag1, overview.Headers.ETag?.Tag);
                Assert.Equal(400, json.RootElement.GetProperty("resources")[0].GetProperty("hourlyRateNis").GetDouble());
                Assert.Equal(13.333, json.RootElement.GetProperty("saved").GetProperty("calculation").GetProperty("resources")[0].GetProperty("targetHours").GetDouble());
            }

            using (var replaced = await SendAsync(client, HttpMethod.Put, Route, """{"partPriceNis":500,"materialTotalNis":6000}""", ifMatch: Tag1))
            {
                var text = await replaced.Content.ReadAsStringAsync();
                Assert.True(replaced.StatusCode == HttpStatusCode.OK, text);
                using var json = JsonDocument.Parse(text);
                var calculation = json.RootElement.GetProperty("calculation");
                // No margin left: the target times are negative, the work cannot be paid for.
                Assert.Equal(-1000, calculation.GetProperty("marginNis").GetDouble());
                Assert.Equal(-2.5, calculation.GetProperty("resources")[0].GetProperty("targetHours").GetDouble());
                Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("notes").ValueKind);
            }

            using (var deleted = await SendAsync(client, HttpMethod.Delete, Route, null, ifMatch: Tag2))
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Equal(0L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM work_order_target_times;"));
            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'work_order_target_time_saved' AND before_data_json IS NOT NULL;"));
            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'work_order_target_time_removed';"));
        });
    }

    [Fact]
    public async Task Preconditions_permissions_and_validation_are_enforced()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using (var noHeader = await SendAsync(client, HttpMethod.Put, Route, Body))
                await AssertErrorAsync(noHeader, (HttpStatusCode)428, "precondition_required");
            using (var missing = await SendAsync(client, HttpMethod.Put, Route, Body, ifMatch: Tag1))
                await AssertErrorAsync(missing, HttpStatusCode.PreconditionFailed, "resource_version_stale");
            using (var created = await SendAsync(client, HttpMethod.Put, Route, Body, ifNoneMatch: "*"))
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using (var duplicate = await SendAsync(client, HttpMethod.Put, Route, Body, ifNoneMatch: "*", user: "other-planner"))
            {
                var error = await AssertErrorAsync(duplicate, HttpStatusCode.PreconditionFailed, "resource_version_stale");
                Assert.Equal("Work Order target time", error.GetProperty("conflict").GetProperty("resource").GetString());
                Assert.Equal("planner-user", error.GetProperty("conflict").GetProperty("changedBy").GetString());
            }
            using (var stale = await SendAsync(client, HttpMethod.Put, Route, Body, ifMatch: Tag2))
                await AssertErrorAsync(stale, HttpStatusCode.PreconditionFailed, "resource_version_stale");

            using (client.SignedInWithOnly(Permissions.ManageSetup))
            {
                using (var put = await SendAsync(client, HttpMethod.Put, Route, Body, ifMatch: Tag1))
                    await AssertErrorAsync(put, HttpStatusCode.Forbidden, "permission_required");
                using (var delete = await SendAsync(client, HttpMethod.Delete, Route, null, ifMatch: Tag1))
                    await AssertErrorAsync(delete, HttpStatusCode.Forbidden, "permission_required");
                // Reading and calculating are open to every signed-in user.
                using (var read = await client.GetAsync(Route)) Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                using (var calculate = await SendAsync(client, HttpMethod.Post, Route + "/calculate", Body))
                    Assert.Equal(HttpStatusCode.OK, calculate.StatusCode);
            }
            using (client.SignedOut())
            using (var anonymous = await client.GetAsync(Route))
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            foreach (var (body, field) in new[]
                     {
                         ("""{"materialTotalNis":10}""", "partPriceNis"),
                         ("""{"partPriceNis":-1,"materialTotalNis":10}""", "partPriceNis"),
                         ("""{"partPriceNis":10}""", "materialTotalNis"),
                         ("""{"partPriceNis":10,"materialTotalNis":-5}""", "materialTotalNis"),
                     })
            {
                using var invalid = await SendAsync(client, HttpMethod.Put, Route, body, ifMatch: Tag1);
                var error = await AssertErrorAsync(invalid, HttpStatusCode.UnprocessableEntity, "validation_failed");
                Assert.Equal(field, error.GetProperty("details")[0].GetProperty("field").GetString());
            }
            using (var unknown = await SendAsync(client, HttpMethod.Put, Route, """{"partPriceNis":1,"materialTotalNis":1,"rate":3}""", ifMatch: Tag1))
                Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            using (var other = await client.GetAsync("/api/v1/batches/no-such-batch/target-time"))
                await AssertErrorAsync(other, HttpStatusCode.NotFound, "resource_not_found");
            Assert.Equal(1L, await ScalarAsync(application.Services, "SELECT version FROM work_order_target_times;"));
        });
    }

    [Fact]
    public async Task Machines_and_workstation_types_carry_an_optional_hourly_price_with_defaults()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using (var machine = await client.GetAsync("/api/v1/machines/machine-2"))
            {
                using var json = JsonDocument.Parse(await machine.Content.ReadAsStringAsync());
                Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("hourlyRateNis").ValueKind);
                Assert.Equal(240, json.RootElement.GetProperty("effectiveHourlyRateNis").GetDouble());
            }
            using (var invalid = await SendAsync(client, HttpMethod.Patch, "/api/v1/machines/machine-2", """{"hourlyRateNis":-1}""", ifMatch: "\"machine:machine-2:v1\""))
                Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            using (var patched = await SendAsync(client, HttpMethod.Patch, "/api/v1/machines/machine-2", """{"hourlyRateNis":275.5}""", ifMatch: "\"machine:machine-2:v1\""))
            {
                var text = await patched.Content.ReadAsStringAsync();
                Assert.True(patched.StatusCode == HttpStatusCode.OK, text);
                using var json = JsonDocument.Parse(text);
                Assert.Equal((275.5, 275.5), (json.RootElement.GetProperty("hourlyRateNis").GetDouble(), json.RootElement.GetProperty("effectiveHourlyRateNis").GetDouble()));
            }

            using (var type = await SendAsync(client, HttpMethod.Post, "/api/v1/resources/workstation-types", """{"name":"Washing","hourlyRateNis":120}"""))
            {
                var text = await type.Content.ReadAsStringAsync();
                Assert.True(type.IsSuccessStatusCode, text);
                using var json = JsonDocument.Parse(text);
                Assert.Equal((120d, 120d), (json.RootElement.GetProperty("hourlyRateNis").GetDouble(), json.RootElement.GetProperty("effectiveHourlyRateNis").GetDouble()));
            }
            using (var invalid = await SendAsync(client, HttpMethod.Post, "/api/v1/resources/workstation-types", """{"name":"Bad","hourlyRateNis":-3}"""))
                Assert.False(invalid.IsSuccessStatusCode);
            using (var update = await SendAsync(client, HttpMethod.Patch, "/api/v1/resources/workstation-types/type-deburr",
                       """{"name":"Deburring","description":null,"propertySchemaJson":"{}","isActive":true,"expectedVersion":1,"hourlyRateNis":80}"""))
                Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());
            using (var types = await client.GetAsync("/api/v1/resources/workstation-types"))
            {
                using var json = JsonDocument.Parse(await types.Content.ReadAsStringAsync());
                var deburr = json.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetString() == "type-deburr");
                Assert.Equal(80, deburr.GetProperty("hourlyRateNis").GetDouble());
            }
            using var overview = await client.GetAsync(Route);
            using var result = JsonDocument.Parse(await overview.Content.ReadAsStringAsync());
            Assert.Equal(80, result.RootElement.GetProperty("resources")[2].GetProperty("hourlyRateNis").GetDouble());
        });
    }

    private static async Task SeedAsync(IServiceProvider services) => await ExecuteAsync(services, """
        INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-1', 'Day', 'UTC');
        INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active, hourly_rate_nis) VALUES
            ('machine-1', 'M-1', 'Mill 1', 'Mill 3x', 'calendar-1', 'active', 1, 300),
            ('machine-2', 'M-2', 'Mill 2', 'Mill 3x', 'calendar-1', 'active', 1, NULL);
        INSERT INTO cases (id, part_number, name, working_folder_path) VALUES ('case-1', 'P-1', 'Bracket', 'C:\Cases\P-1');
        INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status, price) VALUES
            ('o-1', 'case-1', 'SO-1', 6, '2026-11-01', 'active', 500),
            ('o-2', 'case-1', 'SO-2', 4, '2026-11-01', 'active', 600);
        INSERT INTO case_operations (id, case_id, operation_number, route_position, name) VALUES
            ('cop-1', 'case-1', 10, 0, 'Mill'), ('cop-2', 'case-1', 20, 1, 'Turn'),
            ('cop-3', 'case-1', 30, 2, 'Deburr'), ('cop-4', 'case-1', 40, 3, 'Old mill');
        INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity) VALUES ('b-1', 'case-1', 'B-100', 'planned', 10);
        INSERT INTO batch_allocations (id, production_batch_id, allocation_type, order_id, quantity) VALUES
            ('al-1', 'b-1', 'order', 'o-1', 6), ('al-2', 'b-1', 'order', 'o-2', 4);
        INSERT INTO batch_operations (id, production_batch_id, source_case_operation_id, operation_number, route_position, name, required_machine_type, setup_seconds, cycle_seconds, status) VALUES
            ('bop-1', 'b-1', 'cop-1', 10, 0, 'Mill', 'Mill 3x', 3600, 600, 'not_started'),
            ('bop-2', 'b-1', 'cop-2', 20, 1, 'Turn', 'Lathe', 1800, 360, 'not_started'),
            ('bop-3', 'b-1', 'cop-3', 30, 2, 'Deburr', NULL, NULL, NULL, 'not_started'),
            ('bop-4', 'b-1', 'cop-4', 40, 3, 'Old mill', 'Mill 3x', 60, 60, 'cancelled');
        INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position) VALUES
            ('as-1', 'bop-1', 'machine-1', 0), ('as-4', 'bop-4', 'machine-2', 0);
        INSERT INTO workstation_types (id, name, created_at, updated_at) VALUES ('type-deburr', 'Deburring', '2026-10-14', '2026-10-14');
        INSERT INTO operation_resource_requirements (id, case_operation_id, sequence_position, resource_class, workstation_type_id,
            estimated_duration_seconds, duration_per_unit_seconds, direction, created_at, updated_at)
        VALUES ('req-1', 'cop-3', 0, 'WORKSTATION', 'type-deburr', 600, 60, 'FORWARD', '2026-10-14', '2026-10-14');
        """);

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? body, string? ifMatch = null, string? ifNoneMatch = null, string user = "planner-user")
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        request.Headers.TryAddWithoutValidation("X-Meimad-User-Id", user);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        return error.Clone();
    }

    private static async Task ExecuteAsync(IServiceProvider services, string sql)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(IServiceProvider services, string sql)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), "MeimadPlanner.TargetTime.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5102", $"--Database:Path={Path.Combine(path, "test.db")}"],
            webHost => webHost.UseSignedInTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "target-time-client");
            await test(application, client);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
