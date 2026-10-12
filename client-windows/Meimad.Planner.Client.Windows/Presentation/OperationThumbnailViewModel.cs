using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// The TV Dashboard picture of the selected Case Operation (Server schema v118, owner request 2026-10-12). The TV shows
/// the Case picture first, then the Operation's package thumbnail (from the Cimatron simulation package import), then its
/// snapshot, then a placeholder with the Part Number. Here the planner stores the snapshot (an image file, the Case
/// picture or the 3D view) and removes either thumbnail; each change carries the version it was based on.
/// </summary>
internal sealed class OperationThumbnailViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? apiClient;
    private string clientId = string.Empty;
    private bool isEditor;
    private bool isBusy;
    private string? caseId;
    private string? caseOperationId;
    private OperationThumbnailList? thumbnails;
    private BitmapImage? preview;
    private string statusMessage = string.Empty;

    internal OperationThumbnailViewModel()
    {
        RemoveSnapshotCommand = new AsyncCommand(() => RemoveAsync(OperationThumbnailSources.Snapshot),
            () => CanEdit && Find(OperationThumbnailSources.Snapshot) is not null);
        RemovePackageCommand = new AsyncCommand(() => RemoveAsync(OperationThumbnailSources.Package),
            () => CanEdit && Find(OperationThumbnailSources.Package) is not null);
        UseCasePictureCommand = new AsyncCommand(UseCasePictureAsync, () => CanEdit);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncCommand RemoveSnapshotCommand { get; }

    public AsyncCommand RemovePackageCommand { get; }

    public AsyncCommand UseCasePictureCommand { get; }

    public bool HasOperation => caseId is not null && caseOperationId is not null;

    /// <summary>A signed-in user who may edit Cases, with an Operation selected and nothing in progress.</summary>
    public bool CanEdit => apiClient is not null && isEditor && HasOperation && !IsBusy;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value))
            {
                OnPropertyChanged(nameof(CanEdit));
                RaiseCommands();
            }
        }
    }

    public string StatusMessage { get => statusMessage; private set => SetField(ref statusMessage, value); }

    /// <summary>Which picture the TV shows for this Operation, in one line.</summary>
    public string TvSummary => !HasOperation
        ? "Select an Operation to see its TV picture."
        : thumbnails is null
            ? string.Empty
            : thumbnails.TvSource switch
            {
                "case_picture" => "The TV shows the Case picture (it comes before the Operation thumbnails).",
                OperationThumbnailSources.Package => "The TV shows the package thumbnail.",
                OperationThumbnailSources.Snapshot => "The TV shows the snapshot.",
                _ => "The TV shows a placeholder with the Part Number."
            };

    public string PackageText => Find(OperationThumbnailSources.Package) is { } package
        ? $"Package thumbnail: {package.FileName} ({Kilobytes(package)} KB)"
        : "Package thumbnail: none";

    public string SnapshotText => Find(OperationThumbnailSources.Snapshot) is { } snapshot
        ? $"Snapshot: {snapshot.FileName} ({Kilobytes(snapshot)} KB)"
        : "Snapshot: none";

    /// <summary>The Operation's own thumbnail (the package one first), for a look before the TV shows it.</summary>
    public BitmapImage? Preview { get => preview; private set => SetField(ref preview, value); }

    internal void AttachSession(IPlannerApiClient? client, string newClientId, bool editor)
    {
        apiClient = client;
        clientId = newClientId;
        isEditor = editor;
        OnPropertyChanged(nameof(CanEdit));
        RaiseCommands();
    }

    /// <summary>Loads the thumbnails of the Operation; null ids clear the panel.</summary>
    internal async Task LoadAsync(string? newCaseId, string? operationId)
    {
        caseId = newCaseId;
        caseOperationId = operationId;
        thumbnails = null;
        Preview = null;
        StatusMessage = string.Empty;
        RaiseState();
        if (apiClient is null || newCaseId is null || operationId is null) return;
        IsBusy = true;
        try
        {
            var list = await apiClient.ListOperationThumbnailsAsync(newCaseId, operationId);
            if (caseOperationId != operationId) return;
            thumbnails = list;
            var shown = Find(OperationThumbnailSources.Package) ?? Find(OperationThumbnailSources.Snapshot);
            var bytes = shown is null ? null : await apiClient.GetOperationThumbnailImageAsync(newCaseId, operationId, shown.Source);
            if (caseOperationId != operationId) return;
            Preview = ToBitmap(bytes);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
        }
        finally
        {
            IsBusy = false;
            RaiseState();
        }
    }

    /// <summary>
    /// Stores <paramref name="bytes"/> as the Operation's snapshot (replacing the one that was loaded, if any).
    /// <paramref name="reference"/> records where it came from (a file, the Case picture or the 3D view).
    /// </summary>
    internal async Task<bool> UploadSnapshotAsync(byte[] bytes, string fileName, string? reference)
    {
        if (!CanEdit || caseId is null || caseOperationId is null)
        {
            StatusMessage = "Select an Operation of a saved Case before storing its TV picture.";
            return false;
        }
        var operationId = caseOperationId;
        IsBusy = true;
        try
        {
            await apiClient!.UploadOperationThumbnailAsync(caseId, operationId, OperationThumbnailSources.Snapshot, fileName, bytes,
                Find(OperationThumbnailSources.Snapshot)?.EntityTag, clientId, reference);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
        await LoadAsync(caseId, operationId);
        StatusMessage = "The snapshot is stored on the Server for the TV Dashboard.";
        return true;
    }

    private async Task UseCasePictureAsync()
    {
        if (!CanEdit || caseId is null) return;
        byte[]? bytes;
        try
        {
            bytes = await apiClient!.GetCasePreviewAsync(caseId);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
            return;
        }
        if (bytes is null || bytes.Length == 0)
        {
            StatusMessage = "The Case has no picture to use.";
            return;
        }
        await UploadSnapshotAsync(bytes, "case-picture.png", "Case picture");
    }

    private async Task RemoveAsync(string source)
    {
        if (!CanEdit || caseId is null || caseOperationId is null || Find(source) is not { } current) return;
        var operationId = caseOperationId;
        IsBusy = true;
        try
        {
            await apiClient!.DeleteOperationThumbnailAsync(caseId, operationId, source, current.EntityTag, clientId);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
            return;
        }
        finally
        {
            IsBusy = false;
        }
        await LoadAsync(caseId, operationId);
        StatusMessage = source == OperationThumbnailSources.Package
            ? "The package thumbnail was removed."
            : "The snapshot was removed.";
    }

    private OperationThumbnailInfo? Find(string source) =>
        thumbnails?.Items.FirstOrDefault(item => string.Equals(item.Source, source, StringComparison.Ordinal));

    private static string Kilobytes(OperationThumbnailInfo item) =>
        Math.Max(1, item.SizeBytes / 1024).ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    private void RaiseState()
    {
        OnPropertyChanged(nameof(HasOperation));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(TvSummary));
        OnPropertyChanged(nameof(PackageText));
        OnPropertyChanged(nameof(SnapshotText));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        RemoveSnapshotCommand.RaiseCanExecuteChanged();
        RemovePackageCommand.RaiseCanExecuteChanged();
        UseCasePictureCommand.RaiseCanExecuteChanged();
    }

    private static BitmapImage? ToBitmap(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is PlannerApiException or PlannerProtocolException or HttpRequestException or TaskCanceledException or NotSupportedException;

    private static string Friendly(Exception exception) => exception is NotSupportedException
        ? "This Server does not support Operation thumbnails yet; update the Server."
        : exception.Message;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
