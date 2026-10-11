using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Meimad.Planner.Server.Tests.Machines;

/// <summary>Edits a Machine's simulation definition the way the Machine Designer does: save the draft, then publish it.</summary>
internal static class MachineSimulationTestApi
{
    internal static async Task<JsonObject> GetAsync(HttpClient client, string machineId)
    {
        using var response = await client.GetAsync($"/api/v1/machines/{machineId}/simulation-definition");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!.AsObject();
        body["etag"] = response.Headers.ETag!.Tag;
        return body;
    }

    internal static async Task<HttpResponseMessage> PutDraftAsync(HttpClient client, string machineId, string tag, JsonObject draft)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/machines/{machineId}/simulation-definition")
        {
            Content = new StringContent(draft.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(tag));
        return await client.SendAsync(request);
    }

    internal static async Task<HttpResponseMessage> PublishAsync(HttpClient client, string machineId, string tag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/machines/{machineId}/simulation-definition/publish");
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(tag));
        return await client.SendAsync(request);
    }

    /// <summary>Applies <paramref name="change"/> to the draft, saves and publishes it; returns the published response.</summary>
    internal static async Task<JsonObject> ChangeAndPublishAsync(HttpClient client, string machineId, Action<JsonObject> change)
    {
        var current = await GetAsync(client, machineId);
        var draft = current["draft"]!.AsObject();
        change(draft);
        using var saved = await PutDraftAsync(client, machineId, current["etag"]!.GetValue<string>(), draft);
        var savedText = await saved.Content.ReadAsStringAsync();
        Assert.True(saved.StatusCode == HttpStatusCode.OK, savedText);
        using var published = await PublishAsync(client, machineId, saved.Headers.ETag!.Tag);
        var publishedText = await published.Content.ReadAsStringAsync();
        Assert.True(published.StatusCode == HttpStatusCode.OK, publishedText);
        return JsonNode.Parse(publishedText)!.AsObject();
    }

    /// <summary>Sets the magazine and timing values (the Machine's former columns) through the definition.</summary>
    internal static Task<JsonObject> SetTimingAsync(
        HttpClient client, string machineId, int? usableToolPositions, double? rapidRate, double? toolChangeSeconds, double machineTimeFactor) =>
        ChangeAndPublishAsync(client, machineId, draft =>
        {
            var magazine = draft["magazine"] as JsonObject ?? new JsonObject();
            magazine["usablePositions"] = usableToolPositions;
            magazine["toolChangeSeconds"] = toolChangeSeconds;
            if (magazine["nominalCapacity"] is { } nominal && usableToolPositions > nominal.GetValue<int>()) magazine.Remove("nominalCapacity");
            draft["magazine"] = magazine;
            draft["timing"] = new JsonObject
            {
                ["rapidRateMillimetersPerMinute"] = rapidRate,
                ["machineTimeFactor"] = machineTimeFactor
            };
        });
}
