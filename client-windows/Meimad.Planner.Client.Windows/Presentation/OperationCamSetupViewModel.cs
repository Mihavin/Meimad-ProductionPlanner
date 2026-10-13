using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// The Cimatron setup of the selected Case Operation (Server schema v113): the Work CS of its NC
/// programs as Cimatron's UCS, in the part-model coordinates of the exported files, and the
/// fixture and rest-material files (Case model files linked to the Operation). Cimatron's own
/// .elt files are never read; Cimatron writes no Work CS file, so the planner types it from the
/// UCS dialog. The NC viewer places the files through this Work CS.
/// </summary>
internal sealed class OperationCamSetupViewModel : INotifyPropertyChanged
{
    internal static readonly IReadOnlyList<string> SourceOptions = ["cimatron", "manual"];
    private static readonly Regex WorkOffsetPattern = new(@"^(G5[4-9](\.1 ?P\d{1,3})?|G154 ?P\d{1,3}|G110|G1[1-2]\d)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private IPlannerApiClient? apiClient;
    private string clientId = string.Empty;
    private long editGeneration;
    private bool isEditor;
    private bool isBusy;
    private string? caseId;
    private string? caseOperationId;
    private OperationCamSourceResource? camSource;
    private string? entityTag;
    private string statusMessage = string.Empty;
    private string source = "cimatron";
    private string workOffset = "G54";
    private string originX = "0";
    private string originY = "0";
    private string originZ = "0";
    private string rotationX = "0";
    private string rotationY = "0";
    private string rotationZ = "0";
    private string notes = string.Empty;
    private string savedSummary = string.Empty;
    private CamSetupFileRow? selectedFile;
    private int camSetupVersion;
    private SimulationPackageImport? currentPackage;
    private string packageSummary = string.Empty;

    internal OperationCamSetupViewModel()
    {
        SaveCommand = new AsyncCommand(SaveAsync, () => CanEdit && HasOperation);
        ClearCommand = new AsyncCommand(ClearAsync, () => CanEdit && entityTag is not null);
        RemoveFileCommand = new AsyncCommand(RemoveFileAsync, () => CanEdit && SelectedFile is not null);
        ToggleKindCommand = new AsyncCommand(ToggleKindAsync, () => CanEdit && SelectedFile is { Kind: "fixture" or "stock" });
        FindSimulationDataCommand = new AsyncCommand(() => FindSimulationDataAsync(LastNcFilePath, null, automatic: false),
            () => apiClient is not null && HasOperation && !IsSearching);
        CancelSearchCommand = new AsyncCommand(CancelSearchAsync, () => IsSearching);
        LinkSuggestionsCommand = new AsyncCommand(LinkSuggestionsAsync, () => !IsSearching && Suggestions.Any(row => row.IsSelected)
            && (CanEdit || Suggestions.All(row => !row.IsSelected || row.Kind == "tool-table")));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Called after the Operation's linked files change, so the 3D Models tab reloads.</summary>
    internal Func<Task>? FilesChanged { get; set; }

    /// <summary>Called after a Cimatron package was imported (the release form takes its NC and tool table).</summary>
    internal Func<SimulationPackageImport, Task>? PackageImported { get; set; }

    /// <summary>The Operation's current Cimatron package (status imported), or null.</summary>
    public SimulationPackageImport? CurrentPackage
    {
        get => currentPackage;
        private set
        {
            if (!SetField(ref currentPackage, value)) return;
            PackageSummary = value is null
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture,
                    $"Cimatron package {value.NcFileName ?? value.ManifestSha256[..8]} imported {value.ImportedAt.ToLocalTime():yyyy-MM-dd HH:mm} by {value.ImportedBy}; Machine {value.MachineName ?? value.MachineId ?? "?"} ({value.MachineMatchedBy}).");
        }
    }

    /// <summary>Where the package folder dialog starts: the current package's folder, if it is reachable.</summary>
    internal string? SuggestedPackageFolder =>
        CurrentPackage?.PackagePath is { } path && Directory.Exists(path) ? Path.GetDirectoryName(path) : null;

    /// <summary>One line about the current package.</summary>
    public string PackageSummary { get => packageSummary; private set => SetField(ref packageSummary, value); }

    /// <summary>The CAM source of the Operation's last Import from Cimatron, or null.</summary>
    public OperationCamSourceResource? CamSource
    {
        get => camSource;
        private set
        {
            if (!SetField(ref camSource, value)) return;
            OnPropertyChanged(nameof(CamSourceSummary));
            OnPropertyChanged(nameof(CamSourceDetail));
            OnPropertyChanged(nameof(HasCamSource));
        }
    }

    public bool HasCamSource => camSource is not null;

    /// <summary>The source name and the date of the last successful import (specification §4).</summary>
    public string CamSourceSummary => camSource is null
        ? "Not imported from Cimatron yet."
        : string.Create(CultureInfo.InvariantCulture,
            $"Cimatron source: {camSource.DocumentName ?? Path.GetFileName(camSource.DocumentPath)} · last import {camSource.ImportedAt.ToLocalTime():yyyy-MM-dd HH:mm}");

    /// <summary>Paths and identities on demand (tool tip), not on the card itself.</summary>
    public string CamSourceDetail => camSource is null
        ? "Import from Cimatron reads the active Cimatron document and saves its path here."
        : string.Join(Environment.NewLine,
            $"CAM file: {camSource.DocumentPath}" + (camSource.Shared ? "" : $" (local path, opens only on {camSource.SourceHost ?? "the CAM PC"})"),
            $"CAM operations: {string.Join(", ", (camSource.Operations ?? []).Select(item => item.Kind == "toolpath" ? $"toolpath {item.Name} (#{item.Id})" : $"{item.Name} (#{item.Id})"))}",
            $"Setup: {camSource.Setup ?? "?"} · postprocessor: {camSource.Postprocessor ?? "?"}",
            $"Imported by {camSource.ImportedBy ?? "?"} from {camSource.SourceHost ?? "?"}; data set: {camSource.PackagePath ?? "?"}");

    /// <summary>The last import answer of this session (for "View package report").</summary>
    internal SimulationPackageImportResult? LastPackageResult { get; private set; }

    /// <summary>
    /// Imports a Cimatron simulation package folder into the Operation. The request carries the CAM setup
    /// version and the current package this view shows, so a change made meanwhile is refused (409) and
    /// shown, never overwritten. A blocked import returns the Server's report with nothing changed.
    /// </summary>
    internal async Task<SimulationPackageImportResult?> ImportPackageAsync(string packageFolder, SimulationPackageChoices? choices = null)
    {
        if (!CanEdit || caseId is null || caseOperationId is null || apiClient is null)
        {
            StatusMessage = "Select an Operation, with an account that may edit Cases, to import a Cimatron package.";
            return null;
        }

        var operationId = caseOperationId;
        IsBusy = true;
        SimulationPackageImportResult? result = null;
        try
        {
            StatusMessage = "The Server is checking the Cimatron package…";
            result = await apiClient.ImportSimulationPackageAsync(
                caseId,
                operationId,
                new SimulationPackageImportRequest(
                    packageFolder,
                    camSetupVersion,
                    CurrentPackage?.ImportId,
                    choices?.StockPath,
                    choices?.MachineId,
                    choices?.RememberMachineMapping ?? false,
                    choices?.OverwriteManualWorkCs ?? false),
                clientId,
                editGeneration);
            LastPackageResult = result;
            var warnings = result.Report.Diagnostics?.Count(item => item.Severity == "warning") ?? 0;
            var errors = result.Report.Diagnostics?.Count(item => item.Severity == "error") ?? 0;
            StatusMessage = result.Outcome switch
            {
                SimulationPackageImportResult.Imported => warnings == 0
                    ? "Cimatron package imported: stock, fixtures and finished part linked, Work CS set from the package."
                    : string.Format(CultureInfo.InvariantCulture,
                        "Cimatron package imported with {0} warning(s): stock, fixtures and finished part linked, Work CS set from the package.", warnings),
                SimulationPackageImportResult.AlreadyImported => "This Cimatron package is already imported for the Operation; nothing changed.",
                _ => string.Format(CultureInfo.InvariantCulture, "The Cimatron package was not imported: {0} blocking problem(s). Nothing was changed.", errors)
            };
        }
        catch (PlannerApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            StatusMessage = $"{exception.Message} The current setup is shown; import the package again if it is still needed.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
        }
        finally
        {
            IsBusy = false;
        }

        if (caseOperationId != operationId) return result;
        if (result is null || result.Outcome == SimulationPackageImportResult.Imported)
        {
            await ReloadSetupAsync();
            await RefreshFilesAsync();
            await LoadPackagesAsync(operationId);
            if (result is not null && FilesChanged is not null) await FilesChanged();
        }

        if (result is { Outcome: SimulationPackageImportResult.Imported, Import: { } imported } && PackageImported is not null)
            await PackageImported(imported);
        return result;
    }

    private async Task LoadPackagesAsync(string operationId)
    {
        if (apiClient is null || caseId is null) return;
        try
        {
            var imports = await apiClient.ListSimulationPackageImportsAsync(caseId, operationId);
            if (caseOperationId == operationId) CurrentPackage = imports.FirstOrDefault(item => item.Status == "imported");
            var source = await apiClient.GetOperationCamSourceAsync(caseId, operationId);
            if (caseOperationId == operationId) CamSource = source;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            // An older Server without packages, or offline: the card works without the package line.
            if (caseOperationId == operationId)
            {
                CurrentPackage = null;
                CamSource = null;
            }
        }
    }

    public ObservableCollection<CamSetupFileRow> Files { get; } = [];

    public IReadOnlyList<string> Sources => SourceOptions;

    public AsyncCommand SaveCommand { get; }

    public AsyncCommand ClearCommand { get; }

    public AsyncCommand RemoveFileCommand { get; }

    public AsyncCommand ToggleKindCommand { get; }

    // ----- AI-assisted search for the Operation's simulation data -----------------------------------

    private bool isSearching;
    private string searchStatus = string.Empty;
    private string? searchId;
    private CancellationTokenSource? searchCancel;
    private string? lastAutomaticKey;

    /// <summary>Files the search proposes; the planner ticks what is right and links it.</summary>
    public ObservableCollection<SimulationSuggestionRow> Suggestions { get; } = [];

    public AsyncCommand FindSimulationDataCommand { get; }

    public AsyncCommand CancelSearchCommand { get; }

    public AsyncCommand LinkSuggestionsCommand { get; }

    /// <summary>The NC file chosen for the Operation's release, searched from first.</summary>
    internal string? LastNcFilePath { get; private set; }

    /// <summary>Called with a tool list the planner accepted, for the Release G-code form.</summary>
    internal Action<string>? ToolTableChosen { get; set; }

    public bool IsSearching
    {
        get => isSearching;
        private set
        {
            if (SetField(ref isSearching, value)) RaiseCommands();
        }
    }

    public string SearchStatus { get => searchStatus; private set => SetField(ref searchStatus, value); }

    /// <summary>
    /// Searches the Case Working Folder, the NC file's part folder and the part folders of the approved
    /// documents for stock, rest material, fixtures, the tool list and the part model; the Server's local
    /// AI ranks what the rules find. <paramref name="automatic"/> searches once per Operation and file
    /// (when an NC file is chosen); the button always searches again.
    /// </summary>
    internal async Task FindSimulationDataAsync(string? ncFilePath, string? gCodeReleaseId, bool automatic)
    {
        if (apiClient is null || caseId is null || caseOperationId is null) return;
        if (!string.IsNullOrWhiteSpace(ncFilePath)) LastNcFilePath = ncFilePath;
        var key = $"{caseOperationId}|{ncFilePath ?? gCodeReleaseId}";
        if (automatic && string.Equals(key, lastAutomaticKey, StringComparison.OrdinalIgnoreCase)) return;
        lastAutomaticKey = key;
        await CancelSearchAsync();
        var operationId = caseOperationId;
        var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        searchCancel = cancel;
        IsSearching = true;
        Suggestions.Clear();
        SearchStatus = "Looking for stock, fixtures and the tool list…";
        try
        {
            var status = await apiClient.StartSimulationDataSearchAsync(caseId, operationId, ncFilePath, gCodeReleaseId, cancel.Token);
            searchId = status.SearchId;
            while (status.Status is "QUEUED" or "RUNNING")
            {
                SearchStatus = status.Progress ?? "Searching…";
                await Task.Delay(TimeSpan.FromSeconds(1), cancel.Token);
                status = await apiClient.GetSimulationDataSearchAsync(status.SearchId, cancel.Token);
            }
            if (caseOperationId != operationId) return;
            ShowSearchResult(status);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (caseOperationId == operationId && ReferenceEquals(searchCancel, cancel)) SearchStatus = "Search stopped.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            if (caseOperationId == operationId) SearchStatus = Friendly(exception);
        }
        finally
        {
            if (ReferenceEquals(searchCancel, cancel))
            {
                searchCancel = null;
                searchId = null;
                IsSearching = false;
            }
            cancel.Dispose();
        }
    }

    private void ShowSearchResult(SimulationDataSearchStatus status)
    {
        Suggestions.Clear();
        if (status.Status != "COMPLETED")
        {
            SearchStatus = status.Summary ?? status.ResultCode switch
            {
                "timed_out" => "The search took too long. Try again.",
                "cancelled" => "Search stopped.",
                _ => "The search did not finish."
            };
            return;
        }
        foreach (var candidate in status.Candidates ?? [])
        {
            var row = new SimulationSuggestionRow(candidate);
            row.PropertyChanged += (_, _) => LinkSuggestionsCommand.RaiseCanExecuteChanged();
            Suggestions.Add(row);
        }
        SearchStatus = (status.AiRanked ? "AI: " : "Rules: ") + (status.Summary ?? string.Empty);
        RaiseCommands();
    }

    private async Task CancelSearchAsync()
    {
        var cancel = searchCancel;
        var id = searchId;
        if (cancel is null) return;
        searchCancel = null;
        searchId = null;
        cancel.Cancel();
        IsSearching = false;
        if (id is not null && apiClient is not null)
        {
            try { await apiClient.CancelSimulationDataSearchAsync(id); }
            catch (Exception exception) when (IsExpected(exception)) { /* The Server ends it on its own. */ }
        }
    }

    /// <summary>
    /// Links the ticked stock, rest-material and fixture files to the Operation and a part model to the
    /// Case; a ticked tool list goes to the Release G-code form. Nothing is linked without the tick.
    /// </summary>
    private async Task LinkSuggestionsAsync()
    {
        if (apiClient is null || caseId is null || caseOperationId is null) return;
        var chosen = Suggestions.Where(row => row.IsSelected).ToArray();
        var linked = 0;
        var notes = new List<string>();
        IsBusy = true;
        try
        {
            foreach (var row in chosen.Where(row => row.Kind != "tool-table" && !row.AlreadyLinked))
            {
                if (!CanEditFiles) { notes.Add("Linking files needs an account that may edit Cases."); break; }
                var kind = row.Kind == "part-model" ? "part" : row.Kind;
                await apiClient.AddCaseModelFileAsync(
                    caseId,
                    new CaseModelFileCreate(row.Path, kind, row.Name, row.Kind == "part-model" ? null : caseOperationId),
                    clientId,
                    editGeneration);
                row.MarkLinked();
                linked++;
            }
            if (chosen.FirstOrDefault(row => row.Kind == "tool-table") is { } toolTable)
            {
                ToolTableChosen?.Invoke(toolTable.Path);
                toolTable.IsSelected = false;
                notes.Add($"{toolTable.Name} is now the tool table of the Release G-code form.");
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            notes.Add(Friendly(exception));
        }
        finally
        {
            IsBusy = false;
        }
        StatusMessage = string.Join(" ", new[] { linked > 0 ? $"{linked} file(s) linked." : null }.Concat(notes).Where(text => !string.IsNullOrEmpty(text)));
        if (linked > 0)
        {
            await RefreshFilesAsync();
            if (FilesChanged is not null) await FilesChanged();
        }
        RaiseCommands();
    }

    private bool CanEditFiles => apiClient is not null && isEditor;

    public bool CanEdit => apiClient is not null && isEditor && !IsBusy;

    public bool HasOperation => caseId is not null && caseOperationId is not null;

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

    /// <summary>What is saved on the Server, in one line.</summary>
    public string SavedSummary { get => savedSummary; private set => SetField(ref savedSummary, value); }

    public string Source { get => source; set => SetField(ref source, value); }
    public string WorkOffset { get => workOffset; set => SetField(ref workOffset, value); }
    public string OriginX { get => originX; set => SetField(ref originX, value); }
    public string OriginY { get => originY; set => SetField(ref originY, value); }
    public string OriginZ { get => originZ; set => SetField(ref originZ, value); }
    public string RotationX { get => rotationX; set => SetField(ref rotationX, value); }
    public string RotationY { get => rotationY; set => SetField(ref rotationY, value); }
    public string RotationZ { get => rotationZ; set => SetField(ref rotationZ, value); }
    public string Notes { get => notes; set => SetField(ref notes, value); }

    public CamSetupFileRow? SelectedFile
    {
        get => selectedFile;
        set
        {
            if (SetField(ref selectedFile, value)) RaiseCommands();
        }
    }

    /// <summary>Where the import dialog starts: the folder of a linked file, if any.</summary>
    internal string? SuggestedFolder => Files.Select(file => Path.GetDirectoryName(file.Path)).FirstOrDefault(Directory.Exists);

    internal void AttachSession(IPlannerApiClient? client, string newClientId, long generation, bool editor)
    {
        apiClient = client;
        clientId = newClientId;
        editGeneration = generation;
        isEditor = editor;
        OnPropertyChanged(nameof(CanEdit));
        RaiseCommands();
    }

    /// <summary>Loads the Operation's Cimatron setup; null ids clear the panel.</summary>
    internal async Task LoadAsync(string? newCaseId, string? operationId)
    {
        if (!string.Equals(caseOperationId, operationId, StringComparison.Ordinal))
        {
            await CancelSearchAsync();
            Suggestions.Clear();
            SearchStatus = string.Empty;
            LastNcFilePath = null;
        }
        caseId = newCaseId;
        caseOperationId = operationId;
        entityTag = null;
        camSetupVersion = 0;
        CurrentPackage = null;
        CamSource = null;
        Files.Clear();
        SelectedFile = null;
        ResetFields();
        SavedSummary = string.Empty;
        OnPropertyChanged(nameof(HasOperation));
        if (newCaseId is null || operationId is null || apiClient is null)
        {
            RaiseCommands();
            return;
        }
        IsBusy = true;
        try
        {
            var setup = apiClient.GetCaseOperationCamSetupAsync(newCaseId, operationId);
            var files = apiClient.ListCaseModelFilesAsync(newCaseId);
            await Task.WhenAll(setup, files);
            if (caseOperationId != operationId) return;
            Show(await setup);
            ReplaceFiles(await files, operationId);
            StatusMessage = string.Empty;
            await LoadPackagesAsync(operationId);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reloads only the linked files (after the 3D Models tab changed them).</summary>
    internal async Task RefreshFilesAsync()
    {
        if (apiClient is null || caseId is null || caseOperationId is null) return;
        try
        {
            ReplaceFiles(await apiClient.ListCaseModelFilesAsync(caseId), caseOperationId);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
        }
    }

    /// <summary>
    /// Links Cimatron exports to the Operation: STOCK files (by name or in an NC\STOCK folder) as
    /// rest material, the rest as fixtures. Files already linked to the Operation are skipped.
    /// </summary>
    internal async Task ImportFilesAsync(IReadOnlyList<string> paths, string? kind = null)
    {
        if (!CanEdit || caseId is null || caseOperationId is null || apiClient is null)
        {
            StatusMessage = "Select an Operation, with an account that may edit Cases, to add Cimatron files.";
            return;
        }
        var supported = paths.Where(NcViewerMeshes.IsSupported).ToArray();
        if (supported.Length == 0)
        {
            StatusMessage = "Choose STL or STEP files exported from Cimatron.";
            return;
        }
        IsBusy = true;
        var added = 0;
        try
        {
            foreach (var path in supported)
            {
                if (Files.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                await apiClient.AddCaseModelFileAsync(
                    caseId,
                    new CaseModelFileCreate(path, kind ?? NcViewerScenes.GuessKind(path), Path.GetFileName(path), caseOperationId),
                    clientId,
                    editGeneration);
                added++;
            }
            StatusMessage = added == 0
                ? "Those files are already linked to this Operation."
                : $"{added} Cimatron file{(added == 1 ? "" : "s")} linked to the Operation. Check each file's kind.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshFilesAsync();
        if (added > 0 && FilesChanged is not null) await FilesChanged();
    }

    private async Task SaveAsync()
    {
        if (!CanEdit || caseId is null || caseOperationId is null || apiClient is null) return;
        if (!TryCreate(out var value, out var error))
        {
            StatusMessage = error;
            return;
        }
        IsBusy = true;
        try
        {
            var saved = await apiClient.SaveCaseOperationCamSetupAsync(
                caseId, caseOperationId, value!, entityTag, clientId, editGeneration);
            Show(saved);
            StatusMessage = "Cimatron setup saved by the Server.";
        }
        catch (PlannerApiException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // Someone else changed or created the setup: show theirs, keep nothing silently.
            StatusMessage = $"{exception.Message} The current setup is shown; enter your change again if it is still needed.";
            await ReloadSetupAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearAsync()
    {
        if (!CanEdit || caseId is null || caseOperationId is null || apiClient is null || entityTag is null) return;
        IsBusy = true;
        try
        {
            await apiClient.DeleteCaseOperationCamSetupAsync(caseId, caseOperationId, entityTag, clientId, editGeneration);
            entityTag = null;
            ResetFields();
            SavedSummary = "No Work CS saved.";
            StatusMessage = "Work CS removed. The linked files stay linked.";
        }
        catch (PlannerApiException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            StatusMessage = $"{exception.Message} The current setup is shown.";
            await ReloadSetupAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveFileAsync()
    {
        if (!CanEdit || caseId is null || apiClient is null || SelectedFile is not { } file) return;
        IsBusy = true;
        try
        {
            // The file is unlinked from the Case; the file itself is never touched.
            await apiClient.DeleteCaseModelFileAsync(caseId, file.CaseModelFileId, clientId, editGeneration);
            StatusMessage = $"{file.Label} is no longer linked. The file itself was not changed.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshFilesAsync();
        if (FilesChanged is not null) await FilesChanged();
    }

    private async Task ToggleKindAsync()
    {
        if (!CanEdit || caseId is null || apiClient is null || SelectedFile is not { } file) return;
        var kind = file.Kind == "stock" ? "fixture" : "stock";
        IsBusy = true;
        try
        {
            await apiClient.UpdateCaseModelFileAsync(
                caseId, file.CaseModelFileId, file.Version, new CaseModelFileUpdate(Kind: kind), clientId, editGeneration);
            StatusMessage = $"{file.Label} is now {(kind == "stock" ? "rest material" : "a fixture")}.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshFilesAsync();
        if (FilesChanged is not null) await FilesChanged();
    }

    private async Task ReloadSetupAsync()
    {
        if (apiClient is null || caseId is null || caseOperationId is null) return;
        try
        {
            Show(await apiClient.GetCaseOperationCamSetupAsync(caseId, caseOperationId));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = Friendly(exception);
        }
    }

    internal bool TryCreate(out CaseOperationCamSetupWrite? value, out string error)
    {
        value = null;
        var offset = string.IsNullOrWhiteSpace(WorkOffset) ? null : Regex.Replace(WorkOffset.Trim().ToUpperInvariant(), @"\s+", " ");
        if (offset is not null && !WorkOffsetPattern.IsMatch(offset))
        {
            error = "Work offset must look like G54, G55 … G59, G54.1 P1 or G154 P1.";
            return false;
        }
        var numbers = new double[6];
        string[] texts = [OriginX, OriginY, OriginZ, RotationX, RotationY, RotationZ];
        string[] names = ["Origin X", "Origin Y", "Origin Z", "Rotation X", "Rotation Y", "Rotation Z"];
        for (var index = 0; index < texts.Length; index++)
        {
            if (!double.TryParse(string.IsNullOrWhiteSpace(texts[index]) ? "0" : texts[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[index])
                || !double.IsFinite(numbers[index])
                || (index < 3 ? Math.Abs(numbers[index]) > 100000 : Math.Abs(numbers[index]) > 360))
            {
                error = $"{names[index]} must be a number{(index < 3 ? " up to ±100000 mm" : " from -360 to 360 degrees")}, with a decimal point.";
                return false;
            }
        }
        var note = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
        if (note is { Length: > 500 })
        {
            error = "Notes can have at most 500 characters.";
            return false;
        }
        value = new CaseOperationCamSetupWrite(
            SourceOptions.Contains(Source) ? Source : "cimatron",
            offset,
            new PlannerXyz(numbers[0], numbers[1], numbers[2]),
            new PlannerXyz(numbers[3], numbers[4], numbers[5]),
            note);
        error = string.Empty;
        return true;
    }

    private void Show(CaseOperationCamSetupResource? resource)
    {
        entityTag = resource?.EntityTag;
        camSetupVersion = resource?.Value?.Version ?? 0;
        if (resource?.Value is not { } setup)
        {
            ResetFields();
            SavedSummary = "No Work CS saved. The NC viewer draws the files at their own coordinates.";
            RaiseCommands();
            return;
        }
        Source = setup.Source;
        WorkOffset = setup.WorkOffset ?? string.Empty;
        OriginX = Format(setup.OriginMm.X);
        OriginY = Format(setup.OriginMm.Y);
        OriginZ = Format(setup.OriginMm.Z);
        RotationX = Format(setup.RotationDeg.X);
        RotationY = Format(setup.RotationDeg.Y);
        RotationZ = Format(setup.RotationDeg.Z);
        Notes = setup.Notes ?? string.Empty;
        var by = string.IsNullOrWhiteSpace(setup.UpdatedBy) ? string.Empty : $" by {setup.UpdatedBy}";
        SavedSummary = string.Create(CultureInfo.InvariantCulture,
            $"Saved {setup.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}{by} (version {setup.Version}).");
        RaiseCommands();
    }

    private void ResetFields()
    {
        Source = "cimatron";
        WorkOffset = "G54";
        OriginX = OriginY = OriginZ = "0";
        RotationX = RotationY = RotationZ = "0";
        Notes = string.Empty;
    }

    private void ReplaceFiles(IReadOnlyList<CaseModelFile> files, string operationId)
    {
        var selectedId = SelectedFile?.CaseModelFileId;
        Files.Clear();
        foreach (var file in files
                     .Where(file => file.CaseOperationId == operationId && file.Kind is "fixture" or "stock" or "part")
                     .OrderBy(file => file.Kind switch { "fixture" => 0, "stock" => 1, _ => 2 })
                     .ThenBy(file => file.SortOrder)
                     .ThenBy(file => file.Label, StringComparer.CurrentCultureIgnoreCase))
        {
            Files.Add(new CamSetupFileRow(file));
        }
        SelectedFile = Files.FirstOrDefault(file => file.CaseModelFileId == selectedId);
        OnPropertyChanged(nameof(FilesSummary));
    }

    public string FilesSummary => Files.Count == 0
        ? "No fixture or rest-material files linked to this Operation."
        : string.Create(CultureInfo.InvariantCulture,
            $"{Files.Count(file => file.Kind == "fixture")} fixture file(s), {Files.Count(file => file.Kind == "stock")} rest-material file(s), {Files.Count(file => file.Kind == "part")} finished-part file(s).");

    private void RaiseCommands()
    {
        SaveCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        RemoveFileCommand.RaiseCanExecuteChanged();
        ToggleKindCommand.RaiseCanExecuteChanged();
        FindSimulationDataCommand.RaiseCanExecuteChanged();
        CancelSearchCommand.RaiseCanExecuteChanged();
        LinkSuggestionsCommand.RaiseCanExecuteChanged();
    }

    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static bool IsExpected(Exception exception) =>
        exception is PlannerApiException or PlannerProtocolException or HttpRequestException or TaskCanceledException or NotSupportedException;

    private static string Friendly(Exception exception) => exception is NotSupportedException
        ? "This Server does not support Cimatron setups yet; update the Server."
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

/// <summary>A file the simulation-data search proposes; ticked by default when it is likely and not linked yet.</summary>
internal sealed class SimulationSuggestionRow : INotifyPropertyChanged
{
    private readonly SimulationDataCandidate candidate;
    private bool isSelected;
    private bool alreadyLinked;

    internal SimulationSuggestionRow(SimulationDataCandidate candidate)
    {
        this.candidate = candidate;
        alreadyLinked = candidate.AlreadyLinked;
        isSelected = !alreadyLinked && candidate.Confidence >= 0.6 && candidate.Kind != "part-model";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string Kind => candidate.Kind;
    public string KindLabel => candidate.Kind switch
    {
        "stock" => "Rest material / stock",
        "fixture" => "Fixture",
        "tool-table" => "Tool table",
        "part-model" => "Part model",
        _ => candidate.Kind
    };
    public string Name => candidate.Name;
    public string Path => candidate.Path;
    public string Folder => System.IO.Path.GetDirectoryName(candidate.Path) ?? string.Empty;
    public string Reason => candidate.Reason;
    public string SourceLabel => candidate.Source == "AI" ? "AI" : "Rules";
    public string ConfidenceText => (candidate.Confidence * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
    public string Modified => candidate.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public bool AlreadyLinked => alreadyLinked;
    public string LinkedText => alreadyLinked ? "Linked" : string.Empty;

    internal void MarkLinked()
    {
        alreadyLinked = true;
        IsSelected = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlreadyLinked)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LinkedText)));
    }
}

/// <summary>A fixture or rest-material file linked to the Operation.</summary>
internal sealed class CamSetupFileRow(CaseModelFile file)
{
    public string CaseModelFileId => file.CaseModelFileId;
    public int Version => file.Version;
    public string Kind => file.Kind;
    public string KindLabel => file.Kind switch { "stock" => "Rest material", "part" => "Finished part", _ => "Fixture" };
    public string Label => file.Label;
    public string Path => file.FilePath;
    public string Format => file.Format.ToUpperInvariant();
    // Checked once per load: a network path is not probed on every redraw.
    public bool Exists { get; } = File.Exists(file.FilePath);
    public string FileState => Exists ? "Found" : "Missing";
}
