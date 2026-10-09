using System.Collections.ObjectModel;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed partial class ServerMaintenanceViewModel
{
    private KitaronImportPreview? importPreview;
    private bool importReviewed;
    private string importSummary = "Run on the Server PC. Preview reads ERP and simulates the import without saving planning changes.";
    public string ImportSummary { get => importSummary; private set => SetField(ref importSummary, value); }
    public bool ImportReviewed { get => importReviewed; set { SetField(ref importReviewed, value); RaiseCommandStates(); } }
    public ObservableCollection<ImportChangeRow> ImportChanges { get; } = [];
    public AsyncCommand PreviewImportCommand { get; }
    public AsyncCommand ApplyImportCommand { get; }

    internal async Task PreviewImportAsync()
    {
        if (!CanMutate()) return;
        IsBusy = true;
        importPreview = null;
        ImportReviewed = false;
        ImportChanges.Clear();
        try
        {
            var preview = await apiClient!.PreviewKitaronImportAsync();
            importPreview = preview;
            foreach (var change in preview.Local.Changes)
                ImportChanges.Add(new(change.Entity, change.Key, change.Action,
                    string.Join("; ", change.Fields.Select(f => $"{f.Field}: {f.Before ?? "(empty)"} -> {f.After ?? "(empty)"}"))));
            ImportSummary = $"{preview.Local.TotalChanges} changes; {preview.Local.Unchanged} unchanged records. " +
                $"{(preview.Local.Destructive ? "Includes removals or status changes. " : "")}" +
                $"{(preview.Local.Truncated ? "Showing the first 500 changes; the total includes additional changes. " : "")}" +
                $"Source: {preview.Evidence.SourceSystem}, {preview.Evidence.SourceEndpoint}. Mapping v{preview.Evidence.MappingVersion}. " +
                $"Expires {preview.ExpiresAt.ToLocalTime():g}. " + string.Join(" ", preview.Warnings);
        }
        catch (Exception exception) { ImportSummary = exception.Message; }
        finally { IsBusy = false; RaiseCommandStates(); }
    }

    internal async Task ApplyImportAsync()
    {
        if (!CanMutate() || importPreview is null || !ImportReviewed) return;
        IsBusy = true;
        RaiseCommandStates();
        try
        {
            var result = await apiClient!.ApplyKitaronImportAsync(importPreview.PreviewId);
            ImportSummary = $"{result.Status}: {result.Message}";
            importPreview = null;
            ImportReviewed = false;
        }
        catch (Exception exception)
        {
            // Keep the exact preview ID on an uncertain response. A fresh token must not silently replace it.
            ImportSummary = $"Import was not confirmed: {exception.Message}. Retry this preview to retrieve its receipt, or create a fresh preview if it was refused.";
        }
        finally { IsBusy = false; RaiseCommandStates(); }
    }
}

internal sealed record ImportChangeRow(string Entity, string Key, string Action, string Fields);
