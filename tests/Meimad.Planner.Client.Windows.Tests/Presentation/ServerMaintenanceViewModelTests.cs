using System.Net;
using System.Net.Http;
using System.Text;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class ServerMaintenanceViewModelTests
{
    [Fact]
    public async Task Artifact_scan_shows_text_integrity_and_cleanup_requires_configured_retention()
    {
        var handler = new ArtifactHandler();
        using var api = CreateApi(handler);
        var model = new ServerMaintenanceViewModel();
        model.AttachSession(api, "windows", "admin", 1, true, "http://planner:5080");
        await model.ScanArtifactsAsync();
        Assert.Contains("1 requiring investigation", model.ArtifactSummary);
        Assert.Equal("MISSING", Assert.Single(model.ArtifactItems).Integrity);
        Assert.False(model.CollectArtifactOrphansCommand.CanExecute(null));
        handler.Configured = true;
        await model.ScanArtifactsAsync();
        Assert.True(model.CollectArtifactOrphansCommand.CanExecute(null));
        await model.CollectArtifactOrphansAsync();
        Assert.Contains("Removed 1", model.ArtifactSummary);
        Assert.Empty(model.ArtifactItems);
        model.AttachSession(api, "windows", "viewer", 1, false, "http://planner:5080");
        Assert.False(model.ScanArtifactsCommand.CanExecute(null));
        Assert.False(model.CollectArtifactOrphansCommand.CanExecute(null));
    }

    private sealed class ArtifactHandler : HttpMessageHandler
    {
        internal bool Configured;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = request.RequestUri!.AbsolutePath.EndsWith("/cleanup")
                ? """{"completedAt":"2026-10-08T10:00:00Z","deleted":1,"retained":2,"policy":"Retain references"}"""
                : """{"startedAt":"2026-10-08T10:00:00Z","completedAt":"2026-10-08T10:01:00Z","status":"COMPLETE","total":1,"problems":1,"items":[{"store":"production","id":"package-1","state":"ACTIVE","integrity":"MISSING","detail":"Missing file retained for investigation"}],"truncated":false,"retention":"Report only","orphanGraceDays":GRACE}""".Replace("GRACE", Configured ? "7" : "null");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Import_apply_requires_review_and_preserves_preview_after_uncertain_failure()
    {
        var handler = new ImportHandler();
        using var api = CreateApi(handler);
        var viewModel = new ServerMaintenanceViewModel();
        viewModel.AttachSession(api, "windows", "admin", 1, true, "http://127.0.0.1:5080");
        await viewModel.PreviewImportAsync();
        Assert.False(viewModel.ApplyImportCommand.CanExecute(null));
        viewModel.ImportReviewed = true;
        Assert.True(viewModel.ApplyImportCommand.CanExecute(null));
        await viewModel.ApplyImportAsync();
        Assert.Contains("not confirmed", viewModel.ImportSummary);
        Assert.True(viewModel.ApplyImportCommand.CanExecute(null));
        await viewModel.ApplyImportAsync();
        Assert.False(viewModel.ApplyImportCommand.CanExecute(null));
        Assert.Equal(2, handler.Applies);
    }

    private sealed class ImportHandler : HttpMessageHandler
    {
        internal int Applies;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/preview"))
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"previewId":"preview-1","expiresAt":"2026-10-08T18:00:00Z","localRevision":"hash","evidence":{"runId":"preview-1","sourceSystem":"Kitaron","sourceEndpoint":"test","connectionVersion":1,"mappingVersion":2,"sourceFingerprint":"source"},"local":{"changes":[],"totalChanges":0,"unchanged":1,"truncated":false,"destructive":false},"warnings":[]}""", Encoding.UTF8, "application/json") };
            Assert.Contains("\"previewId\":\"preview-1\"", await request.Content!.ReadAsStringAsync(cancellationToken));
            if (++Applies == 1) throw new HttpRequestException("response lost");
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"status":"succeeded","message":"receipt returned"}""", Encoding.UTF8, "application/json") };
        }
    }
    [Fact]
    public async Task Recovery_status_shows_overdue_target_and_blocks_viewer_trigger()
    {
        using var api = CreateApi(new MaintenanceHandler());
        var viewModel = new ServerMaintenanceViewModel();
        viewModel.AttachSession(api, "windows", "viewer", 1, false, "http://planner:5080");
        await viewModel.RefreshAsync();
        Assert.Contains("Recovery point overdue", viewModel.RecoveryStatusText);
        Assert.Contains("4 hours", viewModel.RecoveryStatusText);
        Assert.False(viewModel.CreateRecoverySetCommand.CanExecute(null));
        viewModel.AttachSession(api, "windows", "admin", 1, true, "http://planner:5080");
        await viewModel.RefreshAsync();
        Assert.True(viewModel.CreateRecoverySetCommand.CanExecute(null));
        await viewModel.CreateRecoverySetAsync();
        Assert.Contains("saved and restore-verified", viewModel.StatusMessage);
    }
    [Fact]
    public async Task Preview_is_invalidated_by_filter_changes_and_exact_confirmation_gates_delete()
    {
        var handler = new MaintenanceHandler();
        using var api = CreateApi(handler);
        var viewModel = new ServerMaintenanceViewModel();
        viewModel.AttachSession(api, "windows-1", "planner", 8, true, "http://planner:5080/");
        await viewModel.RefreshAsync();
        await viewModel.PreviewAsync();

        Assert.Equal(3, viewModel.DataTypes.Count);
        Assert.Contains("2 rows", viewModel.PreviewSummary, StringComparison.Ordinal);
        Assert.Equal("DELETE 2", viewModel.RequiredConfirmation);
        viewModel.Reason = "retention cleanup";
        viewModel.Confirmation = "DELETE 2";
        Assert.True(viewModel.DeleteCommand.CanExecute(null));

        viewModel.MachineId = "machine-2";
        Assert.Equal("Preview required", viewModel.RequiredConfirmation);
        Assert.False(viewModel.DeleteCommand.CanExecute(null));

        await viewModel.PreviewAsync();
        viewModel.Reason = "retention cleanup";
        viewModel.Confirmation = "DELETE 2";
        await viewModel.DeleteAsync();
        Assert.Contains("\"machineId\":\"machine-2\"", handler.LastPurgeBody, StringComparison.Ordinal);
        Assert.Contains("verified backup", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_only_session_can_inspect_but_cannot_backup_or_delete()
    {
        var handler = new MaintenanceHandler();
        using var api = CreateApi(handler);
        var viewModel = new ServerMaintenanceViewModel();
        viewModel.AttachSession(api, "windows-2", "viewer", 8, false, "http://planner:5080");
        await viewModel.RefreshAsync();
        await viewModel.PreviewAsync();
        viewModel.Reason = "cleanup";
        viewModel.Confirmation = "DELETE 2";

        Assert.True(viewModel.RefreshCommand.CanExecute(null));
        Assert.True(viewModel.PreviewCommand.CanExecute(null));
        Assert.False(viewModel.DownloadBackupCommand.CanExecute(null));
        Assert.False(viewModel.DeleteCommand.CanExecute(null));
    }

    private static PlannerApiClient CreateApi(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new("http://planner:5080/") });

    private sealed class MaintenanceHandler : HttpMessageHandler
    {
        private const string Database = """{"readAt":"2026-08-28T10:00:00Z","databaseFileBytes":4096,"walFileBytes":0,"sharedMemoryFileBytes":0,"totalOnDiskBytes":4096,"pageSizeBytes":4096,"pageCount":1,"freePageCount":0,"usedPageBytesEstimate":4096,"reusablePageBytes":0,"schemaVersion":61,"collectedData":[]}""";

        internal string LastPurgeBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/recovery-sets", StringComparison.Ordinal))
                return request.Method == HttpMethod.Get
                    ? Json("""{"configured":true,"destination":"test-recovery","scheduleOwner":"External Windows Task Scheduler","lastComplete":null,"ageHours":null,"lastAttemptResult":null,"recoveryPointTargetHours":4,"recoveryTimeTargetHours":4,"recoveryPointOverdue":true}""")
                    : Json("""{"fileName":"test.mprb","setId":"test","snapshotAt":"2026-10-08T10:00:00Z","verifiedAt":"2026-10-08T10:01:00Z","schemaVersion":101,"byteLength":4096,"sha256":"abc","fileCount":3,"externalFolderCount":0}""");
            if (path.EndsWith("/database", StringComparison.Ordinal))
                return Json($$"""{"database":{{Database}},"deletableTypes":[{"type":"cnc_raw_telemetry","displayName":"Raw CNC telemetry","description":"Raw"},{"type":"cnc_state_history","displayName":"Machine state history","description":"State"},{"type":"cnc_connection_events","displayName":"CNC connection events","description":"Connections"}],"backupDownloadMethod":"POST","backupDownloadPath":"/api/v1/server-maintenance/backups/download","deleteRangeSemantics":"half-open"}""");
            if (path.EndsWith("/preview", StringComparison.Ordinal))
                return Json("""{"filter":{"fromInclusive":"2026-07-01T00:00:00Z","toExclusive":"2026-09-01T00:00:00Z","types":["cnc_raw_telemetry","cnc_state_history","cnc_connection_events"],"machineId":null},"items":[{"type":"cnc_raw_telemetry","displayName":"Raw CNC telemetry","rowCount":2,"oldestAt":null,"newestAt":null}],"totalRows":2,"readAt":"2026-08-28T10:00:00Z"}""");
            if (path.EndsWith("/purge", StringComparison.Ordinal))
            {
                LastPurgeBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json($$"""{"filter":{"fromInclusive":"2026-07-01T00:00:00Z","toExclusive":"2026-09-01T00:00:00Z","types":["cnc_raw_telemetry"],"machineId":"machine-2"},"deleted":[],"totalDeletedRows":2,"reason":"retention cleanup","performedBy":"planner","performedAt":"2026-08-28T10:00:00Z","backup":{"fileName":"backup.db","createdAt":"2026-08-28T10:00:00Z","byteLength":4096,"sha256":"ABC","integrityVerified":true,"restoreVerified":true},"database":{{Database}}}""");
            }
            return new(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
