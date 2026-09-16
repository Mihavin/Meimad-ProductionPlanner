using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>Everything a detached 3D window needs from the session that opened it.</summary>
internal sealed record ModelViewerContext(
    IPlannerApiClient ApiClient,
    string ClientId,
    long EditGeneration,
    bool IsEditor);

internal static class ModelFileKinds
{
    public const string Part = "part";
    public const string Stock = "stock";
    public const string Fixture = "fixture";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Part, Stock, Fixture, Other];

    public static string DisplayName(string kind) => kind switch
    {
        Part => "Part model",
        Stock => "Rest material / stock",
        Fixture => "Fixture",
        _ => "Other"
    };

    /// <summary>Default opacity so stock and fixtures never hide the part.</summary>
    public static double DefaultOpacity(string kind) => kind switch
    {
        Stock => 0.45,
        Fixture => 0.8,
        Other => 0.8,
        _ => 1
    };
}

internal sealed class ModelFileItemViewModel(CaseModelFile file) : INotifyPropertyChanged
{
    private CaseModelFile file = file;
    private bool isVisible = true;
    private string? layerId;
    private string? loadError;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CaseModelFile File
    {
        get => file;
        set
        {
            file = value;
            OnPropertyChanged(string.Empty);
        }
    }

    public string CaseModelFileId => file.CaseModelFileId;
    public string Label => file.Label;
    public string Kind => file.Kind;
    public string KindText => ModelFileKinds.DisplayName(file.Kind);
    public string FormatText => file.Format.ToUpperInvariant();
    public string FilePath => file.FilePath;
    public string? CaseOperationId => file.CaseOperationId;
    public bool IsPrimary => file.IsPrimary;
    public int Version => file.Version;
    public string PrimaryText => file.IsPrimary ? "Primary" : string.Empty;

    public bool IsVisible
    {
        get => isVisible;
        set
        {
            if (isVisible == value) return;
            isVisible = value;
            OnPropertyChanged();
            VisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? LayerId
    {
        get => layerId;
        set { layerId = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLoaded)); OnPropertyChanged(nameof(StatusText)); }
    }

    public string? LoadError
    {
        get => loadError;
        set { loadError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLoadError)); OnPropertyChanged(nameof(StatusText)); }
    }

    public bool IsLoaded => layerId is not null;
    public bool HasLoadError => loadError is not null;
    public string StatusText => loadError ?? (IsLoaded ? "Loaded" : "Not loaded");

    public event EventHandler? VisibilityChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Model files of one Case for the detached 3D window: lists the linked STEP/STL files and lets an
/// editor add, remove, or promote them. Loading geometry into the viewer is the window's job.
/// </summary>
internal sealed class ModelViewerViewModel(ModelViewerContext context, string caseId, string title) : INotifyPropertyChanged
{
    private bool isBusy;
    private string statusMessage = "Loading model files…";
    private ModelFileItemViewModel? selectedFile;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CaseId { get; } = caseId;
    public string Title { get; } = title;
    public ObservableCollection<ModelFileItemViewModel> Files { get; } = [];
    public IReadOnlyList<string> Kinds => ModelFileKinds.All;
    public bool IsEditor => context.IsEditor;

    public bool IsBusy
    {
        get => isBusy;
        private set { isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanEdit)); }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set { statusMessage = value; OnPropertyChanged(); }
    }

    public ModelFileItemViewModel? SelectedFile
    {
        get => selectedFile;
        set { selectedFile = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanEdit)); }
    }

    public bool CanEdit => context.IsEditor && !isBusy;

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var files = await context.ApiClient.ListCaseModelFilesAsync(CaseId);
            Files.Clear();
            foreach (var file in files)
            {
                Files.Add(new ModelFileItemViewModel(file));
            }
            StatusMessage = Files.Count == 0
                ? (context.IsEditor
                    ? "No model files are linked to this Case yet. Add a STEP or STL file."
                    : "No model files are linked to this Case yet. Acquire Edit Mode to add one.")
                : $"{Files.Count} model file(s) linked to this Case.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<ModelFileItemViewModel?> AddAsync(string filePath, string kind, string? caseOperationId)
    {
        if (!CanEdit)
        {
            StatusMessage = "Edit Mode is required to link model files.";
            return null;
        }

        IsBusy = true;
        try
        {
            var created = await context.ApiClient.AddCaseModelFileAsync(
                CaseId,
                new CaseModelFileCreate(filePath, kind, Path.GetFileName(filePath), caseOperationId),
                context.ClientId,
                context.EditGeneration);
            var item = new ModelFileItemViewModel(created);
            if (created.IsPrimary)
            {
                foreach (var other in Files.Where(existing => existing.IsPrimary))
                {
                    other.File = other.File with { IsPrimary = false, Version = other.Version + 1 };
                }
                Files.Insert(0, item);
            }
            else
            {
                Files.Add(item);
            }
            StatusMessage = $"Linked {created.Label} as {ModelFileKinds.DisplayName(created.Kind).ToLowerInvariant()}.";
            return item;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> RemoveAsync(ModelFileItemViewModel item)
    {
        if (!CanEdit)
        {
            StatusMessage = "Edit Mode is required to remove model files.";
            return false;
        }

        IsBusy = true;
        try
        {
            await context.ApiClient.DeleteCaseModelFileAsync(CaseId, item.CaseModelFileId, context.ClientId, context.EditGeneration);
            Files.Remove(item);
            if (ReferenceEquals(SelectedFile, item))
            {
                SelectedFile = null;
            }
            StatusMessage = $"Removed the link to {item.Label}. The file itself was not deleted.";
            return true;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SetPrimaryAsync(ModelFileItemViewModel item)
    {
        if (!CanEdit || item.IsPrimary)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var updated = await context.ApiClient.UpdateCaseModelFileAsync(
                CaseId,
                item.CaseModelFileId,
                item.Version,
                new CaseModelFileUpdate(IsPrimary: true),
                context.ClientId,
                context.EditGeneration);
            await ReloadAfterMutationAsync(updated);
            StatusMessage = $"{updated.Label} is now the primary part model.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAfterMutationAsync(CaseModelFile updated)
    {
        // Other rows changed too (the previous primary was demoted), so refresh from the Server
        // while keeping per-item viewer state (layer id, visibility, load errors).
        var files = await context.ApiClient.ListCaseModelFilesAsync(CaseId);
        var previous = Files.ToDictionary(item => item.CaseModelFileId, StringComparer.Ordinal);
        Files.Clear();
        foreach (var file in files)
        {
            if (previous.TryGetValue(file.CaseModelFileId, out var existing))
            {
                existing.File = file;
                Files.Add(existing);
            }
            else
            {
                Files.Add(new ModelFileItemViewModel(file));
            }
        }
        SelectedFile = Files.FirstOrDefault(item => item.CaseModelFileId == updated.CaseModelFileId);
    }

    private static bool IsExpected(Exception exception) => exception is
        PlannerApiException or PlannerProtocolException or HttpRequestException or TaskCanceledException;

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        TaskCanceledException => "The Server did not respond before the client timeout.",
        HttpRequestException => "The configured Server could not be reached.",
        PlannerApiException api => $"{api.Message} ({api.Code})",
        _ => exception.Message
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
