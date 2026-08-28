using System.Net;
using System.Net.Http;
using System.Text;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class ServerMaintenanceViewModelTests
{
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
