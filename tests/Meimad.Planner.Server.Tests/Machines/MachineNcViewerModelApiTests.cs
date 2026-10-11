using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Machines;

public sealed class MachineNcViewerModelApiTests
{
    private static readonly object TableModel = new
    {
        node = "table",
        path = @"\\factory\machines\VF3\table.STL",
        offsetMm = new { x = 0.5, y = -12.25, z = 100 },
        rotationDeg = new { x = 90, y = 0, z = -45.5 },
        color = "#1f2a3b",
        opacity = 0.6
    };

    [Fact]
    public async Task Machine_NC_viewer_models_round_trip_with_defaults_and_invariant_storage()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using var created = await client.PostAsJsonAsync("/api/v1/machines", MachineBody("M-3D", new object[]
            {
                TableModel,
                new { node = "base", path = @"C:\Machines\VF3\base.stl" }
            }));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = createdJson.RootElement.GetProperty("machineId").GetString()!;
            var models = createdJson.RootElement.GetProperty("ncViewerModels").EnumerateArray().ToArray();
            Assert.Equal(2, models.Length);
            Assert.Equal("table", models[0].GetProperty("node").GetString());
            Assert.Equal(@"\\factory\machines\VF3\table.STL", models[0].GetProperty("path").GetString());
            Assert.Equal(-12.25, models[0].GetProperty("offsetMm").GetProperty("y").GetDouble());
            Assert.Equal(-45.5, models[0].GetProperty("rotationDeg").GetProperty("z").GetDouble());
            Assert.Equal("#1F2A3B", models[0].GetProperty("color").GetString());
            Assert.Equal(0.6, models[0].GetProperty("opacity").GetDouble());
            // Omitted placement, color and opacity take their defaults.
            Assert.Equal(0, models[1].GetProperty("offsetMm").GetProperty("x").GetDouble());
            Assert.Equal(0, models[1].GetProperty("rotationDeg").GetProperty("z").GetDouble());
            Assert.Equal("#9AA4AE", models[1].GetProperty("color").GetString());
            Assert.Equal(1, models[1].GetProperty("opacity").GetDouble());

            using var read = await client.GetAsync($"/api/v1/machines/{id}");
            using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.Equal(2, readJson.RootElement.GetProperty("ncViewerModels").GetArrayLength());

            using var listed = await client.GetAsync("/api/v1/machines");
            using var listedJson = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            var item = listedJson.RootElement.GetProperty("items").EnumerateArray()
                .Single(machine => machine.GetProperty("machineId").GetString() == id);
            Assert.Equal("base", item.GetProperty("ncViewerModels")[1].GetProperty("node").GetString());

            // Stored as culture-invariant JSON whatever the Server's culture.
            var stored = (string)(await ScalarAsync(application.Services, $"SELECT nc_viewer_models_json FROM machines WHERE id = '{id}';"))!;
            Assert.Contains("-12.25", stored, StringComparison.Ordinal);
            Assert.Contains("\"node\":\"table\"", stored, StringComparison.Ordinal);

            // A Machine created without the field, or with null, has no models.
            using var plain = await client.PostAsJsonAsync("/api/v1/machines", MachineBody("M-PLAIN", null));
            Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
            using var plainJson = JsonDocument.Parse(await plain.Content.ReadAsStringAsync());
            Assert.Equal(0, plainJson.RootElement.GetProperty("ncViewerModels").GetArrayLength());
        });
    }

    [Fact]
    public async Task Patch_may_repeat_but_never_change_the_models_owned_by_the_definition()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using var created = await client.PostAsJsonAsync("/api/v1/machines", MachineBody("M-PATCH", new[] { TableModel }));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = createdJson.RootElement.GetProperty("machineId").GetString()!;
            var tag = created.Headers.ETag!.Tag;
            var sameList = createdJson.RootElement.GetProperty("ncViewerModels").GetRawText();

            // Absent, JSON null (screens that do not edit models) and the same list leave it unchanged.
            (tag, var absent) = await PatchAsync(client, id, tag, """{"name":"Renamed"}""");
            Assert.Equal("table", Assert.Single(absent.GetProperty("ncViewerModels").EnumerateArray()).GetProperty("node").GetString());
            (tag, var nulled) = await PatchAsync(client, id, tag, """{"name":"Renamed again","ncViewerModels":null}""");
            Assert.Equal("Renamed again", nulled.GetProperty("name").GetString());
            (tag, _) = await PatchAsync(client, id, tag, $$"""{"ncViewerModels":{{sameList}}}""");

            // A different list is the Machine Designer's to publish.
            using (var changed = Patch(id, tag, """{"ncViewerModels":[]}"""))
            using (var refused = await client.SendAsync(changed))
            {
                await AssertIssueAsync(refused, "ncViewerModels", "machine_definition_owned_field");
            }

            // The created models are the definition's meshes (legacy paths until imported) and its projection.
            var definition = await MachineSimulationTestApi.GetAsync(client, id);
            var model = definition["published"]!["nodes"]!.AsArray()
                .SelectMany(node => node!["models"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
                .Single();
            Assert.Equal(@"\\factory\machines\VF3\table.STL", model!["legacyPath"]!.GetValue<string>());
            Assert.Equal(0.6, model["opacity"]!.GetValue<double>());
            Assert.Contains(definition["issues"]!.AsArray(), issue => issue!["code"]!.GetValue<string>() == "legacy_path");
        });
    }

    public static TheoryData<string, string, string> InvalidModels => new()
    {
        { """{"node":"","path":"C:\\m\\a.stl"}""", "ncViewerModels[0].node", "required" },
        { "{\"node\":\"" + new string('n', 65) + "\",\"path\":\"C:\\\\m\\\\a.stl\"}", "ncViewerModels[0].node", "too_long" },
        { """{"node":"base","path":""}""", "ncViewerModels[0].path", "required" },
        { """{"node":"base","path":"models\\a.stl"}""", "ncViewerModels[0].path", "absolute_path_required" },
        { """{"node":"base","path":"C:\\m\\a.step"}""", "ncViewerModels[0].path", "stl_required" },
        { """{"node":"base","path":"C:\\m\\a.stl","offsetMm":{"x":100000.5}}""", "ncViewerModels[0].offsetMm.x", "out_of_range" },
        { """{"node":"base","path":"C:\\m\\a.stl","rotationDeg":{"y":-360.1}}""", "ncViewerModels[0].rotationDeg.y", "out_of_range" },
        { """{"node":"base","path":"C:\\m\\a.stl","color":"#12345"}""", "ncViewerModels[0].color", "invalid_color" },
        { """{"node":"base","path":"C:\\m\\a.stl","color":"red"}""", "ncViewerModels[0].color", "invalid_color" },
        { """{"node":"base","path":"C:\\m\\a.stl","opacity":0.04}""", "ncViewerModels[0].opacity", "out_of_range" },
        { """{"node":"base","path":"C:\\m\\a.stl","opacity":1.01}""", "ncViewerModels[0].opacity", "out_of_range" },
        { "null", "ncViewerModels[0]", "required" }
    };

    [Theory]
    [MemberData(nameof(InvalidModels))]
    public async Task Invalid_models_are_refused_on_create_and_patch(string model, string field, string code)
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using (var create = new StringContent(
                       $$"""{"number":"M-BAD","name":"Bad","processType":"mill","capabilities":[],"workingCalendarId":"calendar-1","isActive":true,"displayEnabled":true,"ncViewerModels":[{{model}}]}""",
                       Encoding.UTF8, "application/json"))
            using (var rejected = await client.PostAsync("/api/v1/machines", create))
            {
                await AssertIssueAsync(rejected, field, code);
            }

            using var created = await client.PostAsJsonAsync("/api/v1/machines", MachineBody("M-OK", null));
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = createdJson.RootElement.GetProperty("machineId").GetString()!;
            using var patch = Patch(id, created.Headers.ETag!.Tag, $$"""{"ncViewerModels":[{{model}}]}""");
            using var patchRejected = await client.SendAsync(patch);
            await AssertIssueAsync(patchRejected, field, code);
            using var unchanged = await client.GetAsync($"/api/v1/machines/{id}");
            Assert.Equal(created.Headers.ETag!.Tag, unchanged.Headers.ETag!.Tag);
        });
    }

    [Fact]
    public async Task More_than_32_models_and_non_finite_numbers_are_refused()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            var many = Enumerable.Range(0, 33).Select(index => new { node = "base", path = $@"C:\m\{index}.stl" }).ToArray();
            using var tooMany = await client.PostAsJsonAsync("/api/v1/machines", MachineBody("M-MANY", many));
            await AssertIssueAsync(tooMany, "ncViewerModels", "too_many");

            var exactly32 = many.Take(32).ToArray();
            using var accepted = await client.PostAsJsonAsync("/api/v1/machines", MachineBody("M-32", exactly32));
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

            // Numbers read from strings ("NaN", "Infinity") must still be finite.
            using var nan = new StringContent(
                """{"number":"M-NAN","name":"N","processType":"mill","capabilities":[],"workingCalendarId":"calendar-1","isActive":true,"displayEnabled":true,"ncViewerModels":[{"node":"base","path":"C:\\m\\a.stl","opacity":"NaN"}]}""",
                Encoding.UTF8, "application/json");
            using var nanResponse = await client.PostAsync("/api/v1/machines", nan);
            await AssertIssueAsync(nanResponse, "ncViewerModels[0].opacity", "not_finite");

            using var acceptedJson = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
            var id = acceptedJson.RootElement.GetProperty("machineId").GetString()!;
            using var infinite = Patch(id, accepted.Headers.ETag!.Tag,
                """{"ncViewerModels":[{"node":"base","path":"C:\\m\\a.stl","offsetMm":{"z":"-Infinity"}}]}""");
            using var infiniteResponse = await client.SendAsync(infinite);
            await AssertIssueAsync(infiniteResponse, "ncViewerModels[0].offsetMm.z", "not_finite");
        });
    }

    [Fact]
    public async Task Model_paths_under_the_network_folder_are_stored_relative_and_served_resolved()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            await ExecuteAsync(application.Services,
                """UPDATE network_folder_settings SET root_path = '\\factory\share', aliases_json = '["J:\\"]' WHERE id = 1;""");
            using var created = await client.PostAsJsonAsync("/api/v1/machines", MachineBody("M-NET", new object[]
            {
                new { node = "base", path = @"J:\machines\VF3\base.stl" },
                new { node = "table", path = @"E:\local\table.stl" }
            }));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = json.RootElement.GetProperty("machineId").GetString()!;
            var models = json.RootElement.GetProperty("ncViewerModels");
            Assert.Equal(@"\\factory\share\machines\VF3\base.stl", models[0].GetProperty("path").GetString());
            Assert.Equal(@"E:\local\table.stl", models[1].GetProperty("path").GetString());

            var stored = (string)(await ScalarAsync(application.Services, $"SELECT nc_viewer_models_json FROM machines WHERE id = '{id}';"))!;
            Assert.Contains(@"""machines\\VF3\\base.stl""", stored, StringComparison.Ordinal);

            using var read = await client.GetAsync($"/api/v1/machines/{id}");
            using var readJson = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            Assert.Equal(@"\\factory\share\machines\VF3\base.stl",
                readJson.RootElement.GetProperty("ncViewerModels")[0].GetProperty("path").GetString());
        });
    }

    private static object MachineBody(string number, object[]? models) => new
    {
        number,
        name = $"Machine {number}",
        processType = "mill",
        capabilities = Array.Empty<string>(),
        workingCalendarId = "calendar-1",
        isActive = true,
        displayEnabled = true,
        ncViewerModels = models
    };

    private static HttpRequestMessage Patch(string id, string tag, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/machines/{id}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(tag));
        return request;
    }

    private static async Task<(string Tag, JsonElement Body)> PatchAsync(HttpClient client, string id, string tag, string json)
    {
        using var request = Patch(id, tag, json);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        using var document = JsonDocument.Parse(text);
        return (response.Headers.ETag!.Tag, document.RootElement.Clone());
    }

    private static async Task AssertIssueAsync(HttpResponseMessage response, string field, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal("validation_failed", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains(json.RootElement.GetProperty("error").GetProperty("details").EnumerateArray(),
            detail => detail.GetProperty("field").GetString() == field && detail.GetProperty("code").GetString() == code);
    }

    private static async Task SeedAsync(IServiceProvider services) =>
        await ExecuteAsync(services, "INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-1', 'Factory', 'Asia/Jerusalem');");

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
        var path = Path.Combine(Path.GetTempPath(), "MeimadPlanner.MachineModels.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5101", $"--Database:Path={Path.Combine(path, "test.db")}"],
            webHost => webHost.UseSignedInTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "machine-models-client");
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
