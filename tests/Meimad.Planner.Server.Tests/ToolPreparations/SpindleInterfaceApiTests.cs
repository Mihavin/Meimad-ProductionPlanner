using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.Accounts;
using Microsoft.AspNetCore.TestHost;

namespace Meimad.Planner.Server.Tests.ToolPreparations;

/// <summary>Spindle adaptors, pull studs, Machine defaults and per-tool overrides (schema v91).</summary>
public sealed class SpindleInterfaceApiTests
{
    private const string Route = "/api/v1/batch-operations/operation-package/tool-preparation";

    [Fact]
    public async Task The_setup_library_starts_with_bt40_and_is_edited_with_versions_and_the_setup_permission()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        var client = server.Client;

        var library = await LibraryAsync(client);
        var bt40 = Assert.Single(library.GetProperty("adaptors").EnumerateArray());
        Assert.Equal(("BT40", 65.4, 44.45, 63d, 25d), (bt40.GetProperty("name").GetString(), bt40.GetProperty("taperLength").GetDouble(),
            bt40.GetProperty("gaugeDiameter").GetDouble(), bt40.GetProperty("toolChangerDiameter").GetDouble(), bt40.GetProperty("toolChangerLength").GetDouble()));
        // The BT40 studs of the published standards (schema v92): L1 is the length above the holder.
        var studs = library.GetProperty("pullStuds").EnumerateArray().ToDictionary(value => value.GetProperty("pullStudId").GetString()!);
        Assert.Equal(9, studs.Count);
        Assert.Equal(("HAAS BT40 45° M16", 34.93, 23d), (studs["haas-bt40-45-m16"].GetProperty("name").GetString(),
            studs["haas-bt40-45-m16"].GetProperty("exposedLength").GetDouble(), studs["haas-bt40-45-m16"].GetProperty("pilotDiameter").GetDouble()));
        Assert.Equal((90d, 35d, 15d, 60d), (studs["mas-p40t-3"].GetProperty("angle").GetDouble(), studs["mas-p40t-3"].GetProperty("exposedLength").GetDouble(),
            studs["mas-p40t-3"].GetProperty("knobDiameter").GetDouble(), studs["mas-p40t-3"].GetProperty("overallLength").GetDouble()));
        Assert.Equal((15d, 29d), (studs["jis-b6339-40p"].GetProperty("angle").GetDouble(), studs["jis-b6339-40p"].GetProperty("exposedLength").GetDouble()));
        Assert.Equal(19.1, studs["mazak-bt40-45"].GetProperty("exposedLength").GetDouble());

        // Without the Setup permission nothing changes.
        using var anonymous = new HttpClient(server.Application.GetTestServer().CreateHandler()) { BaseAddress = client.BaseAddress };
        using (anonymous.SignedInWithOnly(Permissions.PrepareTools))
        {
            using var refused = await anonymous.PostAsJsonAsync("/api/v1/spindle-adaptors", Adaptor("CAT40", 0));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // A 7:24 taper's small end follows from the gauge diameter when it is not given.
        using var created = await client.PostAsJsonAsync("/api/v1/spindle-adaptors", Adaptor("CAT40", 0));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var cat40 = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = cat40.RootElement.GetProperty("spindleAdaptorId").GetString()!;
        Assert.Equal(Math.Round(44.45 - 68.25 * 7 / 24, 3), cat40.RootElement.GetProperty("smallEndDiameter").GetDouble());
        using var duplicate = await client.PostAsJsonAsync("/api/v1/spindle-adaptors", Adaptor("cat40", 0));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var stale = await client.PutAsJsonAsync($"/api/v1/spindle-adaptors/{id}", Adaptor("CAT40", 5));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var updated = await client.PutAsJsonAsync($"/api/v1/spindle-adaptors/{id}", Adaptor("CAT40 V-flange", 1));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var invalid = await client.PostAsJsonAsync("/api/v1/pull-studs", new { expectedVersion = 0, name = "Bad", exposedLength = 0, knobDiameter = 10 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        using var deleted = await client.DeleteAsync($"/api/v1/spindle-adaptors/{id}?version=2");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task A_machine_default_and_a_per_tool_override_reach_the_tool_room_and_protect_the_library_entries()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        var client = server.Client;

        using var unknown = await client.PutAsJsonAsync("/api/v1/machines/machine-package/spindle-interface",
            new { spindleAdaptorId = "missing", pullStudId = (string?)null, expectedVersion = 0 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        using var machine = await client.PutAsJsonAsync("/api/v1/machines/machine-package/spindle-interface",
            new { spindleAdaptorId = "bt40", pullStudId = "haas-bt40-45-m16", expectedVersion = 0 });
        Assert.Equal(HttpStatusCode.OK, machine.StatusCode);
        using var staleMachine = await client.PutAsJsonAsync("/api/v1/machines/machine-package/spindle-interface",
            new { spindleAdaptorId = "bt40", pullStudId = (string?)null, expectedVersion = 0 });
        Assert.Equal(HttpStatusCode.Conflict, staleMachine.StatusCode);

        using var created = await client.PostAsJsonAsync("/api/v1/pull-studs",
            new { expectedVersion = 0, name = "MAS P40T-I 45°", thread = "M16", angle = 45, exposedLength = 25, knobDiameter = 15 });
        var studId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("pullStudId").GetString()!;

        // T1 overrides the pull stud and records its stick-out (OHL); T3, a probe, follows the Machine.
        var tools = new object[]
        {
            new
            {
                toolIdentifier = "T1", offsetNumber = 1, measuredLength = 120.0, measuredDiameter = 10.0, shapeType = "END_MILL",
                shape = new Dictionary<string, double> { ["cuttingDiameter"] = 10, ["fluteLength"] = 22, ["overallLength"] = 80, ["outsideHolderLength"] = 50 },
                components = new[] { new { sequence = 1, componentType = "HOLDER", name = "ER32 holder", length = (double?)null, diameter = 40.0 } },
                pullStudId = studId
            },
            new { toolIdentifier = "T3", offsetNumber = 3, measuredLength = 150.0, measuredDiameter = 6.0, shapeType = "PROBE" }
        };
        using var unknownOverride = await client.PutAsJsonAsync(Route, new
        {
            expectedVersion = 0, toolTableReleaseId = "tools-1",
            tools = new object[] { new { toolIdentifier = "T1", shapeType = "END_MILL", spindleAdaptorId = "missing" } }
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownOverride.StatusCode);
        using var saved = await client.PutAsJsonAsync(Route, new { expectedVersion = 0, toolTableReleaseId = "tools-1", tools });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var read = await client.GetAsync(Route);
        using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("bt40", root.GetProperty("machineSpindleAdaptorId").GetString());
        Assert.Equal("haas-bt40-45-m16", root.GetProperty("machinePullStudId").GetString());
        Assert.Equal(1, root.GetProperty("spindleAdaptors").GetArrayLength());
        Assert.Equal(10, root.GetProperty("pullStuds").GetArrayLength());
        var rows = root.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(studId, rows[0].GetProperty("pullStudId").GetString());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("spindleAdaptorId").ValueKind);
        Assert.Equal(50, rows[0].GetProperty("shape").GetProperty("outsideHolderLength").GetDouble());
        Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("pullStudId").ValueKind);

        // Entries in use cannot be deleted: make them inactive instead.
        using var inUseByTool = await client.DeleteAsync($"/api/v1/pull-studs/{studId}?version=1");
        Assert.Equal(HttpStatusCode.Conflict, inUseByTool.StatusCode);
        using var inUseByMachine = await client.DeleteAsync("/api/v1/spindle-adaptors/bt40?version=1");
        Assert.Equal(HttpStatusCode.Conflict, inUseByMachine.StatusCode);
        Assert.Contains("spindle_interface_in_use", await inUseByMachine.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static object Adaptor(string name, int expectedVersion) => new
    {
        expectedVersion, name, taperLength = 68.25, gaugeDiameter = 44.45, toolChangerDiameter = 63.5, toolChangerLength = 15.9
    };

    private static async Task<JsonElement> LibraryAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/spindle-library");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
