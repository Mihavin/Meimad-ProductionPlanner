using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Tests.ToolPreparations;
using Microsoft.AspNetCore.TestHost;

namespace Meimad.Planner.Server.Tests.ToolCatalog;

public sealed class ToolCatalogApiTests
{
    private const string Route = "/api/v1/tool-catalog";
    private const string PreparationRoute = "/api/v1/batch-operations/operation-package/tool-preparation";

    [Fact]
    public async Task Catalog_tools_get_stable_internal_codes_external_ids_and_optimistic_versions()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        var client = server.Client;

        // Writes need the client identity headers, like the Tool Room's measurements.
        using var anonymous = new HttpClient(server.Application.GetTestServer().CreateHandler()) { BaseAddress = client.BaseAddress };
        using var unidentified = await anonymous.PostAsJsonAsync(Route, Turning());
        Assert.Equal(HttpStatusCode.PreconditionRequired, unidentified.StatusCode);

        using var created = await client.PostAsJsonAsync(Route, Turning());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var turning = createdJson.RootElement;
        var turningId = turning.GetProperty("catalogToolId").GetString()!;
        Assert.Equal("MT-00001", turning.GetProperty("internalCode").GetString());
        Assert.Equal(1, turning.GetProperty("internalNumber").GetInt32());
        Assert.Equal("TURNING_TOOL", turning.GetProperty("toolType").GetString());
        Assert.Equal("TURNING", turning.GetProperty("family").GetString());
        Assert.Equal("RIGHT", turning.GetProperty("hand").GetString());
        Assert.Equal(0.8, turning.GetProperty("shape").GetProperty("cornerRadius").GetDouble());
        Assert.Equal(95, turning.GetProperty("shape").GetProperty("leadAngle").GetDouble());
        Assert.Equal("CNMG120408", turning.GetProperty("attributes").GetProperty("insertCode").GetString());
        Assert.Equal(["ERP", "Sandvik"], turning.GetProperty("externalIds").EnumerateArray().Select(entry => entry.GetProperty("system").GetString()));
        Assert.Equal(1, turning.GetProperty("version").GetInt32());
        Assert.True(turning.GetProperty("isActive").GetBoolean());
        Assert.Equal("tool-room-user", turning.GetProperty("updatedBy").GetString());

        using var second = await client.PostAsJsonAsync(Route, Mill());
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var millId = secondJson.RootElement.GetProperty("catalogToolId").GetString()!;
        Assert.Equal("MT-00002", secondJson.RootElement.GetProperty("internalCode").GetString());
        // A milling cutter carries no hand even when one is sent.
        Assert.Equal(JsonValueKind.Null, secondJson.RootElement.GetProperty("hand").ValueKind);

        Assert.Equal(["MT-00001", "MT-00002"], await CodesAsync(client, Route));
        Assert.Equal(["MT-00001"], await CodesAsync(client, $"{Route}?query=K-4711"));         // an external id
        Assert.Equal(["MT-00001"], await CodesAsync(client, $"{Route}?query=pclnr"));          // the name, case-insensitive
        Assert.Equal(["MT-00002"], await CodesAsync(client, $"{Route}?type=END_MILL"));
        Assert.Empty(await CodesAsync(client, $"{Route}?query=nothing-like-it"));

        // A replace names the version it replaces.
        using var unversioned = await client.PutAsJsonAsync($"{Route}/{turningId}", Turning() with { Name = "renamed" });
        Assert.Equal(HttpStatusCode.BadRequest, unversioned.StatusCode);
        using var stale = await client.PutAsJsonAsync($"{Route}/{turningId}", Turning() with { Name = "renamed", ExpectedVersion = 5 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("tool_catalog_version_conflict", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var updated = await client.PutAsJsonAsync($"{Route}/{turningId}", Turning() with
        {
            Name = "PCLNL 2525 M12 (left hand)", Hand = "left", ExpectedVersion = 1, ExternalIds = [new("ERP", "K-4711")]
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal(2, updatedJson.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("LEFT", updatedJson.RootElement.GetProperty("hand").GetString());
        Assert.Equal("MT-00001", updatedJson.RootElement.GetProperty("internalCode").GetString());
        Assert.Single(updatedJson.RootElement.GetProperty("externalIds").EnumerateArray());

        using var one = await client.GetAsync($"{Route}/{turningId}");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Contains("PCLNL 2525 M12 (left hand)", await one.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var missing = await client.GetAsync($"{Route}/no-such-tool");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // Inactive tools stay in history and are listed on request only.
        using var deactivated = await client.PutAsJsonAsync($"{Route}/{turningId}", Turning() with { IsActive = false, ExpectedVersion = 2 });
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal(["MT-00002"], await CodesAsync(client, Route));
        Assert.Equal(["MT-00001", "MT-00002"], await CodesAsync(client, $"{Route}?includeInactive=true"));

        // Validation.
        await AssertRejectedAsync(client, Turning() with { ToolType = "LASER" }, "tool_catalog_tool_type_invalid");
        await AssertRejectedAsync(client, Turning() with { Hand = "UP" }, "tool_catalog_hand_invalid");
        await AssertRejectedAsync(client, Turning() with { Name = " " }, "tool_catalog_name_invalid");
        await AssertRejectedAsync(client, Turning() with { ExternalIds = [new("ERP", "K-1"), new("erp", "k-1")] }, "tool_catalog_external_id_duplicate");
        await AssertRejectedAsync(client, Turning() with { Shape = new() { ["weight"] = 1 } }, "tool_catalog_shape_dimension_unknown");
        await AssertRejectedAsync(client, Turning() with { Shape = new() { ["leadAngle"] = 200 } }, "tool_catalog_shape_dimension_invalid");
        await AssertRejectedAsync(client, Turning() with { Attributes = new() { ["colour"] = "red" } }, "tool_catalog_attribute_unknown");

        // The Tool Room links a prepared tool to its catalog entry and describes turning tools with their hand.
        using var unknownLink = await client.PutAsJsonAsync(PreparationRoute, Preparation(0, "no-such-tool"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownLink.StatusCode);
        Assert.Contains("tool_preparation_catalog_tool_unknown", await unknownLink.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var linked = await client.PutAsJsonAsync(PreparationRoute, Preparation(0, millId));
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        using var linkedJson = JsonDocument.Parse(await linked.Content.ReadAsStringAsync());
        var preparedRows = linkedJson.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal("EXTERNAL_GROOVING", preparedRows[0].GetProperty("shapeType").GetString());
        Assert.Equal("LEFT", preparedRows[0].GetProperty("hand").GetString());
        Assert.Equal(3, preparedRows[0].GetProperty("shape").GetProperty("cuttingWidth").GetDouble());
        Assert.Equal(millId, preparedRows[0].GetProperty("catalogToolId").GetString());
        Assert.Equal(JsonValueKind.Null, preparedRows[1].GetProperty("hand").ValueKind);

        // A referenced tool cannot be deleted (deactivate it); an unreferenced one can.
        using var inUse = await client.DeleteAsync($"{Route}/{millId}");
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        Assert.Contains("tool_catalog_in_use", await inUse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var deleted = await client.DeleteAsync($"{Route}/{turningId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await client.GetAsync($"{Route}/{turningId}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(0L, await server.ScalarAsync("SELECT COUNT(*) FROM catalog_tool_external_ids WHERE catalog_tool_id = '" + turningId + "';"));
        // The freed number is never reused.
        using var third = await client.PostAsJsonAsync(Route, Mill() with { Name = "Another end mill" });
        using var thirdJson = JsonDocument.Parse(await third.Content.ReadAsStringAsync());
        Assert.Equal("MT-00003", thirdJson.RootElement.GetProperty("internalCode").GetString());
    }

    [Fact]
    public async Task The_tool_type_list_names_families_hands_dimension_keys_and_attributes()
    {
        await using var server = await ToolPreparationApiTests.TestServer.StartAsync(verificationEnabled: false);
        using var response = await server.Client.GetAsync($"{Route}/types");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var types = json.RootElement.GetProperty("types").EnumerateArray()
            .ToDictionary(type => type.GetProperty("code").GetString()!, type => (Family: type.GetProperty("family").GetString(), Handed: type.GetProperty("handed").GetBoolean()));
        Assert.Equal(29, types.Count);
        Assert.Equal(("TURNING", true), types["EXTERNAL_GROOVING"]);
        Assert.Equal(("TURNING", true), types["INTERNAL_THREADING"]);
        Assert.Equal(("MILLING", false), types["THREAD_MILL"]);
        Assert.Equal(("HOLE_MAKING", false), types["COUNTERSINK"]);
        Assert.Equal(("OTHER", false), types["PROBE"]);
        var keys = json.RootElement.GetProperty("dimensionKeys").EnumerateArray().Select(key => key.GetString()).ToArray();
        Assert.Contains("cuttingWidth", keys);
        Assert.Contains("leadAngle", keys);
        Assert.Contains("pitch", keys);
        Assert.Equal(["RIGHT", "LEFT", "NEUTRAL"], json.RootElement.GetProperty("hands").EnumerateArray().Select(hand => hand.GetString()));
        Assert.Contains("insertCode", json.RootElement.GetProperty("attributeKeys").EnumerateArray().Select(key => key.GetString()));
    }

    private static async Task<string[]> CodesAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("internalCode").GetString()!).ToArray();
    }

    private static async Task AssertRejectedAsync(HttpClient client, ToolRequest request, string code)
    {
        using var response = await client.PostAsJsonAsync(Route, request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static ToolRequest Turning() => new(
        "PCLNR 2525 M12 + CNMG 12 04 08", "TURNING_TOOL", "RIGHT", "External roughing holder, 95 degrees",
        new() { ["cornerRadius"] = 0.8, ["leadAngle"] = 95, ["shankWidth"] = 25, ["shankHeight"] = 25, ["overallLength"] = 150 },
        new() { ["insertCode"] = "CNMG120408", ["holderCode"] = "PCLNR2525M12" },
        [new("ERP", "K-4711"), new("Sandvik", "PCLNR 2525M 12")], true, null);

    private static ToolRequest Mill() => new(
        "Flat end mill D10 Z4", "END_MILL", "RIGHT", null,
        new() { ["cuttingDiameter"] = 10, ["fluteLength"] = 22, ["overallLength"] = 72, ["shankDiameter"] = 10, ["fluteCount"] = 4 },
        new() { ["material"] = "Carbide", ["coating"] = "AlTiN" },
        [new("CAM", "EM10-4F")], true, null);

    private static PreparationRequest Preparation(int expectedVersion, string catalogToolId) => new(
        expectedVersion, "tools-1", null,
        [
            new("T1", 1, 120.5, 10, "EXTERNAL_GROOVING", new() { ["cuttingWidth"] = 3, ["maxDepth"] = 12 }, null, [], "LEFT", catalogToolId),
            new("T2", 2, 98.25, 8.4, "DRILL", new() { ["pointAngle"] = 118 }, null, [], "LEFT", null)
        ]);

    private sealed record ExternalIdRequest(string System, string Value);

    private sealed record ToolRequest(
        string? Name, string? ToolType, string? Hand, string? Description,
        Dictionary<string, double>? Shape, Dictionary<string, string>? Attributes,
        ExternalIdRequest[]? ExternalIds, bool? IsActive, int? ExpectedVersion);

    private sealed record PreparedToolRequest(
        string ToolIdentifier, int? OffsetNumber, double? MeasuredLength, double? MeasuredDiameter, string ShapeType,
        Dictionary<string, double> Shape, string? Notes, object[] Components, string? Hand, string? CatalogToolId);

    private sealed record PreparationRequest(int ExpectedVersion, string ToolTableReleaseId, string? Comment, PreparedToolRequest[] Tools);
}
