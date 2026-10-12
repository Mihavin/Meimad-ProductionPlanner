using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// Machine 3D models, the Operation's Cimatron setup and the meshes the NC viewer is served
/// (Server schema v113), and the GPU switch of the viewer window.
/// </summary>
public sealed class NcViewerSetupSceneTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner.NcViewerScene.Tests", Guid.NewGuid().ToString("N"));

    public NcViewerSetupSceneTests() => Directory.CreateDirectory(folder);

    [Fact]
    public void Machine_models_are_keyed_by_viewer_machine_and_the_assigned_machine_wins()
    {
        NcViewerMachineModel Model(string node) => new(node, $@"C:\models\{node}.stl");
        var machines = new[]
        {
            Machine("m-15", "15", "haas-vf-3ss", [Model("table")]),
            Machine("m-10", "10", "haas-vf-3ss", [Model("base")]),
            Machine("m-14", "14", "mazak-variaxis-i-500", [Model("trunnion")]),
            Machine("m-20", "20", "okuma-genos-l200e-m", []),
            Machine("m-30", "30", null, [Model("ignored")])
        };

        var byNumber = NcViewerScenes.MachineModels(machines, assignedMachineId: null);
        var assigned = NcViewerScenes.MachineModels(machines, assignedMachineId: "m-15");

        Assert.Equal(["haas-vf-3ss", "mazak-variaxis-i-500"], byNumber.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("base", Assert.Single(byNumber["haas-vf-3ss"]).Node);
        Assert.Equal("table", Assert.Single(assigned["haas-vf-3ss"]).Node);
    }

    [Fact]
    public void Operation_files_are_the_fixtures_rest_material_and_finished_part_linked_to_that_operation()
    {
        var now = DateTimeOffset.UnixEpoch;
        CaseModelFile File(string id, string? operation, string kind, int order) =>
            new(id, "case-1", operation, kind, "stl", $@"J:\NC\{id}.stl", $"{id}.stl", false, order, 1, now, now);
        var files = new[]
        {
            File("rest", "op-10", "stock", 0),
            File("part", "op-10", "part", 0),
            File("vise-b", "op-10", "fixture", 2),
            File("vise-a", "op-10", "fixture", 1),
            File("other-op", "op-20", "fixture", 0),
            File("case-level", null, "stock", 0)
        };

        var result = NcViewerScenes.OperationFiles(files, "op-10");

        Assert.Equal(["vise-a.stl", "vise-b.stl", "rest.stl", "part.stl"], result.Select(file => file.Name));
        Assert.Equal(["fixture", "fixture", "stock", "part"], result.Select(file => file.Kind));
    }

    [Fact]
    public void Step_meshes_are_written_as_binary_stl_and_stl_files_are_served_as_stored()
    {
        var mesh = new StepModelData(
            [new StepPoint3(0, 0, 0), new StepPoint3(10, 0, 0), new StepPoint3(0, 5, 2.5)],
            [],
            [new StepTriangle3(0, 1, 2)],
            [0, 1, 2]);
        var bytes = NcViewerMeshes.ToBinaryStl(mesh);
        Assert.Equal(84 + 50, bytes.Length);
        Assert.Equal(1u, BitConverter.ToUInt32(bytes, 80));
        Assert.Equal(10f, BitConverter.ToSingle(bytes, 84 + 12 + 12));     // second vertex x
        Assert.Equal(2.5f, BitConverter.ToSingle(bytes, 84 + 12 + 24 + 8)); // third vertex z
        Assert.Equal(mesh.Points.Count, StlMeshLoader.Parse(bytes).Triangles.Count * 3);

        var stl = Path.Combine(folder, "rest.stl");
        System.IO.File.WriteAllBytes(stl, bytes);
        Assert.Equal(bytes, NcViewerMeshes.ReadAsStl(stl));
        var text = Path.Combine(folder, "notes.txt");
        System.IO.File.WriteAllText(text, "x");
        Assert.Throws<InvalidDataException>(() => NcViewerMeshes.ReadAsStl(text));
        Assert.Throws<FileNotFoundException>(() => NcViewerMeshes.ReadAsStl(Path.Combine(folder, "missing.stl")));
        Assert.Throws<InvalidDataException>(() => NcViewerMeshes.ToBinaryStl(mesh with { Triangles = [] }));
    }

    [Fact]
    public void Work_cs_form_accepts_cimatron_values_and_rejects_bad_input()
    {
        var form = new OperationCamSetupViewModel
        {
            WorkOffset = " g54.1  p3 ",
            OriginX = "-12.5",
            OriginY = "40",
            OriginZ = "",
            RotationX = "0",
            RotationY = "-90",
            RotationZ = "180",
            Notes = "  OP20 flipped  "
        };
        Assert.True(form.TryCreate(out var value, out _));
        Assert.Equal("G54.1 P3", value!.WorkOffset);
        Assert.Equal(new PlannerXyz(-12.5, 40, 0), value.OriginMm);
        Assert.Equal(new PlannerXyz(0, -90, 180), value.RotationDeg);
        Assert.Equal("OP20 flipped", value.Notes);
        Assert.Equal("cimatron", value.Source);

        form.WorkOffset = "G43";
        Assert.False(form.TryCreate(out _, out var offsetError));
        Assert.Contains("G54", offsetError, StringComparison.Ordinal);
        form.WorkOffset = "";
        Assert.True(form.TryCreate(out var noOffset, out _));
        Assert.Null(noOffset!.WorkOffset);
        form.RotationZ = "361";
        Assert.False(form.TryCreate(out _, out _));
        form.RotationZ = "0";
        form.OriginX = "1,5";
        Assert.False(form.TryCreate(out _, out _));
    }

    [Fact]
    public async Task Cam_setup_api_creates_with_if_none_match_updates_with_if_match_and_reads_missing_as_null()
    {
        const string Body = """
            {"caseOperationId":"op-10","source":"cimatron","workOffset":"G54","originMm":{"x":1,"y":2,"z":3},
             "rotationDeg":{"x":0,"y":0,"z":90},"notes":null,"version":1,"updatedAt":"2026-10-10T08:00:00Z","updatedBy":"planner"}
            """;
        var handler = new Handler(
            Response(HttpStatusCode.NotFound, """{"error":{"code":"cam_setup_not_found","message":"No Cimatron setup","correlationId":"c","details":[]}}"""),
            Response(HttpStatusCode.Created, Body, "\"case-operation-cam-setup:op-10:v1\""),
            Response(HttpStatusCode.OK, Body.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal), "\"case-operation-cam-setup:op-10:v2\""),
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using var api = new PlannerApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://planner-server:5080/") });
        var write = new CaseOperationCamSetupWrite("cimatron", "G54", new PlannerXyz(1, 2, 3), new PlannerXyz(0, 0, 90), null);

        Assert.Null(await api.GetCaseOperationCamSetupAsync("case-1", "op-10"));
        var created = await api.SaveCaseOperationCamSetupAsync("case-1", "op-10", write, null, "windows-01", 7);
        var updated = await api.SaveCaseOperationCamSetupAsync("case-1", "op-10", write, created.EntityTag, "windows-01", 7);
        await api.DeleteCaseOperationCamSetupAsync("case-1", "op-10", updated.EntityTag, "windows-01", 7);

        Assert.All(handler.Requests, request => Assert.Equal("/api/v1/cases/case-1/operations/op-10/cam-setup", request.Path));
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal("*", handler.Requests[1].IfNoneMatch);
        Assert.Null(handler.Requests[1].IfMatch);
        Assert.Contains("\"originMm\":{\"x\":1,\"y\":2,\"z\":3}", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Equal("\"case-operation-cam-setup:op-10:v1\"", handler.Requests[2].IfMatch);
        Assert.Null(handler.Requests[2].IfNoneMatch);
        Assert.Equal(HttpMethod.Delete, handler.Requests[3].Method);
        Assert.Equal("\"case-operation-cam-setup:op-10:v2\"", handler.Requests[3].IfMatch);
        Assert.All(handler.Requests.Skip(1), request => Assert.Equal("7", request.Generation));
        Assert.Equal(90, created.Value.RotationDeg.Z);
        Assert.Equal(2, updated.Value.Version);
    }

    [Fact]
    public void Viewer_uses_the_gpu_unless_software_rendering_is_forced()
    {
        var previous = Environment.GetEnvironmentVariable("MEIMAD_NC_VIEWER_GPU");
        try
        {
            Environment.SetEnvironmentVariable("MEIMAD_NC_VIEWER_GPU", null);
            Assert.Contains("--ignore-gpu-blocklist", NcViewerWindow.BrowserArguments(), StringComparison.Ordinal);
            Assert.DoesNotContain("--disable-gpu", NcViewerWindow.BrowserArguments(), StringComparison.Ordinal);
            Environment.SetEnvironmentVariable("MEIMAD_NC_VIEWER_GPU", "OFF");
            Assert.Equal("--disable-gpu", NcViewerWindow.BrowserArguments());
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEIMAD_NC_VIEWER_GPU", previous);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private static PlannerMachine Machine(string id, string number, string? viewer, IReadOnlyList<NcViewerMachineModel> models) => new(
        id, number, $"Machine {number}", "mill", null, [], "calendar", true, true, null, null, 0, 1,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, NcViewerMachine: viewer, NcViewerModels: models);

    private static HttpResponseMessage Response(HttpStatusCode status, string body, string? entityTag = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (entityTag is not null) response.Headers.TryAddWithoutValidation("ETag", entityTag);
        return response;
    }

    private sealed record Recorded(HttpMethod Method, string Path, string? IfMatch, string? IfNoneMatch, string? Generation, string Body);

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> queue = new(responses);

        internal List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            static string? Header(HttpRequestMessage message, string name) =>
                message.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
            Requests.Add(new Recorded(
                request.Method,
                request.RequestUri!.AbsolutePath,
                Header(request, "If-Match"),
                Header(request, "If-None-Match"),
                Header(request, "X-Meimad-Edit-Generation"),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return queue.Dequeue();
        }
    }
}
