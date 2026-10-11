using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Machines;

public sealed class MachineSimulationApiTests
{
    [Fact]
    public async Task A_created_Machine_has_a_published_definition_built_from_its_values()
    {
        await RunAsync(async (_, client) =>
        {
            var id = await CreateMachineAsync(client, "M-VF3", "haas-vf-3ss");

            var record = await MachineSimulationTestApi.GetAsync(client, id);

            Assert.Equal($"\"machine-simulation:{id}:v1\"", record["etag"]!.GetValue<string>());
            Assert.Equal(1, record["draftVersion"]!.GetValue<int>());
            Assert.Equal(1, record["publishedVersion"]!.GetValue<int>());
            Assert.False(record["hasUnpublishedChanges"]!.GetValue<bool>());
            var draft = record["draft"]!.AsObject();
            Assert.Equal("haas-vf-3ss", draft["template"]!.GetValue<string>());
            Assert.Equal("all-axes-zero", draft["referencePose"]!.GetValue<string>());
            Assert.Contains(draft["nodes"]!.AsArray(), node => node!["id"]!.GetValue<string>() == "x-table"
                && node["joint"]!["axis"]!.GetValue<string>() == "X");
            Assert.Equal(24, draft["magazine"]!["usablePositions"]!.GetValue<int>());
            Assert.Equal(4.5, draft["magazine"]!["toolChangeSeconds"]!.GetValue<double>());
            Assert.Equal(30000, draft["timing"]!["rapidRateMillimetersPerMinute"]!.GetValue<double>());
            Assert.Equal(1.1, draft["timing"]!["machineTimeFactor"]!.GetValue<double>());
            Assert.Equal("spindle", draft["mounts"]!["tool"]!["node"]!.GetValue<string>());
            // Positions from the installed definition are not verified on the machine: a warning, never an error.
            Assert.DoesNotContain(record["issues"]!.AsArray(), issue => issue!["severity"]!.GetValue<string>() == "error");
            Assert.Contains(record["issues"]!.AsArray(), issue => issue!["code"]!.GetValue<string>() == "unverified");

            // The NC viewer reads every published definition in one request.
            using var all = await client.GetAsync("/api/v1/machines/simulation-definitions");
            Assert.Equal(HttpStatusCode.OK, all.StatusCode);
            var item = Assert.Single(JsonNode.Parse(await all.Content.ReadAsStringAsync())!["items"]!.AsArray());
            Assert.Equal(id, item!["machineId"]!.GetValue<string>());
            Assert.Equal("haas-vf-3ss", item["published"]!["template"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task A_saved_draft_changes_nothing_until_it_is_published_and_publishing_writes_the_projection()
    {
        await RunAsync(async (application, client) =>
        {
            var id = await CreateMachineAsync(client, "M-PUB", "haas-vf-3ss");
            using var before = await client.GetAsync($"/api/v1/machines/{id}");
            var machineTag = before.Headers.ETag!.Tag;
            var record = await MachineSimulationTestApi.GetAsync(client, id);
            var draft = record["draft"]!.AsObject();
            draft["magazine"]!["usablePositions"] = 20;
            draft["timing"]!["rapidRateMillimetersPerMinute"] = 25400;

            using var saved = await MachineSimulationTestApi.PutDraftAsync(client, id, record["etag"]!.GetValue<string>(), draft);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal($"\"machine-simulation:{id}:v2\"", saved.Headers.ETag!.Tag);
            var savedBody = JsonNode.Parse(await saved.Content.ReadAsStringAsync())!;
            Assert.True(savedBody["hasUnpublishedChanges"]!.GetValue<bool>());
            Assert.Equal(1, savedBody["publishedVersion"]!.GetValue<int>());
            Assert.Equal(24, savedBody["published"]!["magazine"]!["usablePositions"]!.GetValue<int>());
            using (var unchanged = await client.GetAsync($"/api/v1/machines/{id}"))
            {
                Assert.Equal(machineTag, unchanged.Headers.ETag!.Tag);
                using var json = JsonDocument.Parse(await unchanged.Content.ReadAsStringAsync());
                Assert.Equal(24, json.RootElement.GetProperty("usableToolPositions").GetInt32());
            }

            // A save based on the older draft is refused and names who saved it.
            using (var stale = await MachineSimulationTestApi.PutDraftAsync(client, id, record["etag"]!.GetValue<string>(), draft))
            {
                Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
                var error = JsonNode.Parse(await stale.Content.ReadAsStringAsync())!["error"]!;
                Assert.Equal("resource_version_stale", error["code"]!.GetValue<string>());
                Assert.Equal("Machine definition", error["conflict"]!["resource"]!.GetValue<string>());
                Assert.False(string.IsNullOrEmpty(error["conflict"]!["changedBy"]!.GetValue<string>()));
            }
            using (var missing = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{id}/simulation-definition/publish"))
            using (var precondition = await client.SendAsync(missing))
            {
                Assert.Equal(HttpStatusCode.PreconditionRequired, precondition.StatusCode);
            }

            using var published = await MachineSimulationTestApi.PublishAsync(client, id, saved.Headers.ETag!.Tag);
            Assert.Equal(HttpStatusCode.OK, published.StatusCode);
            var publishedBody = JsonNode.Parse(await published.Content.ReadAsStringAsync())!;
            Assert.Equal(2, publishedBody["publishedVersion"]!.GetValue<int>());
            Assert.False(publishedBody["hasUnpublishedChanges"]!.GetValue<bool>());
            using (var after = await client.GetAsync($"/api/v1/machines/{id}"))
            {
                Assert.NotEqual(machineTag, after.Headers.ETag!.Tag);
                using var json = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
                Assert.Equal(20, json.RootElement.GetProperty("usableToolPositions").GetInt32());
                Assert.Equal(25400, json.RootElement.GetProperty("rapidRateMillimetersPerMinute").GetDouble());
                Assert.Equal(4.5, json.RootElement.GetProperty("toolChangeTimeSeconds").GetDouble());
                Assert.Equal("haas-vf-3ss", json.RootElement.GetProperty("ncViewerMachine").GetString());
            }
            Assert.Equal(1L, await ScalarAsync(application.Services,
                "SELECT COUNT(*) FROM structured_event_log WHERE event_type = 'machine_simulation_definition_published';"));

            // Machine PATCH cannot change a published value; repeating it is accepted.
            using (var patch = Patch(id, (await client.GetAsync($"/api/v1/machines/{id}")).Headers.ETag!.Tag,
                       """{"usableToolPositions":21,"machineTimeFactor":1.1}"""))
            using (var refused = await client.SendAsync(patch))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
                var text = await refused.Content.ReadAsStringAsync();
                Assert.Contains("machine_definition_owned_field", text, StringComparison.Ordinal);
                Assert.Contains("usableToolPositions", text, StringComparison.Ordinal);
                Assert.DoesNotContain("machineTimeFactor", text, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public async Task An_invalid_definition_can_be_saved_but_not_published()
    {
        await RunAsync(async (_, client) =>
        {
            var id = await CreateMachineAsync(client, "M-BAD", "haas-vf-3ss");
            var record = await MachineSimulationTestApi.GetAsync(client, id);
            var draft = record["draft"]!.AsObject();
            var xTable = draft["nodes"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == "x-table")!;
            xTable["joint"]!["direction"] = new JsonArray(0, 0, 0);
            draft["magazine"]!["nominalCapacity"] = 10;

            using var validate = await client.PostAsync($"/api/v1/machines/{id}/simulation-definition/validate",
                new StringContent(draft.ToJsonString(), Encoding.UTF8, "application/json"));
            var issues = JsonNode.Parse(await validate.Content.ReadAsStringAsync())!["issues"]!.AsArray();
            Assert.Contains(issues, issue => issue!["code"]!.GetValue<string>() == "zero_direction");
            Assert.Contains(issues, issue => issue!["code"]!.GetValue<string>() == "magazine_capacity");

            using var saved = await MachineSimulationTestApi.PutDraftAsync(client, id, record["etag"]!.GetValue<string>(), draft);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            using var refused = await MachineSimulationTestApi.PublishAsync(client, id, saved.Headers.ETag!.Tag);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var error = JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["error"]!;
            Assert.Equal("machine_definition_invalid", error["code"]!.GetValue<string>());
            Assert.Contains(error["details"]!.AsArray(), detail => detail!["path"]!.GetValue<string>().EndsWith(".joint.direction", StringComparison.Ordinal));
            var after = await MachineSimulationTestApi.GetAsync(client, id);
            Assert.Equal(1, after["publishedVersion"]!.GetValue<int>());

            // Malformed JSON and a file path that was never part of the definition are refused outright.
            using (var malformed = await MachineSimulationTestApi.PutDraftAsync(client, id, after["etag"]!.GetValue<string>(), new JsonObject { ["nodes"] = "x" }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            }
            xTable["models"] = new JsonArray(new JsonObject { ["legacyPath"] = @"C:\Windows\System32\config\SAM" });
            using var newPath = await MachineSimulationTestApi.PutDraftAsync(client, id, after["etag"]!.GetValue<string>(), draft);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, newPath.StatusCode);
            Assert.Contains("legacy_path_not_allowed", await newPath.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Reading_needs_sign_in_and_writing_needs_setup_permission()
    {
        await RunAsync(async (_, client) =>
        {
            var id = await CreateMachineAsync(client, "M-PERM", null);
            var record = await MachineSimulationTestApi.GetAsync(client, id);
            using (client.SignedInWithOnly("cases.edit"))
            {
                using var read = await client.GetAsync($"/api/v1/machines/{id}/simulation-definition");
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                using var put = await MachineSimulationTestApi.PutDraftAsync(client, id, record["etag"]!.GetValue<string>(), record["draft"]!.AsObject());
                Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
                using var publish = await MachineSimulationTestApi.PublishAsync(client, id, record["etag"]!.GetValue<string>());
                Assert.Equal(HttpStatusCode.Forbidden, publish.StatusCode);
                using var upload = await client.PostAsync($"/api/v1/machines/{id}/simulation-assets?name=a.glb", new ByteArrayContent(Glb()));
                Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
            }
            using (client.SignedOut())
            {
                using var read = await client.GetAsync($"/api/v1/machines/{id}/simulation-definition");
                Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
            }
            using var missing = await client.GetAsync("/api/v1/machines/no-such-machine/simulation-definition");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        });
    }

    [Fact]
    public async Task Meshes_are_stored_once_by_hash_served_by_id_and_referenced_by_published_definitions()
    {
        await RunAsync(async (application, client) =>
        {
            var id = await CreateMachineAsync(client, "M-GLB", "haas-vf-3ss");
            var glb = Glb();

            using var uploaded = await client.PostAsync($"/api/v1/machines/{id}/simulation-assets?name=Table.glb", new ByteArrayContent(glb));
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            var asset = JsonNode.Parse(await uploaded.Content.ReadAsStringAsync())!;
            var assetId = asset["assetId"]!.GetValue<string>();
            Assert.Matches("^[0-9a-f]{32}$", assetId);
            Assert.Equal("model/gltf-binary", asset["mediaType"]!.GetValue<string>());
            Assert.Equal(glb.Length, asset["sizeBytes"]!.GetValue<long>());
            using (var again = await client.PostAsync($"/api/v1/machines/{id}/simulation-assets?name=Copy.glb", new ByteArrayContent(glb)))
            {
                Assert.Equal(assetId, JsonNode.Parse(await again.Content.ReadAsStringAsync())!["assetId"]!.GetValue<string>());
            }
            var stl = new byte[84 + 50];
            BinaryPrimitives.WriteUInt32LittleEndian(stl.AsSpan(80), 1);
            using (var stlUpload = await client.PostAsync($"/api/v1/machines/{id}/simulation-assets?name=head.STL", new ByteArrayContent(stl)))
            {
                Assert.Equal(HttpStatusCode.Created, stlUpload.StatusCode);
            }

            // Content and type are checked; only .glb and .stl are accepted.
            using (var fake = await client.PostAsync($"/api/v1/machines/{id}/simulation-assets?name=fake.glb", new ByteArrayContent(Encoding.ASCII.GetBytes("not a glb file"))))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, fake.StatusCode);
                Assert.Contains("invalid_asset_content", await fake.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
            using (var obj = await client.PostAsync($"/api/v1/machines/{id}/simulation-assets?name=..%5Cpart.obj", new ByteArrayContent(glb)))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, obj.StatusCode);
                Assert.Contains("unsupported_asset_type", await obj.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using var list = await client.GetAsync($"/api/v1/machines/{id}/simulation-assets");
            Assert.Equal(2, JsonNode.Parse(await list.Content.ReadAsStringAsync())!["items"]!.AsArray().Count);
            using var content = await client.GetAsync($"/api/v1/machines/{id}/simulation-assets/{assetId}/content");
            Assert.Equal("model/gltf-binary", content.Content.Headers.ContentType!.MediaType);
            Assert.Equal(glb, await content.Content.ReadAsByteArrayAsync());
            using var otherMachine = await client.GetAsync($"/api/v1/machines/{await CreateMachineAsync(client, "M-OTHER", null)}/simulation-assets/{assetId}/content");
            Assert.Equal(HttpStatusCode.NotFound, otherMachine.StatusCode);

            // The file lives below the release root (backed up with the releases), named by its hash.
            var root = application.Services.GetRequiredService<GCodeOptions>().ResolvedReleaseRoot;
            Assert.True(File.Exists(Path.Combine(root, "machine-assets", id, asset["sha256"]!.GetValue<string>() + ".glb")));
            var recovery = await RecoveryDatabase.ReadAsync(application.Services.GetRequiredService<SqliteDatabase>().DatabasePath, CancellationToken.None);
            Assert.Contains(recovery.Files, file => file.Path == $"gcode/machine-assets/{id}/{asset["sha256"]!.GetValue<string>()}.glb");

            // A definition may publish an uploaded mesh of this Machine only.
            await MachineSimulationTestApi.ChangeAndPublishAsync(client, id, draft =>
            {
                var table = draft["nodes"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == "x-table")!;
                table["models"] = new JsonArray(new JsonObject
                {
                    ["assetId"] = assetId, ["name"] = "Table", ["color"] = "#336699", ["opacity"] = 0.4, ["visible"] = false,
                    ["collisionParticipation"] = true
                });
            });
            var reread = await MachineSimulationTestApi.GetAsync(client, id);
            var model = reread["published"]!["nodes"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == "x-table")!["models"]![0]!;
            Assert.Equal(assetId, model["assetId"]!.GetValue<string>());
            Assert.False(model["visible"]!.GetValue<bool>());
            Assert.True(model["collisionParticipation"]!.GetValue<bool>());
            Assert.Equal(0.4, model["opacity"]!.GetValue<double>());

            var draft = reread["draft"]!.AsObject();
            draft["nodes"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == "x-table")!["models"]![0]!["assetId"] = new string('a', 32);
            using var saved = await MachineSimulationTestApi.PutDraftAsync(client, id, reread["etag"]!.GetValue<string>(), draft);
            using var refused = await MachineSimulationTestApi.PublishAsync(client, id, saved.Headers.ETag!.Tag);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("asset_missing", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        });
    }

    /// <summary>A minimal valid glTF 2 binary: header plus a JSON chunk.</summary>
    internal static byte[] Glb()
    {
        var json = Encoding.UTF8.GetBytes("""{"asset":{"version":"2.0"}}""".PadRight(28));
        var bytes = new byte[12 + 8 + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)json.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x4E4F534A);
        json.CopyTo(bytes, 20);
        return bytes;
    }

    private static async Task<string> CreateMachineAsync(HttpClient client, string number, string? viewerMachine)
    {
        using var created = await client.PostAsJsonAsync("/api/v1/machines", new
        {
            number,
            name = $"Machine {number}",
            processType = "mill",
            capabilities = Array.Empty<string>(),
            workingCalendarId = "calendar-1",
            isActive = true,
            displayEnabled = true,
            ncViewerMachine = viewerMachine,
            usableToolPositions = 24,
            rapidRateMillimetersPerMinute = 30000,
            toolChangeTimeSeconds = 4.5,
            machineTimeFactor = 1.1
        });
        var text = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, text);
        return JsonNode.Parse(text)!["machineId"]!.GetValue<string>();
    }

    private static HttpRequestMessage Patch(string id, string tag, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/machines/{id}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(tag));
        return request;
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
        var path = Path.Combine(Path.GetTempPath(), "MeimadPlanner.MachineSimulation.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5102", $"--Database:Path={Path.Combine(path, "test.db")}",
             $"--GCode:ReleaseRoot={Path.Combine(path, "releases")}"],
            webHost => webHost.UseSignedInTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "machine-simulation-client");
            await using (var connection = await application.Services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO working_calendars (id, name, time_zone_id) VALUES ('calendar-1', 'Factory', 'Asia/Jerusalem');";
                await command.ExecuteNonQueryAsync();
            }
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
