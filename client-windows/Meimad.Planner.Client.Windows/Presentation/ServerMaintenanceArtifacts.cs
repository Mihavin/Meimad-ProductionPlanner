using System.Collections.ObjectModel;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed partial class ServerMaintenanceViewModel
{
    private string artifactSummary = "Refresh to read the last artifact integrity scan.";
    private bool artifactCleanupConfigured;
    public string ArtifactSummary { get => artifactSummary; private set => SetField(ref artifactSummary, value); }
    public ObservableCollection<ArtifactHealthItem> ArtifactItems { get; } = [];
    public AsyncCommand ScanArtifactsCommand { get; }
    public AsyncCommand CollectArtifactOrphansCommand { get; }

    private async Task RefreshArtifactsAsync()
    {
        try { ShowArtifactHealth(await apiClient!.GetArtifactHealthAsync()); }
        catch (Exception exception) { ArtifactSummary = $"Artifact status unavailable: {exception.Message}"; }
    }

    internal async Task ScanArtifactsAsync()
    {
        if (!CanMutate()) return;
        IsBusy = true;
        RaiseCommandStates();
        try { ShowArtifactHealth(await apiClient!.ScanArtifactsAsync()); }
        catch (Exception exception) { ArtifactSummary = $"Scan was not confirmed: {exception.Message}. Refresh to read the latest completed scan."; }
        finally { IsBusy = false; RaiseCommandStates(); }
    }

    internal async Task CollectArtifactOrphansAsync()
    {
        if (!CanMutate() || !artifactCleanupConfigured) return;
        IsBusy = true;
        RaiseCommandStates();
        try
        {
            var result = await apiClient!.CollectArtifactOrphansAsync();
            ArtifactSummary = $"Removed {result.Deleted} abandoned publications; retained {result.Retained}. {result.Policy} Scan again for current integrity status.";
            ArtifactItems.Clear();
        }
        catch (Exception exception) { ArtifactSummary = $"Cleanup was not confirmed: {exception.Message}. Scan again before retrying."; }
        finally { IsBusy = false; RaiseCommandStates(); }
    }

    private void ShowArtifactHealth(ArtifactHealthReport report)
    {
        ArtifactItems.Clear();
        artifactCleanupConfigured = report.OrphanGraceDays is not null;
        foreach (var item in report.Items) ArtifactItems.Add(item);
        ArtifactSummary = $"{report.Status}: {report.Total} items, {report.Problems} requiring investigation. " +
            $"Last completed: {report.CompletedAt?.ToLocalTime().ToString("g") ?? "not available"}. " +
            (report.Truncated ? "Showing up to 500 items; totals include additional items. " : "") + report.Retention +
            (report.OrphanGraceDays is { } days ? $" Manual orphan cleanup is configured with a {days}-day grace period." : " Orphan cleanup is disabled.");
    }
}
