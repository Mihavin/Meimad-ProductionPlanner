using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class ServerMaintenanceViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? apiClient;
    private string clientId = string.Empty;
    private string userId = string.Empty;
    private string serverAddress = string.Empty;
    private long editGeneration;
    private bool isEditor;
    private bool isBusy;
    private string fromUtc = DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    private string toUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    private string machineId = string.Empty;
    private string reason = string.Empty;
    private string confirmation = string.Empty;
    private string backupFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    private string databaseSizeText = "Connect and refresh to read database storage.";
    private string statusMessage = "Only the listed diagnostic collections can be deleted.";
    private string previewSummary = "Preview the exact rows before deleting.";
    private long expectedTotalRows;

    internal ServerMaintenanceViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, CanRead);
        PreviewCommand = new AsyncCommand(PreviewAsync, CanRead);
        DeleteCommand = new AsyncCommand(DeleteAsync, CanDelete);
        DownloadBackupCommand = new AsyncCommand(DownloadBackupAsync, CanMutate);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MaintenanceDataTypeChoice> DataTypes { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand PreviewCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand DownloadBackupCommand { get; }

    public bool IsBusy { get => isBusy; private set => SetField(ref isBusy, value); }
    public bool IsEditor => isEditor;
    public string DatabaseSizeText { get => databaseSizeText; private set => SetField(ref databaseSizeText, value); }
    public string StatusMessage { get => statusMessage; private set => SetField(ref statusMessage, value); }
    public string PreviewSummary { get => previewSummary; private set => SetField(ref previewSummary, value); }
    public string BackupHttpEndpoint => string.IsNullOrWhiteSpace(serverAddress)
        ? "/api/v1/server-maintenance/backups/download"
        : $"{serverAddress.TrimEnd('/')}/api/v1/server-maintenance/backups/download";
    public string RequiredConfirmation => expectedTotalRows > 0 ? $"DELETE {expectedTotalRows}" : "Preview required";

    public string FromUtc
    {
        get => fromUtc;
        set { if (SetField(ref fromUtc, value)) InvalidatePreview(); }
    }

    public string ToUtc
    {
        get => toUtc;
        set { if (SetField(ref toUtc, value)) InvalidatePreview(); }
    }

    public string MachineId
    {
        get => machineId;
        set { if (SetField(ref machineId, value)) InvalidatePreview(); }
    }

    public string Reason
    {
        get => reason;
        set { if (SetField(ref reason, value)) RaiseCommandStates(); }
    }

    public string Confirmation
    {
        get => confirmation;
        set { if (SetField(ref confirmation, value)) RaiseCommandStates(); }
    }

    public string BackupFolder
    {
        get => backupFolder;
        set => SetField(ref backupFolder, value);
    }

    internal void AttachSession(
        IPlannerApiClient? newApiClient,
        string newClientId,
        string newUserId,
        long newEditGeneration,
        bool newIsEditor,
        string newServerAddress)
    {
        var apiChanged = !ReferenceEquals(apiClient, newApiClient);
        apiClient = newApiClient;
        clientId = newClientId;
        userId = newUserId;
        editGeneration = newEditGeneration;
        isEditor = newIsEditor;
        serverAddress = newServerAddress;
        if (apiChanged)
        {
            DataTypes.Clear();
            DatabaseSizeText = "Connected. Refresh to read database storage.";
            InvalidatePreview();
        }
        OnPropertyChanged(nameof(IsEditor));
        OnPropertyChanged(nameof(BackupHttpEndpoint));
        RaiseCommandStates();
    }

    internal void UpdateConnectionContext(string newServerAddress, string newUserId)
    {
        serverAddress = newServerAddress;
        userId = newUserId;
        OnPropertyChanged(nameof(BackupHttpEndpoint));
    }

    internal async Task RefreshAsync()
    {
        if (!CanRead()) return;
        IsBusy = true;
        try
        {
            var catalog = await apiClient!.GetServerMaintenanceAsync(clientId, userId);
            var selected = DataTypes.Where(item => item.IsSelected).Select(item => item.Type)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var item in DataTypes) item.SelectionChanged -= DataTypeSelectionChanged;
            DataTypes.Clear();
            foreach (var option in catalog.DeletableTypes)
            {
                var choice = new MaintenanceDataTypeChoice(
                    option.Type, option.DisplayName, option.Description,
                    selected.Count == 0 || selected.Contains(option.Type));
                choice.SelectionChanged += DataTypeSelectionChanged;
                DataTypes.Add(choice);
            }
            var database = catalog.Database;
            DatabaseSizeText =
                $"Database {FormatBytes(database.DatabaseFileBytes)} + WAL {FormatBytes(database.WalFileBytes)} " +
                $"+ shared memory {FormatBytes(database.SharedMemoryFileBytes)} = {FormatBytes(database.TotalOnDiskBytes)} on disk. " +
                $"SQLite can reuse about {FormatBytes(database.ReusablePageBytes)}; schema v{database.SchemaVersion}.";
            StatusMessage = "Database storage and protected deletion catalog refreshed.";
            InvalidatePreview();
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    internal async Task PreviewAsync()
    {
        if (!TryBuildPreview(out var request, out var validation))
        {
            StatusMessage = validation;
            return;
        }
        IsBusy = true;
        try
        {
            var preview = await apiClient!.PreviewCollectedDataAsync(request!, clientId, userId);
            expectedTotalRows = preview.TotalRows;
            Confirmation = string.Empty;
            PreviewSummary = preview.TotalRows == 0
                ? "Preview found no matching rows. Nothing can be deleted."
                : $"Preview: {preview.TotalRows:N0} rows — " + string.Join(", ",
                    preview.Items.Select(item => $"{item.DisplayName}: {item.RowCount:N0}"));
            StatusMessage = "Preview complete. Filters are UTC and the end time is excluded.";
            OnPropertyChanged(nameof(RequiredConfirmation));
        }
        catch (Exception exception)
        {
            InvalidatePreview();
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    internal async Task DeleteAsync()
    {
        if (!CanDelete() || !TryBuildPreview(out var preview, out _)) return;
        IsBusy = true;
        try
        {
            var result = await apiClient!.PurgeCollectedDataAsync(
                new(preview!.FromInclusive, preview.ToExclusive, preview.Types, preview.MachineId,
                    expectedTotalRows, Reason.Trim()),
                clientId, userId, editGeneration);
            DatabaseSizeText =
                $"Database {FormatBytes(result.Database.DatabaseFileBytes)}; total on disk {FormatBytes(result.Database.TotalOnDiskBytes)}; " +
                $"reusable pages {FormatBytes(result.Database.ReusablePageBytes)}.";
            StatusMessage = $"Deleted {result.TotalDeletedRows:N0} diagnostic rows after verified backup {result.Backup.FileName}.";
            InvalidatePreview();
            Reason = string.Empty;
        }
        catch (Exception exception)
        {
            InvalidatePreview();
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    internal async Task DownloadBackupAsync()
    {
        if (!CanMutate()) return;
        IsBusy = true;
        try
        {
            var result = await apiClient!.DownloadDatabaseBackupAsync(
                BackupFolder.Trim(), clientId, userId, editGeneration);
            StatusMessage = $"Verified HTTP backup saved to {result.LocalPath} ({FormatBytes(result.ByteLength)}; SHA-256 {result.Sha256}).";
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private bool TryBuildPreview(out CollectedDataPreviewRequest? request, out string error)
    {
        request = null;
        if (!DateTimeOffset.TryParse(FromUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var from)
            || !DateTimeOffset.TryParse(ToUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var to)
            || from >= to)
        {
            error = "Enter a valid UTC range with From earlier than To.";
            return false;
        }
        var types = DataTypes.Where(item => item.IsSelected).Select(item => item.Type).ToArray();
        if (types.Length == 0)
        {
            error = "Select at least one diagnostic data type.";
            return false;
        }
        request = new(from, to, types, string.IsNullOrWhiteSpace(MachineId) ? null : MachineId.Trim());
        error = string.Empty;
        return true;
    }

    private bool CanRead() => apiClient is not null && !string.IsNullOrWhiteSpace(clientId)
        && !string.IsNullOrWhiteSpace(userId) && !IsBusy;
    private bool CanMutate() => CanRead() && isEditor && editGeneration > 0;
    private bool CanDelete() => CanMutate() && expectedTotalRows > 0 && Reason.Trim().Length >= 3
        && string.Equals(Confirmation.Trim(), $"DELETE {expectedTotalRows}", StringComparison.Ordinal);

    private void DataTypeSelectionChanged(object? sender, EventArgs e) => InvalidatePreview();

    private void InvalidatePreview()
    {
        expectedTotalRows = 0;
        confirmation = string.Empty;
        PreviewSummary = "Preview the exact rows before deleting.";
        OnPropertyChanged(nameof(Confirmation));
        OnPropertyChanged(nameof(RequiredConfirmation));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        PreviewCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        DownloadBackupCommand.RaiseCanExecuteChanged();
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var amount = (double)Math.Max(0, value);
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
        return $"{amount:0.##} {units[unit]}";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}

internal sealed class MaintenanceDataTypeChoice : INotifyPropertyChanged
{
    private bool isSelected;

    internal MaintenanceDataTypeChoice(string type, string displayName, string description, bool isSelected)
    {
        Type = type;
        DisplayName = displayName;
        Description = description;
        this.isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal event EventHandler? SelectionChanged;
    public string Type { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
