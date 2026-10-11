using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Meimad.Planner.NcEngine;
using Meimad.Planner.NcEngine.MachineDefinitions;

namespace Meimad.Planner.Client.Windows.Presentation.MachineDesigner;

/// <summary>What the designer needs from its window: dialogs, confirmations and mesh URLs for the 3D page.</summary>
internal interface IMachineDesignerHost
{
    /// <summary>Mesh files to import (GLB, STL or STEP); empty when canceled.</summary>
    IReadOnlyList<string> ChooseMeshFiles();
    bool Confirm(string message, string title);
    /// <summary>A URL the 3D page can load the file at <paramref name="path"/> from (binary STL).</summary>
    string MeshUrl(string path);
}

internal sealed record DesignerTemplateOption(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The Machine Designer (PRD docs/prd-machine-designer-mini-vmid.md, schema v114): edits one Machine's simulation
/// definition — the component tree at the all-axes-zero pose, joints, mounts, meshes, magazine and timing — as a
/// Server draft, validates it with the Server's rules and publishes it. Publishing is the only way to change the
/// Machine's magazine and timing values. Without the setup permission the definition is shown read-only (jogging
/// still works).
/// </summary>
internal sealed partial class MachineDesignerViewModel : DesignerObservable
{
    private readonly IPlannerApiClient api;
    private readonly string clientId;
    private readonly IMachineDesignerHost host;
    private readonly Dictionary<string, string> assetPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> assetProblems = new(StringComparer.Ordinal);
    private string entityTag = string.Empty;
    private bool isDirty;
    private bool isBusy;
    private bool populating;
    private string statusText = "Loading the machine definition…";
    private string versionText = string.Empty;
    private DesignerNode? selectedNode;
    private DesignerMesh? selectedMesh;
    private DesignerTemplateOption? selectedTemplate;
    private string name = string.Empty;
    private string interpreterAxes = string.Empty;
    private string? toolMountNode;
    private string? workpieceMountNode;
    private string nominalCapacity = string.Empty;
    private string usablePositions = string.Empty;
    private string toolChangeSeconds = string.Empty;
    private string rapidRate = string.Empty;
    private string machineTimeFactor = "1";
    private string ignoredPairs = string.Empty;
    private bool zeroVerified;
    private bool rotaryLinesVerified;
    private bool mountsVerified;
    private string calibrationNotes = string.Empty;
    private MachineSimulationDefinition? published;

    internal MachineDesignerViewModel(
        IPlannerApiClient api, string clientId, PlannerMachine machine, bool canEdit, IMachineDesignerHost host,
        IEnumerable<DesignerTemplateOption>? templates = null)
    {
        this.api = api;
        this.clientId = clientId;
        this.host = host;
        Machine = machine;
        CanEdit = canEdit;
        ToolMountPosition = new DesignerVector();
        WorkpieceMountPosition = new DesignerVector();
        ToolMountPosition.Edited += (_, _) => Changed();
        WorkpieceMountPosition.Edited += (_, _) => Changed();
        Templates = new ObservableCollection<DesignerTemplateOption>(
            [new DesignerTemplateOption(null, "None (automatic detection)"), .. templates ?? InstalledTemplates()]);
        selectedTemplate = Templates[0];
        ReloadCommand = new AsyncCommand(ReloadAsync, () => !IsBusy);
        SaveCommand = new AsyncCommand(SaveDraftAsync, () => CanEdit && !IsBusy);
        ValidateCommand = new AsyncCommand(ValidateAsync, () => !IsBusy);
        PublishCommand = new AsyncCommand(PublishAsync, () => CanEdit && !IsBusy);
        AddNodeCommand = new AsyncCommand(() => { AddNode(); return Task.CompletedTask; }, () => CanEdit);
        RemoveNodeCommand = new AsyncCommand(() => { RemoveSelectedNode(); return Task.CompletedTask; }, () => CanEdit && SelectedNode is not null);
        ImportMeshCommand = new AsyncCommand(ImportMeshesAsync, () => CanEdit && SelectedNode is not null && !IsBusy);
        ImportLegacyMeshCommand = new AsyncCommand(ImportSelectedLegacyMeshAsync, () => CanEdit && SelectedMesh is { IsLegacy: true } && !IsBusy);
        RemoveMeshCommand = new AsyncCommand(() => { RemoveSelectedMesh(); return Task.CompletedTask; }, () => CanEdit && SelectedMesh is not null);
        ZeroAllCommand = new AsyncCommand(() => { ZeroAll(); return Task.CompletedTask; });
        LoadTemplateCommand = new AsyncCommand(() => { LoadComponentsFromTemplate(); return Task.CompletedTask; },
            () => CanEdit && SelectedTemplate?.Id is not null);
    }

    /// <summary>Raised when the 3D view must be redrawn (structure, placement or meshes changed).</summary>
    internal event EventHandler? SceneChanged;

    /// <summary>Raised when only the axis readings changed.</summary>
    internal event EventHandler? PoseChanged;

    internal PlannerMachine Machine { get; }
    public bool CanEdit { get; }
    public bool IsReadOnly => !CanEdit;
    public string Title => $"Machine Designer — {Machine.Number} {Machine.Name}";

    public ObservableCollection<DesignerNode> Nodes { get; } = [];
    public ObservableCollection<DesignerNode> RootNodes { get; } = [];
    public ObservableCollection<MachineSimulationIssueDto> Issues { get; } = [];
    public ObservableCollection<DesignerJog> Jogs { get; } = [];
    public ObservableCollection<DesignerTemplateOption> Templates { get; }
    public IReadOnlyList<string> JointTypes => DesignerNode.JointTypes;
    public IReadOnlyList<string> PrimitiveShapes => DesignerNode.PrimitiveShapes;
    public IReadOnlyList<string> CylinderAxes { get; } = ["x", "y", "z"];

    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ValidateCommand { get; }
    public AsyncCommand PublishCommand { get; }
    public AsyncCommand AddNodeCommand { get; }
    public AsyncCommand RemoveNodeCommand { get; }
    public AsyncCommand ImportMeshCommand { get; }
    public AsyncCommand ImportLegacyMeshCommand { get; }
    public AsyncCommand RemoveMeshCommand { get; }
    public AsyncCommand ZeroAllCommand { get; }
    public AsyncCommand LoadTemplateCommand { get; }

    public bool IsDirty { get => isDirty; private set { if (Set(ref isDirty, value)) OnPropertyChanged(nameof(VersionText)); } }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            foreach (var command in new[] { ReloadCommand, SaveCommand, ValidateCommand, PublishCommand, ImportMeshCommand, ImportLegacyMeshCommand })
                command.RaiseCanExecuteChanged();
        }
    }

    public string StatusText { get => statusText; private set => Set(ref statusText, value); }

    public string VersionText => versionText + (IsDirty ? " · unsaved changes" : string.Empty);

    public DesignerNode? SelectedNode
    {
        get => selectedNode;
        set
        {
            var previous = selectedNode;
            if (!Set(ref selectedNode, value)) return;
            if (previous is not null) previous.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            SelectedMesh = null;
            RemoveNodeCommand.RaiseCanExecuteChanged();
            ImportMeshCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelectedNode));
            OnPropertyChanged(nameof(ParentChoices));
            SceneChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool HasSelectedNode => selectedNode is not null;

    public DesignerMesh? SelectedMesh
    {
        get => selectedMesh;
        set
        {
            if (!Set(ref selectedMesh, value)) return;
            RemoveMeshCommand.RaiseCanExecuteChanged();
            ImportLegacyMeshCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Possible parents of the selected component: every other component.</summary>
    public IReadOnlyList<string> ParentChoices => Nodes.Where(node => node != selectedNode).Select(node => node.Id).ToArray();

    public IReadOnlyList<string> NodeIds => Nodes.Select(node => node.Id).ToArray();

    public DesignerTemplateOption? SelectedTemplate
    {
        get => selectedTemplate;
        set
        {
            if (!Set(ref selectedTemplate, value)) return;
            LoadTemplateCommand.RaiseCanExecuteChanged();
            Changed(scene: false);
        }
    }

    public string Name { get => name; set { if (Set(ref name, value)) Changed(scene: false); } }
    /// <summary>Physical-to-interpreted axis names, such as "A=B" (Mazak: the physical A axis is programmed as B).</summary>
    public string InterpreterAxes { get => interpreterAxes; set { if (Set(ref interpreterAxes, value)) Changed(scene: false); } }
    public string? ToolMountNode { get => toolMountNode; set { if (Set(ref toolMountNode, value)) Changed(); } }
    public DesignerVector ToolMountPosition { get; }
    public string? WorkpieceMountNode { get => workpieceMountNode; set { if (Set(ref workpieceMountNode, value)) Changed(); } }
    public DesignerVector WorkpieceMountPosition { get; }
    public string NominalCapacity { get => nominalCapacity; set { if (Set(ref nominalCapacity, value)) Changed(scene: false); } }
    public string UsablePositions { get => usablePositions; set { if (Set(ref usablePositions, value)) Changed(scene: false); } }
    public string ToolChangeSeconds { get => toolChangeSeconds; set { if (Set(ref toolChangeSeconds, value)) Changed(scene: false); } }
    public string RapidRate { get => rapidRate; set { if (Set(ref rapidRate, value)) Changed(scene: false); } }
    public string MachineTimeFactor { get => machineTimeFactor; set { if (Set(ref machineTimeFactor, value)) Changed(scene: false); } }
    /// <summary>Component pairs never checked against each other, as "a/b; c/d".</summary>
    public string IgnoredPairs { get => ignoredPairs; set { if (Set(ref ignoredPairs, value)) Changed(scene: false); } }
    public bool ZeroVerified { get => zeroVerified; set { if (Set(ref zeroVerified, value)) Changed(); } }
    public bool RotaryLinesVerified { get => rotaryLinesVerified; set { if (Set(ref rotaryLinesVerified, value)) Changed(scene: false); } }
    public bool MountsVerified { get => mountsVerified; set { if (Set(ref mountsVerified, value)) Changed(); } }
    public string CalibrationNotes { get => calibrationNotes; set { if (Set(ref calibrationNotes, value)) Changed(scene: false); } }

    /// <summary>What the published definition sets on the Machine, for the summary next to the editable values.</summary>
    public string PublishedSummary => published is null
        ? "Not published yet."
        : string.Create(CultureInfo.InvariantCulture,
            $"Published: {published.Template ?? "automatic detection"} · usable positions {published.Magazine.UsablePositions?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} · tool change {DesignerNumbers.Format(published.Magazine.ToolChangeSeconds)} s · rapid {DesignerNumbers.Format(published.Timing.RapidRateMillimetersPerMinute)} mm/min · time factor {DesignerNumbers.Format(published.Timing.MachineTimeFactor)}");

    // ----- load ---------------------------------------------------------------------------------

    internal async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var resource = await api.GetMachineSimulationAsync(Machine.MachineId);
            Apply(resource);
            StatusText = CanEdit
                ? "Edit the machine at all axes zero, save the draft, then publish it."
                : "Read-only: your account may not change setup data. You can still jog the axes.";
            await ResolveAssetsAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusText = $"The machine definition could not be loaded: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAsync()
    {
        if (IsDirty && !host.Confirm("Discard your unsaved changes and load the definition from the Server?", "Reload")) return;
        await LoadAsync();
    }

    private void Apply(MachineSimulationResource resource)
    {
        entityTag = resource.EntityTag;
        var record = resource.Value;
        published = record.Published;
        versionText = string.Create(CultureInfo.InvariantCulture,
            $"Draft v{record.DraftVersion} · published v{record.PublishedVersion}{(record.HasUnpublishedChanges ? " · draft not published" : string.Empty)}");
        Populate(record.Draft);
        SetIssues(record.Issues);
        IsDirty = false;
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(PublishedSummary));
    }

    internal void Populate(MachineSimulationDefinition definition)
    {
        populating = true;
        try
        {
            var selectedId = SelectedNode?.Id;
            Nodes.Clear();
            foreach (var node in definition.Nodes) Track(new DesignerNode(node));
            Name = definition.Name ?? Machine.Name;
            SelectedTemplate = Templates.FirstOrDefault(option => option.Id == definition.Template)
                ?? AddMissingTemplate(definition.Template);
            InterpreterAxes = string.Join(", ", (definition.InterpreterAxes ?? new Dictionary<string, string>())
                .Select(pair => $"{pair.Key}={pair.Value}"));
            ToolMountNode = definition.Mounts.Tool?.Node;
            Copy(definition.Mounts.Tool?.Position, ToolMountPosition);
            WorkpieceMountNode = definition.Mounts.Workpiece?.Node;
            Copy(definition.Mounts.Workpiece?.Position, WorkpieceMountPosition);
            NominalCapacity = definition.Magazine.NominalCapacity?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            UsablePositions = definition.Magazine.UsablePositions?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            ToolChangeSeconds = DesignerNumbers.Format(definition.Magazine.ToolChangeSeconds);
            RapidRate = DesignerNumbers.Format(definition.Timing.RapidRateMillimetersPerMinute);
            MachineTimeFactor = DesignerNumbers.Format(definition.Timing.MachineTimeFactor);
            IgnoredPairs = string.Join("; ", (definition.Collision?.IgnoredPairs ?? []).Select(pair => string.Join("/", pair)));
            ZeroVerified = definition.Calibration?.ZeroVerified ?? false;
            RotaryLinesVerified = definition.Calibration?.RotaryLinesVerified ?? false;
            MountsVerified = definition.Calibration?.MountsVerified ?? false;
            CalibrationNotes = definition.Calibration?.Notes ?? string.Empty;
            RebuildTree();
            RebuildJogs();
            SelectedNode = Nodes.FirstOrDefault(node => node.Id == selectedId) ?? Nodes.FirstOrDefault();
        }
        finally
        {
            populating = false;
        }
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    private DesignerTemplateOption AddMissingTemplate(string? id)
    {
        if (id is null) return Templates[0];
        var option = new DesignerTemplateOption(id, $"{id} (not installed with this client)");
        Templates.Add(option);
        return option;
    }

    private static void Copy(double[]? value, DesignerVector target)
    {
        var v = MachineVectors.IsFinite3(value) ? value! : [0, 0, 0];
        target.X = DesignerNumbers.Format(v[0]);
        target.Y = DesignerNumbers.Format(v[1]);
        target.Z = DesignerNumbers.Format(v[2]);
    }

    private void Track(DesignerNode node)
    {
        node.Edited += (_, _) => Changed();
        node.PropertyChanged += (_, args) =>
        {
            if (populating) return;
            if (args.PropertyName is nameof(DesignerNode.Parent) or nameof(DesignerNode.Id))
            {
                RebuildTree();
                OnPropertyChanged(nameof(NodeIds));
            }
            if (args.PropertyName is nameof(DesignerNode.JointType) or nameof(DesignerNode.Axis) or nameof(DesignerNode.Min)
                or nameof(DesignerNode.Max) or nameof(DesignerNode.Continuous))
                RebuildJogs();
        };
        Nodes.Add(node);
    }

    private void Changed(bool scene = true)
    {
        if (populating) return;
        IsDirty = true;
        if (scene) SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildTree()
    {
        RootNodes.Clear();
        foreach (var node in Nodes) node.Children.Clear();
        var byId = Nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            // A missing parent or a cycle shows the component at the top level; validation names the problem.
            if (node.Parent is { } parent && byId.TryGetValue(parent, out var parentNode) && parentNode != node && !IsAncestor(node, parentNode, byId))
                parentNode.Children.Add(node);
            else
                RootNodes.Add(node);
        }
    }

    private static bool IsAncestor(DesignerNode node, DesignerNode candidate, Dictionary<string, DesignerNode> byId)
    {
        var cursor = candidate;
        for (var depth = 0; cursor is not null && depth <= byId.Count; depth++)
        {
            if (cursor == node) return true;
            cursor = cursor.Parent is { } parent && byId.TryGetValue(parent, out var next) ? next : null;
        }
        return cursor is not null;
    }

    private void RebuildJogs()
    {
        var previous = Jogs.ToDictionary(jog => jog.Axis, jog => jog.Value, StringComparer.Ordinal);
        foreach (var jog in Jogs) jog.PropertyChanged -= JogChanged;
        Jogs.Clear();
        foreach (var node in Nodes.Where(node => node.HasJoint && !string.IsNullOrWhiteSpace(node.Axis)))
        {
            if (Jogs.Any(jog => jog.Axis == node.Axis)) continue;
            DesignerNumbers.TryParseOptional(node.Min, out var min);
            DesignerNumbers.TryParseOptional(node.Max, out var max);
            var continuous = node.IsRotary && node.Continuous;
            var jog = new DesignerJog(node.Axis, node.IsRotary, continuous ? null : min, continuous ? null : max);
            if (previous.TryGetValue(jog.Axis, out var value)) jog.Value = Math.Clamp(value, jog.Minimum, jog.Maximum);
            jog.PropertyChanged += JogChanged;
            Jogs.Add(jog);
        }
        PoseChanged?.Invoke(this, EventArgs.Empty);
    }

    private void JogChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DesignerJog.Value)) PoseChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ZeroAll()
    {
        foreach (var jog in Jogs) jog.Value = Math.Clamp(0, jog.Minimum, jog.Maximum);
    }

    internal void MarkSavedForTesting() => IsDirty = false;

    internal IReadOnlyDictionary<string, double> Pose() =>
        Jogs.ToDictionary(jog => jog.Axis, jog => jog.Value, StringComparer.Ordinal);

    // ----- the definition -----------------------------------------------------------------------

    /// <summary>The definition the fields describe; fields that are not numbers are reported in <paramref name="problems"/>.</summary>
    internal MachineSimulationDefinition BuildDefinition(out IReadOnlyList<MachineSimulationIssue> problems)
    {
        var found = new List<MachineSimulationIssue>();
        var nodes = Nodes.Select((node, index) => node.ToNode(found, $"nodes[{index}]")).ToArray();
        int? Integer(string text, string path)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
            found.Add(new(path, "not_finite", "Enter a whole number or leave it empty."));
            return null;
        }
        double? Number(string text, string path)
        {
            if (DesignerNumbers.TryParseOptional(text, out var value)) return value;
            found.Add(new(path, "not_finite", "Enter a number with a decimal point or leave it empty."));
            return null;
        }
        var axes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in InterpreterAxes.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && pair[0].Length > 0 && pair[1].Length > 0) axes[pair[0].ToUpperInvariant()] = pair[1].ToUpperInvariant();
            else found.Add(new("interpreterAxes", "interpreter_axes", "Write physical=interpreted pairs, such as A=B."));
        }
        var pairs = IgnoredPairs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('/', StringSplitOptions.TrimEntries))
            .ToArray();
        var toolPosition = ToolMountPosition.Value(out var toolProblem);
        if (toolProblem is not null) found.Add(new("mounts.tool.position", "not_finite", toolProblem));
        var workpiecePosition = WorkpieceMountPosition.Value(out var workpieceProblem);
        if (workpieceProblem is not null) found.Add(new("mounts.workpiece.position", "not_finite", workpieceProblem));
        var definition = new MachineSimulationDefinition
        {
            MachineId = Machine.MachineId,
            Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
            Template = SelectedTemplate?.Id,
            InterpreterAxes = axes.Count > 0 ? axes : null,
            Nodes = nodes,
            Mounts = new MachineMounts
            {
                Tool = string.IsNullOrWhiteSpace(ToolMountNode) ? null : new MachineMount { Node = ToolMountNode, Position = toolPosition ?? [0, 0, 0] },
                Workpiece = string.IsNullOrWhiteSpace(WorkpieceMountNode) ? null : new MachineMount { Node = WorkpieceMountNode, Position = workpiecePosition ?? [0, 0, 0] }
            },
            Magazine = new MachineMagazine
            {
                NominalCapacity = Integer(NominalCapacity, "magazine.nominalCapacity"),
                UsablePositions = Integer(UsablePositions, "magazine.usablePositions"),
                ToolChangeSeconds = Number(ToolChangeSeconds, "magazine.toolChangeSeconds")
            },
            Timing = new MachineTiming
            {
                RapidRateMillimetersPerMinute = Number(RapidRate, "timing.rapidRateMillimetersPerMinute"),
                MachineTimeFactor = Number(MachineTimeFactor, "timing.machineTimeFactor") ?? 1
            },
            Collision = pairs.Length > 0 ? new MachineCollisionSettings { IgnoredPairs = pairs } : null,
            Calibration = new MachineCalibration
            {
                ZeroVerified = ZeroVerified,
                RotaryLinesVerified = RotaryLinesVerified,
                MountsVerified = MountsVerified,
                Notes = string.IsNullOrWhiteSpace(CalibrationNotes) ? null : CalibrationNotes.Trim()
            }
        };
        problems = found;
        return definition;
    }

    private void SetIssues(IEnumerable<MachineSimulationIssueDto> issues)
    {
        Issues.Clear();
        foreach (var issue in issues.OrderBy(issue => issue.IsError ? 0 : 1)) Issues.Add(issue);
        foreach (var (assetId, problem) in assetProblems)
            Issues.Add(new MachineSimulationIssueDto($"asset {assetId}", "asset_unavailable", problem, "error"));
    }

    /// <summary>Selects the component an issue path such as "nodes[3].joint.direction" points at.</summary>
    internal void SelectIssue(MachineSimulationIssueDto? issue)
    {
        if (issue is null) return;
        var match = NodeIndex().Match(issue.Path);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index < Nodes.Count)
            SelectedNode = Nodes[index];
    }

    [GeneratedRegex(@"^nodes\[(\d+)\]")]
    private static partial Regex NodeIndex();

    private async Task ValidateAsync()
    {
        var definition = BuildDefinition(out var problems);
        if (problems.Count > 0)
        {
            SetIssues(problems.Select(Dto));
            StatusText = "Some fields are not numbers; fix the fields listed under Validation.";
            return;
        }
        IsBusy = true;
        try
        {
            var issues = await api.ValidateMachineSimulationAsync(Machine.MachineId, definition);
            SetIssues(issues);
            StatusText = issues.Any(issue => issue.IsError)
                ? $"{issues.Count(issue => issue.IsError)} error(s) must be fixed before publishing."
                : "No errors. Warnings do not block publishing.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusText = $"Validation failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task<bool> SaveDraftAsync()
    {
        var definition = BuildDefinition(out var problems);
        if (problems.Count > 0)
        {
            SetIssues(problems.Select(Dto));
            StatusText = "The draft was not saved: some fields are not numbers (see Validation).";
            return false;
        }
        IsBusy = true;
        try
        {
            var saved = await api.SaveMachineSimulationDraftAsync(Machine.MachineId, definition, entityTag, clientId);
            Apply(saved);
            StatusText = saved.Value.Issues.Any(issue => issue.IsError)
                ? "Draft saved. It has errors to fix before it can be published."
                : "Draft saved. Publish it to use it in the NC viewer and planning.";
            return true;
        }
        catch (PlannerApiException exception) when (exception.Conflict is not null || exception.Code == "resource_version_stale")
        {
            StatusText = "Someone saved this machine definition after you opened it, so your draft was not saved. Reload to see their changes, then make yours again.";
            return false;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusText = $"The draft was not saved: {exception.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PublishAsync()
    {
        if (IsDirty && !await SaveDraftAsync()) return;
        if (Issues.Any(issue => issue.IsError))
        {
            StatusText = "Fix the errors under Validation before publishing.";
            return;
        }
        if (!host.Confirm(
                "Publish this definition? The NC viewer will draw and interpret the Machine with it, and its magazine and timing values become the Machine's values used for cycle-time estimates and planning.",
                "Publish machine definition"))
            return;
        IsBusy = true;
        try
        {
            var result = await api.PublishMachineSimulationAsync(Machine.MachineId, entityTag, clientId);
            Apply(result);
            StatusText = "Published. NC viewers opened from now on use this definition.";
        }
        catch (PlannerApiException exception) when (exception.Code == "machine_definition_invalid")
        {
            StatusText = "The Server found errors; the definition was not published (see Validation).";
            IsBusy = false;
            await ValidateAsync();
        }
        catch (PlannerApiException exception) when (exception.Conflict is not null || exception.Code == "resource_version_stale")
        {
            StatusText = "Someone changed this machine definition after you opened it, so it was not published. Reload, check their changes, then publish again.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusText = $"The definition was not published: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static MachineSimulationIssueDto Dto(MachineSimulationIssue issue) => new(issue.Path, issue.Code, issue.Message, issue.Severity);

    // ----- components ---------------------------------------------------------------------------

    internal DesignerNode AddNode()
    {
        var index = Nodes.Count + 1;
        string id;
        do id = $"component-{index++}"; while (Nodes.Any(node => node.Id == id));
        var parent = SelectedNode?.Id ?? Nodes.FirstOrDefault(node => node.Parent is null)?.Id;
        var node = new DesignerNode(new MachineNode { Id = id, Label = "New component", Parent = parent });
        Track(node);
        RebuildTree();
        OnPropertyChanged(nameof(NodeIds));
        SelectedNode = node;
        Changed();
        return node;
    }

    internal void RemoveSelectedNode()
    {
        if (SelectedNode is not { } node) return;
        if (!host.Confirm($"Remove the component {node.Label} ({node.Id})? Its children move to its parent.", "Remove component")) return;
        foreach (var child in Nodes.Where(other => other.Parent == node.Id).ToArray()) child.Parent = node.Parent;
        Nodes.Remove(node);
        if (ToolMountNode == node.Id) ToolMountNode = null;
        if (WorkpieceMountNode == node.Id) WorkpieceMountNode = null;
        RebuildTree();
        RebuildJogs();
        OnPropertyChanged(nameof(NodeIds));
        SelectedNode = Nodes.FirstOrDefault(other => other.Id == node.Parent) ?? Nodes.FirstOrDefault();
        Changed();
    }

    /// <summary>
    /// Replaces the components, mounts and axis names with those of an installed NC viewer machine (a starting point
    /// whose positions must still be checked on the real machine); magazine and timing values are kept.
    /// </summary>
    internal void LoadComponentsFromTemplate()
    {
        if (SelectedTemplate?.Id is not { } templateId) return;
        if (Nodes.Count > 0 && !host.Confirm(
                $"Replace the components, mounts and axis names with those of {SelectedTemplate.Name}? Meshes on the current components are removed. Magazine and timing values are kept.",
                "Start from installed machine"))
            return;
        var template = NcEngineMachineCatalog.LoadDefinition(templateId);
        if (template is null)
        {
            StatusText = $"{templateId} is not installed with this client.";
            return;
        }
        var current = BuildDefinition(out _);
        var built = MachineSimulationCompiler.FromTemplate(template, templateId, new MachineLegacyValues(
            Machine.MachineId, current.Name, current.Magazine.UsablePositions, current.Timing.RapidRateMillimetersPerMinute,
            current.Magazine.ToolChangeSeconds, current.Timing.MachineTimeFactor));
        Populate(built with
        {
            Name = current.Name,
            Magazine = built.Magazine with { NominalCapacity = current.Magazine.NominalCapacity ?? built.Magazine.NominalCapacity }
        });
        IsDirty = true;
        StatusText = $"Components loaded from {SelectedTemplate.Name}. Check every position against the real machine, then save.";
    }

    // ----- meshes -------------------------------------------------------------------------------

    private async Task ImportMeshesAsync()
    {
        if (SelectedNode is not { } node) return;
        var files = host.ChooseMeshFiles();
        if (files.Count == 0) return;
        IsBusy = true;
        try
        {
            foreach (var file in files)
            {
                var mesh = await UploadAsync(file);
                if (mesh is null) continue;
                node.AddMesh(new DesignerMesh(mesh));
            }
            SceneChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportSelectedLegacyMeshAsync()
    {
        if (SelectedMesh is not { IsLegacy: true, LegacyPath: { } path } mesh) return;
        IsBusy = true;
        try
        {
            var uploaded = await UploadAsync(path);
            if (uploaded is null) return;
            mesh.AssetId = uploaded.AssetId;
            mesh.LegacyPath = null;
            StatusText = $"{mesh.Name} is now stored on the Server. Save the draft to keep it.";
            SceneChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Uploads a GLB or STL file as it is; a STEP file is tessellated here and uploaded as STL (the Server stores only
    /// GLB and STL). Returns the new mesh, or null when the file could not be read or uploaded (the reason is shown).
    /// </summary>
    internal async Task<MachineModel?> UploadAsync(string path)
    {
        var fileName = Path.GetFileName(path);
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".glb" or ".stl" or ".stp" or ".step"))
            {
                StatusText = $"{fileName}: choose a GLB, STL or STEP file.";
                return null;
            }
            var bytes = extension is ".stp" or ".step"
                ? await Task.Run(() => NcViewerMeshes.ReadAsStl(path))
                : await File.ReadAllBytesAsync(path);
            if (extension == ".glb") GlbMeshReader.ReadTriangles(bytes); // refuse an unreadable GLB before it is stored
            var uploadName = extension is ".stp" or ".step" ? Path.ChangeExtension(fileName, ".stl") : fileName;
            using var stream = new MemoryStream(bytes, writable: false);
            var asset = await api.UploadMachineSimulationAssetAsync(Machine.MachineId, uploadName, stream, clientId);
            await RememberAsync(asset, bytes);
            return new MachineModel
            {
                AssetId = asset.AssetId,
                Name = Path.GetFileNameWithoutExtension(fileName),
                Color = MachineNcViewerModelDefaults.Color
            };
        }
        catch (Exception exception) when (IsExpected(exception) || exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException)
        {
            StatusText = $"{fileName} was not imported: {exception.Message}";
            return null;
        }
    }

    private async Task RememberAsync(MachineSimulationAssetDto asset, byte[] bytes)
    {
        Directory.CreateDirectory(NcViewerMachineDefinitions.CacheFolder);
        var path = Path.Combine(NcViewerMachineDefinitions.CacheFolder,
            asset.Sha256 + (asset.MediaType == "model/gltf-binary" ? ".glb" : ".stl"));
        if (!File.Exists(path) && string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            await File.WriteAllBytesAsync(path, bytes);
        assetPaths[asset.AssetId] = path;
        assetProblems.Remove(asset.AssetId);
    }

    private void RemoveSelectedMesh()
    {
        if (SelectedNode is not { } node || SelectedMesh is not { } mesh) return;
        node.RemoveMesh(mesh);
        SelectedMesh = null;
    }

    /// <summary>Downloads (once) every uploaded mesh the definition uses so the 3D view can draw it.</summary>
    private async Task ResolveAssetsAsync()
    {
        var used = Nodes.SelectMany(node => node.Meshes).Select(mesh => mesh.AssetId).OfType<string>()
            .Where(id => !assetPaths.ContainsKey(id)).Distinct().ToArray();
        if (used.Length == 0) return;
        try
        {
            var assets = await api.ListMachineSimulationAssetsAsync(Machine.MachineId);
            foreach (var id in used)
            {
                if (assets.FirstOrDefault(asset => asset.AssetId == id) is not { } asset)
                {
                    assetProblems[id] = "The mesh is not on the Server; import it again.";
                    continue;
                }
                try
                {
                    assetPaths[id] = await NcViewerMachineDefinitions.CachedAsync(api, Machine.MachineId, asset, CancellationToken.None);
                }
                catch (Exception exception) when (IsExpected(exception) || exception is IOException or InvalidDataException)
                {
                    assetProblems[id] = $"The mesh {asset.FileName} could not be downloaded: {exception.Message}";
                }
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            foreach (var id in used) assetProblems[id] = $"The meshes could not be listed: {exception.Message}";
        }
        if (assetProblems.Count > 0) SetIssues(Issues.Where(issue => issue.Code != "asset_unavailable").ToArray());
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    // ----- the 3D page --------------------------------------------------------------------------

    /// <summary>
    /// The scene for the 3D page: every component with its zero-pose position, joint, primitive and meshes (mesh URLs
    /// from the window), the mounts and the selected component. Fields that are not numbers draw as zero.
    /// </summary>
    internal string SceneJson()
    {
        var definition = BuildDefinition(out _);
        return JsonSerializer.Serialize(new
        {
            type = "scene",
            selected = SelectedNode?.Id,
            nodes = definition.Nodes.Select(node => new
            {
                id = node.Id,
                parent = node.Parent,
                label = node.Label ?? node.Id,
                position = node.Position,
                joint = node.Joint is { } joint
                    ? new
                    {
                        axis = joint.Axis,
                        type = joint.Type,
                        direction = MachineVectors.TryNormalize(joint.Direction, out var unit) ? unit : [0, 0, 1],
                        point = joint.Point,
                        scale = joint.Scale
                    }
                    : null,
                primitive = node.Primitive,
                models = node.Models.Select(model => new
                {
                    name = model.Name ?? model.AssetId ?? Path.GetFileName(model.LegacyPath),
                    meshUrl = MeshPath(model) is { } path ? host.MeshUrl(path) : null,
                    problem = MeshPath(model) is null
                        ? model.AssetId is { } id && assetProblems.TryGetValue(id, out var problem) ? problem : "The mesh file is not available on this PC."
                        : null,
                    position = model.Alignment.Position,
                    rotationDeg = model.Alignment.RotationDeg,
                    scale = model.Alignment.Scale,
                    color = model.Color,
                    opacity = model.Opacity,
                    visible = model.Visible,
                    wireframe = model.Wireframe
                })
            }),
            mounts = new
            {
                tool = definition.Mounts.Tool is { } tool ? new { node = tool.Node, position = tool.Position } : null,
                workpiece = definition.Mounts.Workpiece is { } workpiece ? new { node = workpiece.Node, position = workpiece.Position } : null
            },
            axes = Pose(),
            calibration = ZeroVerified && MountsVerified
                ? null
                : "Unverified: positions are not yet checked on the real machine."
        }, PageJson);
    }

    internal string PoseJson() => JsonSerializer.Serialize(new { type = "pose", axes = Pose() }, PageJson);

    private static readonly JsonSerializerOptions PageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private string? MeshPath(MachineModel model) =>
        model.AssetId is { } id
            ? assetPaths.GetValueOrDefault(id)
            : model.LegacyPath is { } legacy && File.Exists(legacy) ? legacy : null;

    /// <summary>A component picked in the 3D view.</summary>
    internal void SelectFromPage(string? id)
    {
        if (Nodes.FirstOrDefault(node => node.Id == id) is { } node) SelectedNode = node;
    }

    /// <summary>A mesh the 3D page could not draw: shown as an error so a broken mesh is never mistaken for a loaded one.</summary>
    internal void ReportMeshProblem(string? node, string? mesh, string? message)
    {
        var issue = new MachineSimulationIssueDto($"mesh {node}/{mesh}", "mesh_not_drawn",
            $"The mesh {mesh} of {node} could not be drawn: {message}", "error");
        if (!Issues.Any(existing => existing.Path == issue.Path && existing.Message == issue.Message)) Issues.Add(issue);
    }

    internal static IEnumerable<DesignerTemplateOption> InstalledTemplates()
    {
        try
        {
            return NcEngineMachineCatalog.Load().Machines.Select(machine => new DesignerTemplateOption(machine.Id, machine.Name)).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or PlannerApiException or PlannerProtocolException
            or NotSupportedException or InvalidOperationException;
}

internal static class MachineNcViewerModelDefaults
{
    internal const string Color = "#9AA4AE";
}
