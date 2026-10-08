using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class PreparationQueueViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? api;
    private string clientId = string.Empty;
    private string userId = string.Empty;
    private bool isBusy;
    private PreparationQueueItem? selected;
    private string status;
    private readonly Dictionary<string, (string RequestId, PreparationQueueItem Item)> pendingPackages = [];

    internal PreparationQueueViewModel(string stage, string title, string description)
    {
        Stage = stage;
        Title = title;
        Description = description;
        // The title stays as written so the status translates through the catalog.
        status = $"Connect to view {title}.";
        RefreshCommand = new AsyncCommand(RefreshAsync, () => api is not null && !isBusy);
        OpenCaseCommand = new AsyncCommand(() => RequestActionAsync("OPEN_CASE"), CanUseSelected);
        OpenOperationCommand = new AsyncCommand(() => RequestActionAsync("OPEN_OPERATION"), CanUseSelected);
        UploadGCodeCommand = new AsyncCommand(() => RequestActionAsync("UPLOAD_GCODE"),
            () => CanUseSelected() && Stage == "PROGRAMMING_PENDING");
        OpenToolTableCommand = new AsyncCommand(OpenToolTableAsync,
            () => CanUseSelected() && Stage == "TOOL_PREPARATION_PENDING" && Selected?.ToolTableReleaseId is not null);
        ViewToolTableFileCommand = new AsyncCommand(ViewToolTableFileAsync,
            () => CanUseSelected() && Stage == "TOOL_PREPARATION_PENDING" && Selected?.ToolTableReleaseId is not null);
        // Every queue (NC Creator, Tool Room, Setup) can open the operation's NC release in the viewer.
        ViewNcFileCommand = new AsyncCommand(ViewNcFileAsync,
            () => CanUseSelected() && Selected?.GCodeReleaseId is not null);
        CreateProductionPackageCommand = new AsyncCommand(CreateProductionPackageAsync,
            () => CanUseSelected() && Stage == "TOOL_PREPARATION_PENDING");
        CreateManualOffsetProductionPackageCommand = new AsyncCommand(CreateManualOffsetProductionPackageAsync,
            () => CanUseSelected() && Stage == "TOOL_PREPARATION_PENDING");
        OpenProductionPackageCommand = new AsyncCommand(OpenProductionPackageAsync,
            () => CanUseSelected() && Stage == "SETUP_PENDING");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal event EventHandler<PreparationQueueActionRequest>? ActionRequested;
    public string Stage { get; }
    public string Title { get; }
    public string Description { get; }
    public ObservableCollection<PreparationQueueItem> Items { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand OpenCaseCommand { get; }
    public AsyncCommand OpenOperationCommand { get; }
    public AsyncCommand UploadGCodeCommand { get; }
    public AsyncCommand OpenToolTableCommand { get; }
    public AsyncCommand ViewToolTableFileCommand { get; }
    public AsyncCommand ViewNcFileCommand { get; }
    public AsyncCommand CreateProductionPackageCommand { get; }
    public AsyncCommand CreateManualOffsetProductionPackageCommand { get; }
    public AsyncCommand OpenProductionPackageCommand { get; }

    public PreparationQueueItem? Selected
    {
        get => selected;
        set
        {
            if (Set(ref selected, value)) RaiseActionStates();
        }
    }

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    internal void AttachSession(IPlannerApiClient? client, string? activeClientId = null, string? activeUserId = null)
    {
        if (!ReferenceEquals(api, client) || userId != (activeUserId ?? string.Empty)) pendingPackages.Clear();
        api = client;
        clientId = activeClientId ?? string.Empty;
        userId = activeUserId ?? string.Empty;
        RefreshCommand.RaiseCanExecuteChanged();
        RaiseActionStates();
        if (api is not null) _ = RefreshAsync();
    }

    private bool CanUseSelected() => api is not null && !isBusy && Selected is not null;

    private Task RequestActionAsync(string kind)
    {
        if (Selected is { } item)
        {
            if (kind == "UPLOAD_GCODE") item = item with
            {
                CaseId = item.RecipeCaseId ?? item.CaseId,
                CaseOperationId = item.RecipeCaseOperationId ?? item.CaseOperationId
            };
            ActionRequested?.Invoke(this, new(kind, item, null));
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens the editable tool table: the released tool rows merged with the latest Tool Room
    /// measurements, shapes and components. Saving appends a version on the Server; the
    /// Production Package (Offset Loader) uses the latest version.
    /// </summary>
    internal async Task OpenToolTableAsync()
    {
        if (api is not { } client || Selected is not { } item) return;
        await RunActionAsync(async () =>
        {
            var preparation = await client.GetToolPreparationAsync(item.BatchOperationId, context: item.Context);
            var editor = new ToolPreparation.ToolPreparationViewModel(client, clientId, userId, preparation, item.Context);
            ActionRequested?.Invoke(this, new("OPEN_TOOL_PREPARATION", item, editor));
            Status = preparation.Version == 0
                ? "Tool table opened; no measurements were saved yet."
                : "Tool table opened with the latest saved measurements.";
        });
    }

    /// <summary>Shows the released Tool Table file itself, read-only, as the postprocessor produced it.</summary>
    private async Task ViewToolTableFileAsync()
    {
        if (api is null || Selected?.CaseId is null || Selected.CaseOperationId is null
            || Selected.ToolTableReleaseId is null) return;
        await RunActionAsync(async () =>
        {
            var bytes = await api.ReadToolTableFileAsync(
                Selected.RecipeCaseId ?? Selected.CaseId, Selected.RecipeCaseOperationId ?? Selected.CaseOperationId, Selected.ToolTableReleaseId);
            ActionRequested?.Invoke(this, new("OPEN_TOOL_TABLE", Selected, bytes));
            Status = "Current Tool Table opened from its immutable Server release.";
        });
    }

    private async Task ViewNcFileAsync()
    {
        if (api is not { } client
            || Selected is not { CaseId: { } caseId, CaseOperationId: { } operationId, GCodeReleaseId: { } releaseId } item)
        {
            return;
        }
        await RunActionAsync(async () =>
        {
            var request = await NcViewer.NcViewerRequests.ForReleaseAsync(
                client, item.RecipeCaseId ?? caseId, item.RecipeCaseOperationId ?? operationId, releaseId, item.MachineId,
                $"{item.PartText} · {item.OperationText} · {item.MachineText}",
                item.BatchOperationId, context: item.Context);
            ActionRequested?.Invoke(this, new("VIEW_NC_READ_ONLY", item, request));
            Status = "NC release opened read-only in the NC viewer.";
        });
    }

    private async Task CreateProductionPackageAsync()
        => await CreateProductionPackageAsync("MEASURED");

    private async Task CreateManualOffsetProductionPackageAsync()
        => await CreateProductionPackageAsync("MANUAL_DUMMY");

    private async Task CreateProductionPackageAsync(string toolOffsetMode)
    {
        if (api is not { } client || Selected is not { } item) return;
        await RunActionAsync(async () =>
        {
            var key = $"{item.RowKey}/{toolOffsetMode}";
            if (!pendingPackages.TryGetValue(key, out var pending))
                pendingPackages[key] = pending = (Guid.NewGuid().ToString("N"), item);
            ProductionPackageInfo package;
            try
            {
                package = await client.CreateProductionPackageAsync(
                    item.BatchOperationId, clientId, userId, toolOffsetMode,
                    context: pending.Item.Context, requestId: pending.RequestId);
                pendingPackages.Remove(key);
            }
            catch (PlannerApiException exception) when ((int)exception.StatusCode is >= 400 and < 500
                && (int)exception.StatusCode is not (408 or 429))
            {
                // A definite refusal is not an uncertain publication. A later deliberate
                // invocation may use the refreshed context and a new request key.
                pendingPackages.Remove(key);
                throw;
            }
            ActionRequested?.Invoke(this, new("PRODUCTION_PACKAGE_CREATED", item, package));
            Status = package.ToolOffsetMode == "MANUAL_DUMMY"
                ? $"Production Package #{package.PackageNumber} created with a verification-only Offset Loader. Setupist must enter real tool offsets manually."
                : $"Production Package #{package.PackageNumber} is available. Refresh the queue to check the current package.";
        });
        var resultMessage = Status;
        await RefreshAsync();
        if (pendingPackages.Count == 0) Status = resultMessage;
    }

    private async Task OpenProductionPackageAsync()
    {
        if (api is not { } client || Selected is not { } item) return;
        await RunActionAsync(async () =>
        {
            var package = await client.GetCurrentProductionPackageAsync(item.BatchOperationId, context: item.Context)
                ?? throw new InvalidOperationException("No current valid Production Package exists.");
            ActionRequested?.Invoke(this, new("OPEN_PRODUCTION_PACKAGE", item, package));
            Status = $"Opened current Production Package #{package.PackageNumber}. No workflow state changed.";
        });
    }

    internal async Task ExportCurrentProductionPackageAsync(
        ProductionPackageInfo package,
        string selectedDirectory,
        CancellationToken cancellationToken = default)
    {
        if (api is null) throw new InvalidOperationException("Connect to the Server first.");
        // Re-fetch immediately before exporting: the snapshot passed in can be several seconds
        // stale (folder-picker dialog time), and if a newer Production Package has superseded it
        // since, every artifact ID below would 404 and no file would be written at all.
        package = await api.GetCurrentProductionPackageAsync(package.BatchOperationId, cancellationToken, package.Context)
            ?? throw new InvalidOperationException("No current valid Production Package exists.");
        var root = Path.GetFullPath(selectedDirectory).TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(root);
        foreach (var artifact in package.Artifacts)
        {
            var relative = artifact.LogicalPath.Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("A package artifact path escaped the selected export folder.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var bytes = await api.ReadProductionPackageArtifactAsync(
                package.BatchOperationId, artifact.ArtifactId, cancellationToken, package.Context);
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Package artifact '{artifact.LogicalPath}' failed checksum verification.");
            await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
        }
        Status = $"Exported Production Package #{package.PackageNumber}. No workflow state changed.";
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        if (isBusy) return;
        isBusy = true;
        RaiseActionStates();
        try { await action(); }
        catch (Exception exception) { Status = exception.Message; }
        finally { isBusy = false; RaiseActionStates(); }
    }

    private void RaiseActionStates()
    {
        OpenCaseCommand.RaiseCanExecuteChanged();
        OpenOperationCommand.RaiseCanExecuteChanged();
        UploadGCodeCommand.RaiseCanExecuteChanged();
        OpenToolTableCommand.RaiseCanExecuteChanged();
        ViewNcFileCommand.RaiseCanExecuteChanged();
        CreateProductionPackageCommand.RaiseCanExecuteChanged();
        CreateManualOffsetProductionPackageCommand.RaiseCanExecuteChanged();
        OpenProductionPackageCommand.RaiseCanExecuteChanged();
    }

    internal async Task RefreshAsync()
    {
        if (api is null || isBusy) return;
        isBusy = true;
        RefreshCommand.RaiseCanExecuteChanged();
        var selectedId = Selected?.RowKey;
        try
        {
            var values = (await api.ListPreparationQueueAsync(Stage)).ToList();
            // A successful publication can move a row to Setup before its response reaches us.
            // Keep an explicitly unconfirmed row actionable until its original request resolves.
            foreach (var pending in pendingPackages.Values.DistinctBy(value => value.Item.RowKey))
            {
                values.RemoveAll(value => value.RowKey == pending.Item.RowKey);
                values.Add(pending.Item with { ReadinessFacts = [.. pending.Item.ReadinessFacts,
                    new("packageRequest", "Package request", "PENDING",
                        "Result not confirmed. Retry package creation to recover the result.", false)] });
            }
            MergeItems(values);
            Selected = selectedId is null
                ? null
                : Items.FirstOrDefault(value => value.RowKey == selectedId);
            Status = pendingPackages.Count > 0
                ? "A package result is unconfirmed. Retry its original creation action to recover the result."
                : Items.Count == 0
                ? "No operations are waiting at this preparation gate."
                : $"{Items.Count} operation(s) waiting at this preparation gate.";
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            isBusy = false;
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    // Updates Items in place (Move/Replace/Insert/Remove) instead of Clear()+re-Add so an
    // unrelated row's DataGridRow container survives a poll tick — a Clear() raises a Reset
    // notification that tears down every row, which silently closes any open right-click
    // context menu and drops the current selection every 5 seconds.
    private void MergeItems(IReadOnlyList<PreparationQueueItem> values)
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!values.Any(value => value.RowKey == Items[i].RowKey))
                Items.RemoveAt(i);
        }
        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            var existingIndex = IndexOf(value.RowKey);
            if (existingIndex < 0)
            {
                Items.Insert(Math.Min(i, Items.Count), value);
            }
            else
            {
                if (existingIndex != i) Items.Move(existingIndex, i);
                if (!ContentEquals(Items[i], value)) Items[i] = value;
            }
        }
    }

    private int IndexOf(string batchOperationId)
    {
        for (var i = 0; i < Items.Count; i++)
            if (Items[i].RowKey == batchOperationId) return i;
        return -1;
    }

    private static bool ContentEquals(PreparationQueueItem a, PreparationQueueItem b)
        => a == b with { ReadinessFacts = a.ReadinessFacts }
           && a.ReadinessFacts.SequenceEqual(b.ReadinessFacts);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new(property));
        return true;
    }
}

internal sealed record PreparationQueueActionRequest(
    string Action,
    PreparationQueueItem Item,
    object? Payload);
