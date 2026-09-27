using System.ComponentModel;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// Setup of the shared network folder every Case link is stored relative to, so any PC opens
/// the same working folder, preview pictures, model files and saved G-code.
/// </summary>
internal sealed class NetworkFolderViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? apiClient;
    private string clientId = string.Empty;
    private long editGeneration;
    private bool isEditor;
    private bool isBusy;
    private int version;
    private string rootPath = string.Empty;
    private string aliasesText = string.Empty;
    private string kitaronCaseFolder = "Meimad Cases";
    private string status = "Connect to the Server to load the network folder.";

    internal NetworkFolderViewModel()
    {
        SaveCommand = new AsyncCommand(SaveAsync, () => isEditor && apiClient is not null && !isBusy && version > 0);
        RefreshCommand = new AsyncCommand(LoadAsync, () => apiClient is not null && !isBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncCommand SaveCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public string RootPath { get => rootPath; set => Set(ref rootPath, value); }

    /// <summary>Drive-letter forms of the same share, one per line, e.g. J:\customers files.</summary>
    public string AliasesText { get => aliasesText; set => Set(ref aliasesText, value); }

    public string KitaronCaseFolder { get => kitaronCaseFolder; set => Set(ref kitaronCaseFolder, value); }

    public string Status { get => status; private set => Set(ref status, value); }

    public bool IsEditor => isEditor;

    internal void AttachSession(IPlannerApiClient? client, string newClientId, long generation, bool editor)
    {
        var changed = !ReferenceEquals(apiClient, client);
        apiClient = client;
        clientId = newClientId;
        editGeneration = generation;
        isEditor = editor;
        OnPropertyChanged(nameof(IsEditor));
        SaveCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        if (client is not null && (changed || version == 0)) _ = LoadAsync();
    }

    internal async Task LoadAsync()
    {
        if (apiClient is null || isBusy) return;
        isBusy = true;
        try
        {
            Apply(await apiClient.GetNetworkFolderAsync());
            Status = string.IsNullOrWhiteSpace(RootPath)
                ? "No network folder is set: Case links are stored exactly as entered."
                : "Case links under this folder are stored relative to it, so every PC opens them.";
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            isBusy = false;
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    internal async Task SaveAsync()
    {
        if (apiClient is null || isBusy || !isEditor) return;
        isBusy = true;
        SaveCommand.RaiseCanExecuteChanged();
        try
        {
            var aliases = AliasesText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var saved = await apiClient.UpdateNetworkFolderAsync(
                new NetworkFolderUpdate(string.IsNullOrWhiteSpace(RootPath) ? null : RootPath.Trim(), aliases,
                    KitaronCaseFolder.Trim(), version),
                clientId, editGeneration);
            Apply(saved);
            Status = $"Network folder saved; {saved.ConvertedLinks ?? 0} stored Case link(s) converted to the relative form.";
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
        finally
        {
            isBusy = false;
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    private void Apply(NetworkFolderSettings value)
    {
        version = value.Version;
        RootPath = value.RootPath ?? string.Empty;
        AliasesText = string.Join(Environment.NewLine, value.Aliases);
        KitaronCaseFolder = value.KitaronCaseFolder;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
