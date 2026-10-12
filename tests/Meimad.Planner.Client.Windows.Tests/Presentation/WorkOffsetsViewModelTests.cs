using System.Net;
using System.Net.Http;
using System.Text;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>Reports → Work Offsets, the API client routes and the NC viewer card's host answer.</summary>
public sealed class WorkOffsetsViewModelTests
{
    [Fact]
    public async Task The_view_lists_candidates_compares_with_the_current_measurement_and_accepts_with_what_the_user_saw()
    {
        var api = new FakeApi();
        var viewModel = new WorkOffsetsViewModel();
        viewModel.AttachSession(api, "client-1", Editor());
        await viewModel.LoadMachinesAsync();

        Assert.Equal(["All machines", "10 - Haas VF-3"], viewModel.Machines.Select(choice => choice.Label));
        Assert.Equal("REVIEW_REQUIRED", api.ListRequests[^1].Statuses);
        var candidate = Assert.Single(viewModel.Items);
        Assert.Equal("⚠ Review required", candidate.StatusText);
        Assert.Equal("Large change against the current measurement", candidate.ReasonText);
        Assert.Equal("X 130.0000  Y -80.0160  Z 42.6910", candidate.PositionText);

        viewModel.Selected = candidate;
        await WaitForAsync(() => viewModel.CurrentForSelected is not null);
        Assert.Equal("current", viewModel.CurrentForSelected!.WorkOffsetId);
        Assert.Contains("ΔX +4.5980", viewModel.DeltaText, StringComparison.Ordinal);
        Assert.Contains("ΔY 0.0000", viewModel.DeltaText, StringComparison.Ordinal);
        Assert.True(viewModel.AcceptCommand.CanExecute(null));

        viewModel.Comment = " Fixture moved ";
        await viewModel.ReviewAsync(accept: true);
        var review = Assert.Single(api.Reviews);
        Assert.Equal(("candidate", true, "client-1"), (review.Id, review.Accept, review.ClientId));
        Assert.Equal(new WorkOffsetReviewRequestInfo(3, "current", "Fixture moved"), review.Request);
        Assert.Equal("G54 of 10 - Haas VF-3 is now the current measurement.", viewModel.Message);

        viewModel.Status = viewModel.Statuses.Single(choice => choice.Statuses == "UNMATCHED");
        await WaitForAsync(() => api.ListRequests[^1].Statuses == "UNMATCHED");
    }

    [Fact]
    public async Task Without_the_run_operations_permission_the_view_is_read_only_and_a_conflict_is_explained()
    {
        var api = new FakeApi();
        var viewModel = new WorkOffsetsViewModel();
        viewModel.AttachSession(api, "client-1", new EditModeStatus(ClientEditState.Viewer, 1, null, null, DateTimeOffset.UtcNow, 0));
        await viewModel.LoadMachinesAsync();
        viewModel.Selected = viewModel.Items[0];
        Assert.False(viewModel.AcceptCommand.CanExecute(null));
        Assert.False(viewModel.CanReview);
        Assert.Contains("Run operations", viewModel.PermissionText, StringComparison.Ordinal);

        viewModel.AttachSession(api, "client-1", Editor());
        api.Conflict = true;
        await viewModel.ReviewAsync(accept: false);
        Assert.Equal(
            "The measured work offset changed after you loaded it. Changed by Dana at 2026-10-12 09:00. Refresh the work offsets.",
            viewModel.Message);
    }

    [Fact]
    public async Task The_report_program_is_requested_for_the_selected_measurement()
    {
        var api = new FakeApi();
        var viewModel = new WorkOffsetsViewModel();
        viewModel.AttachSession(api, "client-1", Editor());
        await viewModel.LoadMachinesAsync();
        viewModel.Selected = viewModel.Items[0];
        viewModel.ReportOffsets = "G54,G55";
        viewModel.ReportAxes = "XYZBC";
        await viewModel.LoadReportAsync();
        Assert.Equal(("machine-10", "operation-20", "release-2", "G54,G55", "XYZBC"), api.ReportRequest);
        Assert.StartsWith("%", viewModel.ReportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_viewer_card_reads_the_machine_s_measurements_or_explains_why_there_are_none()
    {
        var none = await NcViewerWorkOffsets.LoadAsync(null);
        Assert.Contains("available = False", none.ToString(), StringComparison.Ordinal);

        var api = new FakeApi();
        var machines = new[] { FakeApi.Machine() };
        var context = NcViewerWorkOffsets.For(api, "operation-20", "release-2", "machine-10", machines, null);
        Assert.Equal("10 - Haas VF-3", context.MachineLabel);
        var answer = System.Text.Json.JsonSerializer.SerializeToElement(await NcViewerWorkOffsets.LoadAsync(context));
        Assert.True(answer.GetProperty("available").GetBoolean());
        Assert.Equal(2, answer.GetProperty("records").GetArrayLength());
        Assert.Equal(("machine-10", "operation-20"), api.OperationRequest);

        var anyMachine = NcViewerWorkOffsets.For(api, "operation-20", "release-2", null, machines, null);
        await NcViewerWorkOffsets.LoadAsync(anyMachine);
        Assert.Equal(("VALID,REVIEW_REQUIRED", "operation-20"), (api.ListRequests[^1].Statuses, api.ListRequests[^1].Operation));
    }

    [Fact]
    public async Task The_API_client_uses_the_documented_routes_and_carries_the_client_id()
    {
        var handler = new RecordingHandler();
        using var api = new PlannerApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://planner-server:5080/") });
        handler.Next = """{"machineId":"m","caseOperationId":"o","workOffset":"G54.1 P1","current":[],"history":[]}""";
        await api.GetOperationWorkOffsetsAsync("machine 10", "op/20", "G54.1 P1", 50);
        handler.Next = """{"items":[]}""";
        await api.ListWorkOffsetsAsync("REVIEW_REQUIRED,UNMATCHED", "machine-10", null, 100);
        handler.Next = Record("candidate", "VALID");
        await api.ReviewWorkOffsetAsync("candidate", true, new WorkOffsetReviewRequestInfo(3, "", "ok"), "client-7");
        handler.Next = """{"ncDialect":"HAAS_NGC","fileName":"work-offsets/O01992.nc","text":"%","lines":["%"]}""";
        await api.GetWorkOffsetReportAsync("machine-10", "operation-20", "release-2", "G54", "XYZ");

        Assert.Equal("/api/v1/machines/machine%2010/operations/op%2F20/work-offsets?offset=G54.1%20P1&limit=50", handler.Requests[0].Path);
        Assert.Equal("/api/v1/machine-evidence/work-offsets?status=REVIEW_REQUIRED%2CUNMATCHED&machineId=machine-10&limit=100", handler.Requests[1].Path);
        Assert.Equal("/api/v1/machine-evidence/work-offsets/candidate/accept", handler.Requests[2].Path);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal("client-7", handler.Requests[2].ClientId);
        Assert.Contains("\"version\":3", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("\"expectedCurrentId\":\"\"", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.Equal("/api/v1/machines/machine-10/work-offset-report?operationId=operation-20&releaseId=release-2&offsets=G54&axes=XYZ", handler.Requests[3].Path);
    }

    private static EditModeStatus Editor() => new(ClientEditState.Editor, 1, null, null, DateTimeOffset.UtcNow, 0);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }

    internal static string Record(string id, string status, double x = 125.402) => $$"""
        {"workOffsetId":"{{id}}","machineId":"machine-10","machineNumber":"10","machineName":"Haas VF-3",
         "caseOperationId":"operation-20","operationNumber":20,"operationName":"OP20","ncReleaseId":"release-2",
         "ncIdentityToken":654321,"ncReleaseLabel":"process r1 · Haas r2 · NC 654321","ncReleaseIsCurrent":true,
         "workOffset":"G54","positionMm":{"x":{{x.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"y":-80.016,"z":42.691},
         "rotationDeg":{"a":null,"b":null,"c":null},"units":"MM","source":"DPRNT_TCP","sourceSession":"S1","sourceSequence":null,
         "claimed":{"machine":"10","program":"654321","operation":"20","release":"654321"},"rawMessage":"MEIMAD/OFFSET...",
         "rawMessageHash":"abc","measuredAt":"2026-10-12T06:00:00+00:00","receivedAt":"2026-10-12T06:00:00+00:00",
         "receivedBy":"DPRNT:cnc-10","status":"{{status}}","statusReason":null,"initialStatus":"{{status}}","supersedesId":null,
         "reviewedBy":null,"reviewedAt":null,"reviewComment":null,"version":1}
        """;

    private static WorkOffsetRecordInfo Info(string id, string status, double x, string? reason, int version) =>
        System.Text.Json.JsonSerializer.Deserialize<WorkOffsetRecordInfo>(Record(id, status, x),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))! with
        {
            StatusReason = reason,
            Version = version
        };

    private sealed class FakeApi : StubPlannerApiClient, IPlannerApiClient
    {
        internal List<(string? Statuses, string? Machine, string? Operation)> ListRequests { get; } = [];
        internal List<(string Id, bool Accept, WorkOffsetReviewRequestInfo Request, string ClientId)> Reviews { get; } = [];
        internal (string Machine, string Operation)? OperationRequest { get; private set; }
        internal (string, string, string?, string?, string?)? ReportRequest { get; private set; }
        internal bool Conflict { get; set; }

        internal static PlannerMachine Machine() => new(
            "machine-10", "10", "Haas VF-3", "mill", null, [], "calendar", true, true, null, null, 0, 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        public Task<IReadOnlyList<PlannerMachine>> ListMachinesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerMachine>>([Machine()]);

        public Task<IReadOnlyList<WorkOffsetRecordInfo>> ListWorkOffsetsAsync(
            string? statuses = null, string? machineId = null, string? caseOperationId = null, int? limit = null,
            CancellationToken cancellationToken = default)
        {
            ListRequests.Add((statuses, machineId, caseOperationId));
            return Task.FromResult<IReadOnlyList<WorkOffsetRecordInfo>>(
                [Info("candidate", "REVIEW_REQUIRED", 130.0, "large_change", 3)]);
        }

        public Task<WorkOffsetOperationInfo> GetOperationWorkOffsetsAsync(
            string machineId, string caseOperationId, string? workOffset = null, int? limit = null,
            CancellationToken cancellationToken = default)
        {
            OperationRequest = (machineId, caseOperationId);
            var current = Info("current", "VALID", 125.402, null, 1);
            return Task.FromResult(new WorkOffsetOperationInfo(machineId, caseOperationId, workOffset,
                [current], [Info("candidate", "REVIEW_REQUIRED", 130.0, "large_change", 3), current]));
        }

        public Task<WorkOffsetRecordInfo> ReviewWorkOffsetAsync(
            string workOffsetId, bool accept, WorkOffsetReviewRequestInfo request, string clientId,
            CancellationToken cancellationToken = default)
        {
            if (Conflict)
                throw new PlannerApiException(HttpStatusCode.Conflict, "edit_conflict",
                    "The measured work offset changed after you loaded it.",
                    conflict: new PlannerConflict("measured work offset", "Dana",
                        new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 12, 9, 0, 0))),
                        "Refresh the work offsets."));
            Reviews.Add((workOffsetId, accept, request, clientId));
            return Task.FromResult(Info(workOffsetId, accept ? "VALID" : "REJECTED", 130.0, "large_change", 4));
        }

        public Task<WorkOffsetReportInfo> GetWorkOffsetReportAsync(
            string machineId, string caseOperationId, string? releaseId, string? offsets, string? axes,
            CancellationToken cancellationToken = default)
        {
            ReportRequest = (machineId, caseOperationId, releaseId, offsets, axes);
            return Task.FromResult(new WorkOffsetReportInfo("HAAS_NGC", "work-offsets/O01992.nc", "%\r\nO01992", ["%", "O01992"]));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal string Next { get; set; } = "{}";
        internal List<(HttpMethod Method, string Path, string? ClientId, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery,
                request.Headers.TryGetValues("X-Meimad-Client-Id", out var values) ? values.Single() : null, body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Next, Encoding.UTF8, "application/json") };
        }
    }
}
