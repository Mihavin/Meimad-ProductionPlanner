using System.Net;
using System.Net.Http;
using System.Text;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Tests.Api;

/// <summary>The Case Operation thumbnail routes (Server schema v118) as the Windows client and importers call them.</summary>
public sealed class OperationThumbnailApiClientTests
{
    private const string ItemJson = """
        {"caseOperationId":"op 10","source":"package","fileName":"finished.png","mediaType":"image/png","sizeBytes":2048,
         "sha256":"aa","sourceReference":"C:\\pkg\\part.meimad-sim","version":2,
         "entityTag":"\"case-operation-thumbnail:op 10:package:v2\"","updatedAt":"2026-10-12T08:00:00Z","updatedBy":"planner"}
        """;

    [Fact]
    public async Task Upload_creates_or_replaces_with_the_slot_version_and_names_the_source()
    {
        var handler = new Handler(Json(HttpStatusCode.Created, ItemJson), Json(HttpStatusCode.OK, ItemJson));
        using var api = Create(handler);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        var created = await api.UploadOperationThumbnailAsync("case 1", "op 10", OperationThumbnailSources.Package, @"C:\x\finished.png",
            png, null, "windows-01", @"C:\pkg\part.meimad-sim");
        await api.UploadOperationThumbnailAsync("case 1", "op 10", OperationThumbnailSources.Snapshot, "snap.png", png,
            created.EntityTag, "windows-01");

        Assert.Equal(2, created.Version);
        Assert.Equal("\"case-operation-thumbnail:op 10:package:v2\"", created.EntityTag);
        var first = handler.Requests[0];
        Assert.Equal(HttpMethod.Put, first.Method);
        Assert.Equal("/api/v1/cases/case%201/operations/op%2010/thumbnails/package?name=finished.png&reference=C%3A%5Cpkg%5Cpart.meimad-sim", first.Path);
        Assert.Equal("*", first.IfNoneMatch);
        Assert.Null(first.IfMatch);
        Assert.Equal(png, first.Body);
        Assert.Equal("windows-01", first.ClientId);
        var second = handler.Requests[1];
        Assert.Equal("/api/v1/cases/case%201/operations/op%2010/thumbnails/snapshot?name=snap.png", second.Path);
        Assert.Equal("\"case-operation-thumbnail:op 10:package:v2\"", second.IfMatch);
        Assert.Null(second.IfNoneMatch);
    }

    [Fact]
    public async Task Set_reads_the_current_version_first_so_an_importer_can_always_store_its_picture()
    {
        var list = $$"""{"caseOperationId":"op 10","tvSource":"package","items":[{{ItemJson}}]}""";
        var handler = new Handler(Json(HttpStatusCode.OK, list), Json(HttpStatusCode.OK, ItemJson),
            Json(HttpStatusCode.OK, """{"caseOperationId":"op 10","tvSource":"placeholder","items":[]}"""), Json(HttpStatusCode.Created, ItemJson));
        using var api = Create(handler);
        IPlannerApiClient client = api;

        await client.SetOperationThumbnailAsync("case 1", "op 10", OperationThumbnailSources.Package, "finished.png", [1, 2, 3], "importer");
        await client.SetOperationThumbnailAsync("case 1", "op 10", OperationThumbnailSources.Package, "finished.png", [1, 2, 3], "importer");

        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/v1/cases/case%201/operations/op%2010/thumbnails", handler.Requests[0].Path);
        Assert.Equal("\"case-operation-thumbnail:op 10:package:v2\"", handler.Requests[1].IfMatch);
        Assert.Equal("*", handler.Requests[3].IfNoneMatch);
    }

    [Fact]
    public async Task Delete_sends_the_version_and_a_stale_change_surfaces_the_server_error()
    {
        var stale = Json(HttpStatusCode.PreconditionFailed,
            """{"error":{"code":"resource_version_stale","message":"The Operation thumbnail changed since you opened it, so your change was not saved.","details":[]}}""");
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.NoContent), stale);
        using var api = Create(handler);

        await api.DeleteOperationThumbnailAsync("case-1", "op-10", "snapshot", "\"case-operation-thumbnail:op-10:snapshot:v1\"", "windows-01");
        var error = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.DeleteOperationThumbnailAsync("case-1", "op-10", "snapshot", "\"case-operation-thumbnail:op-10:snapshot:v1\"", "windows-01"));

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal("/api/v1/cases/case-1/operations/op-10/thumbnails/snapshot", handler.Requests[0].Path);
        Assert.Equal("\"case-operation-thumbnail:op-10:snapshot:v1\"", handler.Requests[0].IfMatch);
        Assert.Equal("resource_version_stale", error.Code);
    }

    private static PlannerApiClient Create(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://planner-server:5080/") });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed record Recorded(HttpMethod Method, string Path, string? IfMatch, string? IfNoneMatch, string? ClientId, byte[] Body);

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> queue = new(responses);

        internal List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            static string? Header(HttpRequestMessage message, string name) =>
                message.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
            Requests.Add(new Recorded(request.Method, request.RequestUri!.PathAndQuery, Header(request, "If-Match"),
                Header(request, "If-None-Match"), Header(request, "X-Meimad-Client-Id"),
                request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return queue.Dequeue();
        }
    }
}
