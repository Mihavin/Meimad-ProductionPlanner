using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Tests.Api;

/// <summary>The Operation's NC viewer work offsets (Server schema v120) as the Windows client calls them.</summary>
public sealed class OperationViewerWorkOffsetsApiClientTests
{
    private const string ItemJson = """
        {"caseOperationId":"op 10","viewerMachine":"meimad-machine-m15","offsets":{"G54":{"X":-3.5627,"Z":-450.25},"G54.1 P3":{"C":90}},
         "version":2,"entityTag":"\"case-operation-viewer-work-offsets:op 10/meimad-machine-m15:v2\"",
         "updatedAt":"2026-10-13T08:00:00Z","updatedBy":"planner"}
        """;

    [Fact]
    public async Task List_save_and_delete_use_the_operation_route_and_the_entity_tag()
    {
        var handler = new Handler(
            Json(HttpStatusCode.OK, $$"""{"items":[{{ItemJson}}]}"""),
            Json(HttpStatusCode.Created, ItemJson),
            Json(HttpStatusCode.OK, ItemJson),
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using var api = Create(handler);

        var listed = Assert.Single(await api.ListOperationViewerWorkOffsetsAsync("case 1", "op 10"));
        Assert.Equal(-3.5627, listed.Offsets["G54"]["X"]);
        Assert.Equal(90, listed.Offsets["G54.1 P3"]["C"]);
        Assert.Equal("\"case-operation-viewer-work-offsets:op 10/meimad-machine-m15:v2\"", listed.EntityTag);

        var offsets = new Dictionary<string, IReadOnlyDictionary<string, double>> { ["G54"] = new Dictionary<string, double> { ["X"] = -4 } };
        await api.SaveOperationViewerWorkOffsetsAsync("case 1", "op 10", "meimad-machine-m15", offsets, null);
        await api.SaveOperationViewerWorkOffsetsAsync("case 1", "op 10", "meimad-machine-m15", offsets, listed.EntityTag);
        await api.DeleteOperationViewerWorkOffsetsAsync("case 1", "op 10", "meimad-machine-m15", listed.EntityTag);

        Assert.Equal("/api/v1/cases/case%201/operations/op%2010/viewer-work-offsets", handler.Requests[0].Path);
        var create = handler.Requests[1];
        Assert.Equal((HttpMethod.Put, "/api/v1/cases/case%201/operations/op%2010/viewer-work-offsets/meimad-machine-m15", "*", (string?)null),
            (create.Method, create.Path, create.IfNoneMatch, create.IfMatch));
        using (var body = JsonDocument.Parse(create.Body))
            Assert.Equal(-4, body.RootElement.GetProperty("offsets").GetProperty("G54").GetProperty("X").GetDouble());
        Assert.Equal((listed.EntityTag, (string?)null), (handler.Requests[2].IfMatch, handler.Requests[2].IfNoneMatch));
        Assert.Equal((HttpMethod.Delete, listed.EntityTag), (handler.Requests[3].Method, handler.Requests[3].IfMatch));
    }

    private static PlannerApiClient Create(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://planner-server:5080/") });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed record Recorded(HttpMethod Method, string Path, string? IfMatch, string? IfNoneMatch, byte[] Body);

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> queue = new(responses);

        internal List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            static string? Header(HttpRequestMessage message, string name) =>
                message.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
            Requests.Add(new Recorded(request.Method, request.RequestUri!.PathAndQuery, Header(request, "If-Match"),
                Header(request, "If-None-Match"),
                request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return queue.Dequeue();
        }
    }
}
