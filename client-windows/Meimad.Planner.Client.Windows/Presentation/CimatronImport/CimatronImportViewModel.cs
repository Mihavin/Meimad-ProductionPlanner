using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Meimad.Planner.NcEngine;

namespace Meimad.Planner.Client.Windows.Presentation.CimatronImport;

/// <summary>What the import window needs from the Case workspace.</summary>
/// <param name="Register">Registers a package folder with the Server (the Cimatron card's import: versions, reloads).</param>
/// <param name="UseProgram">Hands the Meimad-format program to the Release G-code form (never releases it).</param>
internal sealed record CimatronImportContext(
    IPlannerApiClient Api,
    string CaseId,
    string CaseName,
    string? PartNumber,
    CaseOperation Operation,
    Func<string, SimulationPackageChoices?, Task<SimulationPackageImportResult?>> Register,
    Action<string> UseProgram);

/// <summary>One CAM operation row of the selection tree: a procedure of a toolpath.</summary>
internal sealed class CamProcedureNode(CimatronProcedure procedure, CamToolpathNode toolpath) : INotifyPropertyChanged
{
    private bool isChecked;

    public int Id => procedure.Id;
    public string Name => procedure.Name;
    public bool Calculated => procedure.Calculated;
    public string Label => procedure.Calculated ? procedure.Name : $"{procedure.Name} (not calculated)";
    public CamToolpathNode Toolpath => toolpath;

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            toolpath.ChildChanged();
        }
    }

    internal void Set(bool value)
    {
        if (isChecked == value) return;
        isChecked = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A Cimatron toolpath: checked when all its procedures are, partly when some are.</summary>
internal sealed class CamToolpathNode : INotifyPropertyChanged
{
    private readonly Action changed;

    internal CamToolpathNode(CimatronToolpath toolpath, string setup, Action changed)
    {
        Id = toolpath.Id;
        Name = toolpath.Name;
        Setup = setup;
        this.changed = changed;
        Procedures = new(toolpath.Procedures?.Select(procedure => new CamProcedureNode(procedure, this)) ?? []);
    }

    public int Id { get; }
    public string Name { get; }
    public string Setup { get; }
    public ObservableCollection<CamProcedureNode> Procedures { get; }

    /// <summary>True: every procedure; false: none; null: some.</summary>
    public bool? IsChecked
    {
        get => Procedures.Count == 0 ? false
            : Procedures.All(item => item.IsChecked) ? true
            : Procedures.Any(item => item.IsChecked) ? null : false;
        set
        {
            var target = value != false;
            foreach (var procedure in Procedures) procedure.Set(target);
            ChildChanged();
        }
    }

    public bool IsWhole => Procedures.Count > 0 && Procedures.All(item => item.IsChecked);

    internal void ChildChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        changed();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed record CamSetupNode(string Name, string Label, ObservableCollection<CamToolpathNode> Toolpaths);

/// <summary>
/// Import from Cimatron (specification of 2026-10-13): from the selected Meimad Operation, read the active Cimatron
/// document through the exporter installed with the client, let the user choose CAM operations (grouped by setup), then
/// export them into a new folder of the Operation's G-code structure, convert the main NC to the Meimad format with the
/// Server's formatter (no duplicate markers), and register the data set with the Server, which saves the CAM source.
/// Every step reports in the same window; a failure leaves the last registered data set untouched and removes only the
/// folder this import created.
/// </summary>
internal sealed class CimatronImportViewModel : INotifyPropertyChanged
{
    internal const string GeometryFolderName = "Meimad geometry";
    internal const string PackageFolderName = "cimatron-export";

    private readonly CimatronImportContext context;
    private readonly ICimatronBridge bridge;
    private readonly Func<DateTimeOffset> clock;
    private CancellationTokenSource? running;
    private CimatronListing? listing;
    private OperationCamSourceResource? source;
    private IReadOnlyList<PlannerMachine> machines = [];
    private PlannerPostprocessor? selectedPostprocessor;
    private SimulationPackageMachineCandidate? selectedMachine;
    private string ucs = "MODEL";
    private string documentTitle = "Reading the active Cimatron document…";
    private string documentDetail = "";
    private string statusText = "";
    private string reportText = "";
    private bool isBusy;
    private bool overwriteManualWorkCs;
    private bool needsMachineChoice;
    private bool needsWorkCsConfirmation;
    private string? preparedPackage;
    private string? preparedDataset;
    private string? importedProgram;

    internal CimatronImportViewModel(CimatronImportContext context, ICimatronBridge bridge, Func<DateTimeOffset>? clock = null)
    {
        this.context = context;
        this.bridge = bridge;
        this.clock = clock ?? (() => DateTimeOffset.Now);
        RefreshCommand = new AsyncCommand(LoadAsync, () => !IsBusy);
        ImportCommand = new AsyncCommand(ImportAsync, () => CanImport);
        RetryRegistrationCommand = new AsyncCommand(RetryRegistrationAsync, () => !IsBusy && preparedPackage is not null);
        CancelCommand = new AsyncCommand(() =>
        {
            running?.Cancel();
            return Task.CompletedTask;
        }, () => running is not null);
        TargetTitle = string.Format(CultureInfo.InvariantCulture, "Meimad Operation {0} — {1} ({2})",
            context.Operation.OperationNumber, context.Operation.Name, context.CaseName);
    }

    public string TargetTitle { get; }
    public ObservableCollection<CamSetupNode> Setups { get; } = [];
    public ObservableCollection<PlannerPostprocessor> Postprocessors { get; } = [];
    public ObservableCollection<string> UcsNames { get; } = [];
    public ObservableCollection<SimulationPackageMachineCandidate> MachineCandidates { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand RetryRegistrationCommand { get; }
    public AsyncCommand CancelCommand { get; }

    /// <summary>The registered import, when the import completed.</summary>
    internal SimulationPackageImport? Imported { get; private set; }

    /// <summary>The Meimad-format main program of the imported data set.</summary>
    internal string? ImportedProgram => importedProgram;

    public bool CanOpenSimulation => importedProgram is not null && !IsBusy;

    public string DocumentTitle { get => documentTitle; private set => SetField(ref documentTitle, value); }
    public string DocumentDetail { get => documentDetail; private set => SetField(ref documentDetail, value); }
    public string StatusText { get => statusText; private set => SetField(ref statusText, value); }
    public string ReportText { get => reportText; private set => SetField(ref reportText, value); }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetField(ref isBusy, value)) return;
            OnPropertyChanged(nameof(CanImport));
            OnPropertyChanged(nameof(CanOpenSimulation));
            RaiseCommands();
        }
    }

    public PlannerPostprocessor? SelectedPostprocessor
    {
        get => selectedPostprocessor;
        set
        {
            if (!SetField(ref selectedPostprocessor, value)) return;
            OnPropertyChanged(nameof(CanImport));
            ImportCommand.RaiseCanExecuteChanged();
        }
    }

    public string Ucs { get => ucs; set => SetField(ref ucs, value); }

    public bool NeedsMachineChoice { get => needsMachineChoice; private set => SetField(ref needsMachineChoice, value); }

    public SimulationPackageMachineCandidate? SelectedMachine
    {
        get => selectedMachine;
        set
        {
            if (!SetField(ref selectedMachine, value)) return;
            RetryRegistrationCommand.RaiseCanExecuteChanged();
        }
    }

    public bool NeedsWorkCsConfirmation { get => needsWorkCsConfirmation; private set => SetField(ref needsWorkCsConfirmation, value); }

    public bool OverwriteManualWorkCs { get => overwriteManualWorkCs; set => SetField(ref overwriteManualWorkCs, value); }

    /// <summary>The chosen CAM operations: whole toolpaths by toolpath id, the rest by procedure id.</summary>
    internal (IReadOnlyList<int> Toolpaths, IReadOnlyList<int> Procedures, IReadOnlyList<string> Setups) Selection
    {
        get
        {
            var toolpaths = AllToolpaths().Where(item => item.IsWhole).ToArray();
            var procedures = AllToolpaths().Where(item => !item.IsWhole).SelectMany(item => item.Procedures).Where(item => item.IsChecked).ToArray();
            var setups = toolpaths.Select(item => item.Setup).Concat(procedures.Select(item => item.Toolpath.Setup))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return (toolpaths.Select(item => item.Id).ToArray(), procedures.Select(item => item.Id).ToArray(), setups);
        }
    }

    public string SelectionText
    {
        get
        {
            var (toolpaths, procedures, setups) = Selection;
            if (toolpaths.Count + procedures.Count == 0) return "Choose the CAM operations to import.";
            if (setups.Count > 1) return "Choose CAM operations of one setup: one import holds one setup (its stock and work frame).";
            var uncalculated = AllToolpaths().SelectMany(item => item.Procedures).Count(item => item.IsChecked && !item.Calculated);
            if (uncalculated > 0) return $"{uncalculated} chosen procedure(s) are not calculated in Cimatron; calculate them there first.";
            return string.Format(CultureInfo.InvariantCulture, "{0} toolpath(s) and {1} single procedure(s) of setup {2}.",
                toolpaths.Count, procedures.Count, setups.Single().Length == 0 ? "(none)" : setups.Single());
        }
    }

    public bool CanImport
    {
        get
        {
            if (IsBusy || listing is not { Ok: true, Document: { } document } || SelectedPostprocessor is null) return false;
            if (document.Modified == true) return false;
            var (toolpaths, procedures, setups) = Selection;
            return toolpaths.Count + procedures.Count > 0 && setups.Count == 1
                   && !AllToolpaths().SelectMany(item => item.Procedures).Any(item => item.IsChecked && !item.Calculated);
        }
    }

    /// <summary>Reads the active Cimatron document, the Operation's CAM source and the allowed postprocessors.</summary>
    internal async Task LoadAsync()
    {
        IsBusy = true;
        ClearPrepared();
        StatusText = "Reading the active Cimatron document…";
        try
        {
            source = await context.Api.GetOperationCamSourceAsync(context.CaseId, context.Operation.CaseOperationId);
            await LoadPostprocessorsAsync();
            listing = await bridge.ListAsync(CancellationToken.None);
            Setups.Clear();
            UcsNames.Clear();
            if (listing is not { Ok: true, Document: { } document })
            {
                DocumentTitle = "No Cimatron document";
                DocumentDetail = "";
                StatusText = listing?.Error?.Code switch
                {
                    "cimatron_not_installed" => "Cimatron is not installed on this PC. Import from Cimatron works on a workstation with Cimatron; the rest of Meimad works here as usual.",
                    "exporter_not_installed" => listing.Error.Message,
                    _ => (listing?.Error?.Message ?? "The Cimatron document could not be read.") + " "
                         + "Open the NC document in Cimatron, save it, click its window, then press Refresh."
                };
                ReportText = listing?.Error is { } error ? $"{error.Code}: {error.Message}" : "";
                return;
            }

            DocumentTitle = string.Format(CultureInfo.InvariantCulture, "CAM file: {0}", document.Name);
            DocumentDetail = document.Path ?? "";
            foreach (var name in listing.UcsNames ?? []) UcsNames.Add(name);
            if (!UcsNames.Contains(Ucs, StringComparer.OrdinalIgnoreCase) && UcsNames.Count > 0 && !UcsNames.Contains("MODEL"))
                Ucs = UcsNames[0];
            foreach (var setup in listing.Setups ?? [])
            {
                var toolpaths = new ObservableCollection<CamToolpathNode>(
                    (setup.Toolpaths ?? []).Select(toolpath => new CamToolpathNode(toolpath, setup.Name, SelectionChanged)));
                Setups.Add(new CamSetupNode(setup.Name, setup.Name.Length == 0 ? "Setup (unnamed)" : $"Setup {setup.Name}", toolpaths));
            }

            var restored = RestoreSelection(document);
            StatusText = document.Modified == true
                ? $"'{document.Name}' has unsaved changes in Cimatron: save it there, then press Refresh."
                : restored ?? "Choose the CAM operations to import, then press Import.";
            ReportText = string.Format(CultureInfo.InvariantCulture, "Cimatron {0}, exporter {1}.",
                listing.CimatronVersion ?? "(version unknown)", listing.ExporterVersion ?? "?");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
            SelectionChanged();
        }
    }

    // Postprocessors allowed for the Operation: active ones a Machine of its machine type supports (all active ones when
    // the Operation names no machine type). The last import's postprocessor is chosen again.
    private async Task LoadPostprocessorsAsync()
    {
        var all = (await context.Api.ListPostprocessorsAsync()).Where(item => item.IsActive).ToArray();
        machines = await context.Api.ListMachinesAsync();
        var type = context.Operation.RequiredMachineType?.Trim();
        var allowed = string.IsNullOrEmpty(type)
            ? all
            : all.Where(post => machines.Any(machine => machine.IsActive
                    && string.Equals(machine.ProcessType, type, StringComparison.OrdinalIgnoreCase)
                    && machine.SupportedPostprocessorIds?.Contains(post.PostprocessorId, StringComparer.Ordinal) == true))
                .ToArray();
        Postprocessors.Clear();
        foreach (var post in allowed.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)) Postprocessors.Add(post);
        SelectedPostprocessor = Postprocessors.FirstOrDefault(item => string.Equals(item.Name, source?.Postprocessor, StringComparison.OrdinalIgnoreCase))
                                ?? (Postprocessors.Count == 1 ? Postprocessors[0] : null);
        if (Postprocessors.Count == 0)
        {
            ReportText = string.IsNullOrEmpty(type)
                ? "Meimad has no active postprocessor. In Setup → Postprocessors, add the Cimatron post by its Cimatron name."
                : $"No Machine of the Operation's machine type '{type}' supports an active postprocessor. In Setup, give such a Machine the Cimatron post.";
        }
    }

    // Re-import: the CAM operations of the last import are chosen again when the active document is the same file and the
    // Cimatron ids still exist. A name alone never selects anything.
    private string? RestoreSelection(CimatronDocument document)
    {
        if (source is null) return null;
        var sameDocument = document.Path is not null && string.Equals(Normalize(document.Path), Normalize(source.DocumentPath), StringComparison.OrdinalIgnoreCase)
                           && (source.DocumentId is null || document.DocumentId is null || source.DocumentId == document.DocumentId);
        if (!sameDocument)
            return $"This Operation was last imported from '{source.DocumentName ?? source.DocumentPath}'; the active Cimatron document is another file, so nothing is preselected.";
        var missing = 0;
        foreach (var reference in source.Operations ?? [])
        {
            if (reference.Kind == "toolpath" && AllToolpaths().FirstOrDefault(item => item.Id == reference.Id) is { } toolpath) toolpath.IsChecked = true;
            else if (reference.Kind == "procedure" && AllToolpaths().SelectMany(item => item.Procedures).FirstOrDefault(item => item.Id == reference.Id) is { } procedure)
                procedure.IsChecked = true;
            else missing++;
        }

        return missing == 0
            ? "The CAM operations of the last import are chosen again; change the choice if needed, then press Import."
            : $"The CAM operations of the last import are chosen again; {missing} of them no longer exist in the document.";
    }

    /// <summary>Exports the chosen CAM operations, converts the NC to the Meimad format and registers the data set.</summary>
    internal async Task ImportAsync()
    {
        if (!CanImport || listing?.Document is not { } document || SelectedPostprocessor is not { } post) return;
        var (toolpathIds, procedureIds, _) = Selection;
        IsBusy = true;
        ClearPrepared();
        Imported = null;
        importedProgram = null;
        running = new CancellationTokenSource();
        CancelCommand.RaiseCanExecuteChanged();
        string? dataset = null;
        var registering = false;
        try
        {
            var dialect = NcViewerDialects.Resolve(machines, null, post.PostprocessorId)
                ?? throw new InvalidOperationException(
                    $"The NC dialect of the postprocessor '{post.Name}' is not known: no Machine supports it, or its Machines use different dialects. Set it in Setup → Machines.");

            StatusText = "Preparing the Operation's G-code folder…";
            var folders = NcProgramFolders.ForOperation(context.Api, context.CaseId, context.Operation.CaseOperationId, post.PostprocessorId);
            var revision = await folders.NextReleaseAsync(post.PostprocessorId, null, running.Token);
            dataset = UniqueFolder(Path.Combine(revision.Path, "cimatron-" + clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
            Directory.CreateDirectory(dataset);

            StatusText = "Cimatron is posting the chosen operations; this takes up to a minute…";
            var importId = Guid.NewGuid().ToString("N");
            var result = await bridge.ExportAsync(new CimatronExportRequest(
                dataset, PackageFolderName, post.Name, string.IsNullOrWhiteSpace(Ucs) ? "MODEL" : Ucs.Trim(), toolpathIds, procedureIds,
                context.Operation.OperationNumber.ToString(CultureInfo.InvariantCulture), context.Operation.Name, context.PartNumber,
                GeometryFolder(document.Path, toolpathIds.Count == 1 && procedureIds.Count == 0
                    ? AllToolpaths().First(item => item.Id == toolpathIds[0]).Name : null),
                document.Path, RequireSavedDocument: true,
                new CimatronImportContextData(importId, context.CaseId, context.Operation.CaseOperationId,
                    context.Operation.Version.ToString(CultureInfo.InvariantCulture))), running.Token);
            if (!result.Complete || result.PackagePath is null)
            {
                Fail(result.Error?.Message ?? FirstError(result) ?? "The export did not complete.", result.Report, dataset);
                dataset = null;
                return;
            }

            StatusText = "Converting the NC program to the Meimad format…";
            var program = await ConvertAsync(result.PackagePath, dataset, dialect, running.Token);
            if (program.Error is not null)
            {
                Fail(program.Error, result.Report, dataset);
                dataset = null;
                return;
            }

            running.Dispose();
            running = null;
            CancelCommand.RaiseCanExecuteChanged();
            registering = true;
            preparedPackage = result.PackagePath;
            preparedDataset = dataset;
            importedProgram = program.Path;
            ReportText = Join(result.Report, program.Notes);
            await RegisterAsync();
        }
        catch (OperationCanceledException)
        {
            if (dataset is not null) RemoveOwnFolder(dataset);
            StatusText = "Import cancelled; nothing was changed in Meimad.";
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidDataException)
        {
            if (!registering && dataset is not null) RemoveOwnFolder(dataset);
            StatusText = $"Not imported: {exception.Message}";
        }
        finally
        {
            running?.Dispose();
            running = null;
            IsBusy = false;
            CancelCommand.RaiseCanExecuteChanged();
            RaiseCommands();
        }
    }

    /// <summary>Registers the prepared data set again (after a network failure, a Machine choice or a Work CS confirmation).</summary>
    internal async Task RetryRegistrationAsync()
    {
        if (preparedPackage is null) return;
        IsBusy = true;
        try
        {
            await RegisterAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RegisterAsync()
    {
        StatusText = "The Server is checking and registering the data set…";
        var choices = NeedsMachineChoice || NeedsWorkCsConfirmation
            ? new SimulationPackageChoices(null, SelectedMachine?.MachineId, false, OverwriteManualWorkCs)
            : null;
        var result = await context.Register(preparedPackage!, choices);
        if (result is null)
        {
            StatusText = "The data set is prepared but not registered (the Server could not be reached or the Operation changed). Press Retry registration.";
            return;
        }

        var diagnostics = result.Report.Diagnostics ?? [];
        if (result.IsBlocked)
        {
            MachineCandidates.Clear();
            foreach (var candidate in result.Report.MachineCandidates ?? []) MachineCandidates.Add(candidate);
            NeedsMachineChoice = diagnostics.Any(item => item.Code is "machine_mapping_ambiguous" or "machine_mapping_unresolved") && MachineCandidates.Count > 0;
            NeedsWorkCsConfirmation = diagnostics.Any(item => item.Code == "work_cs_manual_conflict");
            var errors = diagnostics.Where(item => item.Severity == "error").ToArray();
            var answerable = errors.All(item => item.Code is "machine_mapping_ambiguous" or "machine_mapping_unresolved" or "work_cs_manual_conflict");
            ReportText = string.Join(Environment.NewLine, diagnostics.Select(Describe));
            if (answerable)
            {
                StatusText = "The Server needs an answer: " + string.Join(" ", errors.Select(item => item.Message))
                             + " Answer below, then press Retry registration.";
                return;
            }

            StatusText = "Not imported: " + string.Join(" ", errors.Select(item => item.Message));
            if (preparedDataset is not null) RemoveOwnFolder(preparedDataset);
            ClearPrepared();
            importedProgram = null;
            return;
        }

        Imported = result.Import;
        NeedsMachineChoice = false;
        NeedsWorkCsConfirmation = false;
        var program = importedProgram!;
        ClearPrepared();
        importedProgram = program;
        context.UseProgram(program);
        var warnings = diagnostics.Where(item => item.Severity == "warning").ToArray();
        var forSimulation = SimulationGaps(result);
        StatusText = (result.Outcome == SimulationPackageImportResult.AlreadyImported
                         ? "This data set was already imported; nothing changed."
                         : string.Format(CultureInfo.InvariantCulture, "Imported: NC {0} in Meimad format, tools, stock and Work CS. The CAM source is saved.",
                             Path.GetFileName(program)))
                     + (forSimulation.Count == 0 ? " Complete for simulation." : " Incomplete for simulation: " + string.Join(", ", forSimulation) + ".");
        ReportText = Join(ReportText, string.Join(Environment.NewLine, warnings.Select(Describe)));
        OnPropertyChanged(nameof(CanOpenSimulation));
    }

    // What a full simulation still lacks (§6): never report full readiness without part, fixtures and a coordinate system.
    private static IReadOnlyList<string> SimulationGaps(SimulationPackageImportResult result)
    {
        var gaps = new List<string>();
        var files = result.Import?.Files ?? [];
        if (files.All(file => file.Role != "part")) gaps.Add("no finished part");
        if (files.All(file => file.Role != "fixture")) gaps.Add("no fixture");
        if (result.Report.CoordinateSystem is null) gaps.Add("no coordinate system");
        return gaps;
    }

    // The package's main NC, converted with the Server's formatter, written beside the package with its subprograms
    // (unchanged: subprograms carry no Meimad placeholders). A program the formatter cannot complete blocks the import.
    private async Task<(string? Path, string? Error, string Notes)> ConvertAsync(string package, string dataset, string dialect, CancellationToken cancellationToken)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(package, "manifest.json"), cancellationToken));
        var relative = manifest.RootElement.GetProperty("files").GetProperty("nc").GetProperty("path").GetString()
                       ?? throw new InvalidDataException("The package manifest names no NC program.");
        var ncPath = Path.Combine(package, relative.Replace('/', Path.DirectorySeparatorChar));
        var original = NcTextFile.Decode(await File.ReadAllBytesAsync(ncPath, cancellationToken));
        var formatted = await context.Api.FormatNcTemplateAsync(original.Text, dialect, cancellationToken);
        if (!formatted.Validation.IsValid)
        {
            return (null, $"The NC program cannot be converted to the Meimad format: {formatted.Validation.Message} ({formatted.Validation.Code}).",
                string.Join(Environment.NewLine, formatted.Warnings));
        }

        var target = Path.Combine(dataset, Path.GetFileName(ncPath));
        await File.WriteAllBytesAsync(target, NcTextFile.Encode(formatted.Text, original.LineEnding, original.HasBom, original.EncodingName), cancellationToken);
        var ncFolder = Path.GetDirectoryName(ncPath)!;
        foreach (var file in Directory.EnumerateFiles(ncFolder))
        {
            if (string.Equals(file, ncPath, StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(dataset, Path.GetFileName(file)), overwrite: false);
        }

        var notes = new List<string>();
        notes.Add(formatted.Changed
            ? "Meimad format: " + string.Join("; ", formatted.Changes)
            : "Meimad format: the program already had every marker; nothing was added.");
        notes.AddRange(formatted.Warnings);
        return (target, null, string.Join(Environment.NewLine, notes));
    }

    /// <summary>"Meimad geometry\&lt;toolpath&gt;" beside the NC document, else "Meimad geometry", when it holds STL files.</summary>
    internal static string? GeometryFolder(string? documentPath, string? toolpath)
    {
        if (string.IsNullOrWhiteSpace(documentPath) || Path.GetDirectoryName(documentPath) is not { } folder) return null;
        var root = Path.Combine(folder, GeometryFolderName);
        static bool HasStl(string path) => Directory.Exists(path) && Directory.EnumerateFiles(path, "*.stl").Any();
        if (toolpath is not null && HasStl(Path.Combine(root, toolpath))) return Path.Combine(root, toolpath);
        return HasStl(root) ? root : null;
    }

    private void Fail(string message, string? report, string dataset)
    {
        RemoveOwnFolder(dataset);
        StatusText = $"Not imported: {message} Nothing was changed in Meimad.";
        ReportText = report ?? "";
    }

    // Removes the folder this import created (and its revision folder when that is left empty), never anything else.
    private static void RemoveOwnFolder(string dataset)
    {
        try
        {
            if (Directory.Exists(dataset)) Directory.Delete(dataset, true);
            var revision = Path.GetDirectoryName(dataset);
            if (revision is not null && Directory.Exists(revision) && !Directory.EnumerateFileSystemEntries(revision).Any()) Directory.Delete(revision);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the user; the data set was never registered.
        }
    }

    private static string UniqueFolder(string path)
    {
        var candidate = path;
        for (var index = 2; Directory.Exists(candidate); index++) candidate = $"{path}-{index}";
        return candidate;
    }

    private void ClearPrepared()
    {
        preparedPackage = null;
        preparedDataset = null;
        RetryRegistrationCommand.RaiseCanExecuteChanged();
    }

    private IEnumerable<CamToolpathNode> AllToolpaths() => Setups.SelectMany(setup => setup.Toolpaths);

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanImport));
        ImportCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        ImportCommand.RaiseCanExecuteChanged();
        RetryRegistrationCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private static string? FirstError(CimatronExportResult result) =>
        result.Entries?.FirstOrDefault(entry => entry.Status == "error") is { } error ? $"{error.Message} ({error.Code})" : null;

    private static string Describe(SimulationPackageDiagnostic item) =>
        $"{(item.Severity == "error" ? "✖ Error" : item.Severity == "warning" ? "⚠ Warning" : "ℹ Note")}: {item.Message} ({item.Code})";

    private static string Join(params string?[] parts) =>
        string.Join(Environment.NewLine + Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\');
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is PlannerApiException or PlannerProtocolException or IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or JsonException
            or KeyNotFoundException;

    public event PropertyChangedEventHandler? PropertyChanged;

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
