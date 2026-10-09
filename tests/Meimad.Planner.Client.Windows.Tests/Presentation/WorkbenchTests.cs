using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class WorkbenchTests
{
    [Fact]
    public void Back_preserves_exact_run_program_output_context_and_clear_removes_history()
    {
        var navigation = new WorkbenchNavigation();
        var exact = new ProductionPackageContext("a", "run", "program", "output", "op", "machine", "process", "stamp", 12);
        var context = new WorkbenchContext("case", "recipe-op", "batch", "op", "machine", exact);
        navigation.Open("Preparation", context);
        navigation.Open("Operation", context);
        Assert.True(navigation.Back());
        Assert.Equal("Preparation", navigation.Workspace);
        Assert.Same(exact, navigation.Context!.ExactContext);
        navigation.Clear();
        Assert.Null(navigation.Context);
        Assert.False(navigation.CanGoBack);
    }

    [Fact]
    public async Task Health_response_arriving_after_signout_cannot_repopulate_the_view()
    {
        var api = new HealthApi();
        var model = new OperationalHealthViewModel();
        model.AttachSession(api);
        var pending = model.ReadAsync(false);
        model.AttachSession(null);
        api.Response.SetResult(new(DateTimeOffset.UtcNow, "revision",
            [new("CNC", "machine", "Machine", "ONLINE", "STALE", null, null, null, null, "Old observation")], null));
        await pending;
        Assert.Empty(model.Items);
        Assert.Contains("Sign in", model.Status);
    }

    [Fact]
    public async Task Operation_workbench_passes_exact_selection_and_does_not_invent_current_package()
    {
        var api = new OperationApi();
        var model = new OperationWorkbenchViewModel();
        model.AttachSession(api);
        var exact = new ProductionPackageContext("a", "run", "program", "output", "op", "machine", "process", "stamp", 12);
        await model.OpenAsync(new("case", "recipe-op", "batch", "op", "machine", exact));
        Assert.Equal(exact, api.Context);
        Assert.Null(model.Package);
        Assert.Contains("No valid current package", model.Status);
        model.AttachSession(null);
        Assert.Null(model.Readiness);
        Assert.Null(model.Context);
    }
    private sealed class HealthApi : StubPlannerApiClient, IPlannerApiClient
    {
        internal TaskCompletionSource<OperationalHealthPage> Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<OperationalHealthPage> GetOperationalHealthAsync(string? cursor = null, CancellationToken cancellationToken = default) => Response.Task;
    }
    private sealed class OperationApi : StubPlannerApiClient, IPlannerApiClient
    {
        internal ProductionPackageContext? Context;
        internal List<ProductionPackageContext> ContextRows = [new("a", "run", "program", "output", "op", "machine", "process", "stamp", 12)];
        internal ProductionPackageInfo? CurrentPackage;
        internal byte[] ArtifactBytes = "O0001\nM30\n"u8.ToArray();
        public Task<OperationWorkbenchDetails> GetOperationWorkbenchAsync(string operationId, ProductionPackageContext? context, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationWorkbenchDetails("case", "PN", "Part", "batch", "B1", 12, 1, operationId, 10, "Mill", 1, "case", "recipe-op", "process", null, [], false, DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<ProductionPackageContext>> GetExecutionContextsAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProductionPackageContext>>(ContextRows.ToArray());
        public Task<PlannerProductionReadiness> GetProductionReadinessAsync(string batchOperationId, CancellationToken cancellationToken = default)
            => Task.FromResult(new PlannerProductionReadiness("BLOCKED", false, true, "Missing", [], null, false, []));
        public Task<ProductionPackageInfo?> GetCurrentProductionPackageAsync(string batchOperationId, CancellationToken cancellationToken = default, ProductionPackageContext? context = null)
        { Context = context; return Task.FromResult(CurrentPackage); }
        public Task<HandoffView> GetPackageHandoffsAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(new HandoffView(packageId, 0, true, false, [], []));
        public Task<byte[]> ReadHistoricalPackageArtifactAsync(string packageId, string artifactId, CancellationToken cancellationToken = default)
            => Task.FromResult(ArtifactBytes);
    }
    [Fact]
    public async Task Split_context_requires_selection_and_never_falls_back_after_the_selection_disappears()
    {
        var api = new OperationApi();
        api.ContextRows.Add(api.ContextRows[0] with { MachineAssignmentId = "other-assignment", ProductionRunProgramId = "other-program", ProductionRunOutputId = "other-output" });
        var model = new OperationWorkbenchViewModel(); model.AttachSession(api);
        await model.OpenAsync(new("case", null, "batch", "op", null));
        Assert.Null(model.SelectedContext); Assert.Null(api.Context);
        Assert.Contains("Select the exact", model.Status);
        model.SelectedContext = api.ContextRows[1]; await model.RefreshAsync();
        Assert.Equal("other-output", api.Context!.ProductionRunOutputId);
        api.ContextRows.RemoveAt(1); await model.RefreshAsync();
        Assert.Null(model.SelectedContext); Assert.Null(model.Package);
        Assert.False(model.BuildMeasuredCommand.CanExecute(null));
    }
    [Fact]
    public async Task File_inspection_refuses_corrupt_NC_and_clears_prior_verification_on_context_change()
    {
        var api = new OperationApi();
        var artifact = new ProductionPackageArtifactInfo("file", "RUNNABLE_NC", "main.nc", api.ArtifactBytes.Length,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(api.ArtifactBytes)), "release");
        api.CurrentPackage = new("package", 1, "op", "run", "a", "machine", "release", "tools", null, "CNC_GCODE", false,
            null, null, new string('a', 64), DateTimeOffset.UtcNow, "user", null, true, false, false, [artifact], api.ContextRows[0]);
        var model = new OperationWorkbenchViewModel(); model.AttachSession(api);
        await model.OpenAsync(new("case", null, "batch", "op", "machine", api.ContextRows[0]));
        var opened = 0; model.NcRequested += (_, request) => { opened++; Assert.True(request.ReadOnly); };
        await model.InspectFilesAsync(false); Assert.Contains("Verified 1", model.IntegrityStatus);
        api.ArtifactBytes[0] ^= 1;
        await model.InspectFilesAsync(true); Assert.Equal(0, opened); Assert.Contains("mismatch", model.IntegrityStatus);
        api.ArtifactBytes[0] ^= 1;
        await model.InspectFilesAsync(true); Assert.Equal(1, opened);
        model.SelectedContext = null;
        Assert.Contains("not been verified", model.IntegrityStatus);
    }
    [Fact]
    public async Task Uncertain_build_reuses_exact_request_and_context_and_refusal_preserves_selection()
    {
        var api = new BuildApi();
        var model = new OperationWorkbenchViewModel(); model.AttachSession(api);
        var exact = new ProductionPackageContext("a", "run", "program", "output", "op", "machine", "process", "stamp", 12);
        model.SelectedContext = exact;
        await model.BuildAsync("MEASURED");
        Assert.Contains("uncertain", model.Status);
        await model.BuildAsync("MEASURED");
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(api.Requests[0], api.Requests[1]);
        Assert.Same(exact, model.SelectedContext);
        Assert.Contains("refused", model.Status);
        await model.BuildAsync("MEASURED");
        Assert.NotEqual(api.Requests[1].Id, api.Requests[2].Id);
    }
    private sealed class BuildApi : StubPlannerApiClient, IPlannerApiClient
    {
        internal List<(string? Id, ProductionPackageContext? Context)> Requests = [];
        public Task<ProductionPackageInfo> CreateProductionPackageAsync(string operationId, string clientId, string userId,
            string toolOffsetMode = "MEASURED", CancellationToken cancellationToken = default, ProductionPackageContext? context = null, string? requestId = null)
        {
            Requests.Add((requestId, context));
            if (Requests.Count == 1) throw new System.Net.Http.HttpRequestException("connection dropped");
            throw new PlannerApiException(System.Net.HttpStatusCode.Conflict, "context_changed", "Refresh selection");
        }
    }
}
