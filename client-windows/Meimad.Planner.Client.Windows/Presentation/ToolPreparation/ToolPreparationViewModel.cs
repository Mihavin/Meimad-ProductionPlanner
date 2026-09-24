using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

internal sealed record ToolShapeOption(string Id, string Name);

internal sealed record ToolComponentTypeOption(string Id, string Name);

/// <summary>The shapes and component kinds the Tool Room can pick; ids are the Server's codes.</summary>
internal static class ToolPreparationCatalog
{
    internal static readonly IReadOnlyList<ToolShapeOption> Shapes =
    [
        new("END_MILL", "Flat end mill"),
        new("BALL_END_MILL", "Ball end mill"),
        new("BULL_NOSE_END_MILL", "Bull-nose end mill"),
        new("CHAMFER_MILL", "Chamfer mill"),
        new("FACE_MILL", "Face mill"),
        new("DRILL", "Drill"),
        new("TAP", "Tap"),
        new("REAMER", "Reamer"),
        new("BORING_BAR", "Boring bar"),
        new("TURNING_TOOL", "Turning tool"),
        new("PROBE", "Probe"),
        new("OTHER", "Other")
    ];

    internal static readonly IReadOnlyList<ToolComponentTypeOption> ComponentTypes =
    [
        new("HOLDER", "Holder"),
        new("EXTENSION", "Extension"),
        new("COLLET", "Collet"),
        new("ARBOR", "Arbor"),
        new("SHANK", "Shank"),
        new("CUTTER", "Cutter"),
        new("INSERT", "Insert"),
        new("OTHER", "Other")
    ];

    internal static ToolShapeOption Shape(string? id) =>
        Shapes.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Shapes[^1];

    internal static ToolComponentTypeOption ComponentType(string? id) =>
        ComponentTypes.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase)) ?? ComponentTypes[^1];
}

internal sealed class ToolPreparationValidationException(string message) : Exception(message);

internal abstract class ToolPreparationObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Millimetre or angle text: invariant decimal point, a comma is accepted as well.</summary>
    internal static double? ParseNumber(string? text, string field)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        var normalized = trimmed.Contains('.') ? trimmed : trimmed.Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < 0)
        {
            throw new ToolPreparationValidationException($"{field} must be a number of millimetres (0 or more).");
        }
        return value;
    }

    internal static string Text(double? value) =>
        value?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;
}

internal sealed class ToolPreparationComponentViewModel : ToolPreparationObservable
{
    private ToolComponentTypeOption type = ToolPreparationCatalog.ComponentTypes[0];
    private string name = string.Empty;
    private string catalogNumber = string.Empty;
    private string lengthText = string.Empty;
    private string diameterText = string.Empty;
    private string notes = string.Empty;

    internal ToolPreparationComponentViewModel(ToolPreparationToolViewModel owner) => Owner = owner;

    internal ToolPreparationToolViewModel Owner { get; }

    public IReadOnlyList<ToolComponentTypeOption> Types => ToolPreparationCatalog.ComponentTypes;

    public ToolComponentTypeOption Type
    {
        get => type;
        set { if (Set(ref type, value ?? ToolPreparationCatalog.ComponentTypes[^1])) Owner.Changed(); }
    }

    public string Name { get => name; set { if (Set(ref name, value ?? string.Empty)) Owner.Changed(); } }
    public string CatalogNumber { get => catalogNumber; set { if (Set(ref catalogNumber, value ?? string.Empty)) Owner.Changed(); } }
    public string LengthText { get => lengthText; set { if (Set(ref lengthText, value ?? string.Empty)) Owner.Changed(); } }
    public string DiameterText { get => diameterText; set { if (Set(ref diameterText, value ?? string.Empty)) Owner.Changed(); } }
    public string Notes { get => notes; set { if (Set(ref notes, value ?? string.Empty)) Owner.Changed(); } }

    internal static ToolPreparationComponentViewModel From(ToolPreparationToolViewModel owner, PlannerToolPreparationComponent component) => new(owner)
    {
        type = ToolPreparationCatalog.ComponentType(component.ComponentType),
        name = component.Name,
        catalogNumber = component.CatalogNumber ?? string.Empty,
        lengthText = Text(component.Length),
        diameterText = Text(component.Diameter),
        notes = component.Notes ?? string.Empty
    };

    internal ToolShapeComponent ToShape() => new(Type.Id, Name.Trim(), TryNumber(LengthText), TryNumber(DiameterText));

    internal ToolPreparationComponentUpdate ToUpdate(int sequence, string tool)
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ToolPreparationValidationException($"Tool {tool}: every component needs a name.");
        return new ToolPreparationComponentUpdate(
            sequence, Type.Id, Name.Trim(),
            string.IsNullOrWhiteSpace(CatalogNumber) ? null : CatalogNumber.Trim(),
            ParseNumber(LengthText, $"Tool {tool}: component length"),
            ParseNumber(DiameterText, $"Tool {tool}: component diameter"),
            string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim());
    }

    private static double? TryNumber(string text)
    {
        try { return ParseNumber(text, "value"); }
        catch (ToolPreparationValidationException) { return null; }
    }
}

/// <summary>One released tool row with its measurements, shape and components.</summary>
internal sealed class ToolPreparationToolViewModel : ToolPreparationObservable
{
    private static readonly string[] DimensionKeys =
        ["cuttingDiameter", "fluteLength", "overallLength", "shankDiameter", "cornerRadius", "pointAngle", "taperAngle", "tipDiameter"];

    private readonly Dictionary<string, string> dimensions = DimensionKeys.ToDictionary(key => key, _ => string.Empty, StringComparer.Ordinal);
    private readonly Action changed;
    private string offsetNumberText;
    private string measuredLengthText;
    private string measuredDiameterText;
    private ToolShapeOption shape;
    private string notes;
    private ToolPreparationComponentViewModel? selectedComponent;

    internal ToolPreparationToolViewModel(PlannerToolPreparationTool tool, Action changed)
    {
        this.changed = changed;
        RowNumber = tool.RowNumber;
        ToolIdentifier = tool.ToolIdentifier;
        Description = tool.Description;
        IsRequired = tool.IsRequired;
        MagazinePosition = tool.MagazinePosition ?? string.Empty;
        offsetNumberText = tool.OffsetNumber?.ToString(CultureInfo.InvariantCulture)
            ?? DefaultOffsetNumber(tool.ToolIdentifier)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        measuredLengthText = Text(tool.MeasuredLength);
        measuredDiameterText = Text(tool.MeasuredDiameter);
        shape = ToolPreparationCatalog.Shape(tool.ShapeType);
        notes = tool.Notes ?? string.Empty;
        foreach (var (key, value) in tool.Shape)
        {
            if (dimensions.ContainsKey(key)) dimensions[key] = Text(value);
        }
        Components = new ObservableCollection<ToolPreparationComponentViewModel>(
            tool.Components.OrderBy(component => component.Sequence).Select(component => ToolPreparationComponentViewModel.From(this, component)));
        selectedComponent = Components.FirstOrDefault();
    }

    public int RowNumber { get; }
    public string ToolIdentifier { get; }
    public string Description { get; }
    public bool IsRequired { get; }
    public string MagazinePosition { get; }
    public string RequiredText => IsRequired ? "Required" : "Optional";
    public IReadOnlyList<ToolShapeOption> Shapes => ToolPreparationCatalog.Shapes;
    public ObservableCollection<ToolPreparationComponentViewModel> Components { get; }

    public string OffsetNumberText { get => offsetNumberText; set { if (Set(ref offsetNumberText, value ?? string.Empty)) Changed(); } }
    public string MeasuredLengthText { get => measuredLengthText; set { if (Set(ref measuredLengthText, value ?? string.Empty)) Changed(); } }
    public string MeasuredDiameterText { get => measuredDiameterText; set { if (Set(ref measuredDiameterText, value ?? string.Empty)) Changed(); } }
    public string Notes { get => notes; set { if (Set(ref notes, value ?? string.Empty)) Changed(); } }

    public ToolShapeOption Shape
    {
        get => shape;
        set { if (Set(ref shape, value ?? ToolPreparationCatalog.Shapes[^1])) { Raise(nameof(ShapeName)); Changed(); } }
    }

    public string ShapeName => shape.Name;

    public string CuttingDiameterText { get => dimensions["cuttingDiameter"]; set => SetDimension("cuttingDiameter", value); }
    public string FluteLengthText { get => dimensions["fluteLength"]; set => SetDimension("fluteLength", value); }
    public string OverallLengthText { get => dimensions["overallLength"]; set => SetDimension("overallLength", value); }
    public string ShankDiameterText { get => dimensions["shankDiameter"]; set => SetDimension("shankDiameter", value); }
    public string CornerRadiusText { get => dimensions["cornerRadius"]; set => SetDimension("cornerRadius", value); }
    public string PointAngleText { get => dimensions["pointAngle"]; set => SetDimension("pointAngle", value); }
    public string TaperAngleText { get => dimensions["taperAngle"]; set => SetDimension("taperAngle", value); }
    public string TipDiameterText { get => dimensions["tipDiameter"]; set => SetDimension("tipDiameter", value); }

    public ToolPreparationComponentViewModel? SelectedComponent { get => selectedComponent; set => Set(ref selectedComponent, value); }

    /// <summary>Length, diameter and an offset number are what the Production Package needs.</summary>
    public bool IsComplete =>
        TryParse(MeasuredLengthText) is not null && TryParse(MeasuredDiameterText) is not null && OffsetNumber() is not null;

    public string CompletionText => IsComplete ? "Measured" : IsRequired ? "Missing" : "Optional";
    public int ComponentCount => Components.Count;

    public ToolShapeGeometry Geometry => ToolShapeBuilder.Build(
        Shape.Id,
        DimensionValues(),
        Components.Select(component => component.ToShape()).ToArray(),
        TryParse(MeasuredLengthText),
        TryParse(MeasuredDiameterText));

    internal ToolPreparationComponentViewModel AddComponent(string? type = null)
    {
        var component = new ToolPreparationComponentViewModel(this)
        {
            Type = ToolPreparationCatalog.ComponentType(type ?? (Components.Count == 0 ? "HOLDER" : "OTHER"))
        };
        Components.Add(component);
        SelectedComponent = component;
        Changed();
        return component;
    }

    internal void RemoveComponent(ToolPreparationComponentViewModel component)
    {
        if (!Components.Remove(component)) return;
        SelectedComponent = Components.LastOrDefault();
        Changed();
    }

    internal ToolPreparationToolUpdate ToUpdate()
    {
        var offset = OffsetNumber();
        if (!string.IsNullOrWhiteSpace(OffsetNumberText) && offset is null)
            throw new ToolPreparationValidationException($"Tool {ToolIdentifier}: the offset number must be a whole number between 1 and 9999.");
        var shapeValues = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, text) in dimensions)
        {
            var value = ParseNumber(text, $"Tool {ToolIdentifier}: {key}");
            if (value is not null) shapeValues[key] = value.Value;
        }
        return new ToolPreparationToolUpdate(
            ToolIdentifier,
            offset,
            ParseNumber(MeasuredLengthText, $"Tool {ToolIdentifier}: measured length"),
            ParseNumber(MeasuredDiameterText, $"Tool {ToolIdentifier}: measured diameter"),
            Shape.Id,
            shapeValues,
            string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            Components.Select((component, index) => component.ToUpdate(index + 1, ToolIdentifier)).ToArray());
    }

    internal void Changed()
    {
        Raise(nameof(Geometry));
        Raise(nameof(IsComplete));
        Raise(nameof(CompletionText));
        Raise(nameof(ComponentCount));
        changed();
    }

    internal static int? DefaultOffsetNumber(string identifier)
    {
        var digits = new string(identifier.Trim().SkipWhile(character => !char.IsDigit(character)).TakeWhile(char.IsDigit).ToArray());
        return digits.Length is > 0 and <= 4 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;
    }

    private int? OffsetNumber() =>
        int.TryParse(OffsetNumberText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 9999
            ? number
            : null;

    private IReadOnlyDictionary<string, double> DimensionValues()
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, text) in dimensions)
        {
            if (TryParse(text) is { } value) values[key] = value;
        }
        return values;
    }

    private void SetDimension(string key, string? value)
    {
        var text = value ?? string.Empty;
        if (dimensions[key] == text) return;
        dimensions[key] = text;
        Raise(key switch
        {
            "cuttingDiameter" => nameof(CuttingDiameterText),
            "fluteLength" => nameof(FluteLengthText),
            "overallLength" => nameof(OverallLengthText),
            "shankDiameter" => nameof(ShankDiameterText),
            "cornerRadius" => nameof(CornerRadiusText),
            "pointAngle" => nameof(PointAngleText),
            "taperAngle" => nameof(TaperAngleText),
            _ => nameof(TipDiameterText)
        });
        Changed();
    }

    private static double? TryParse(string text)
    {
        try { return ParseNumber(text, "value"); }
        catch (ToolPreparationValidationException) { return null; }
    }
}

/// <summary>
/// The Tool Room's editable tool table of one queue item: the released tools with the measured
/// length and diameter, offset number, cutter shape and dimensions, and the assembled components.
/// Saving appends an immutable version on the Server; the Production Package uses the latest one.
/// </summary>
internal sealed class ToolPreparationViewModel : ToolPreparationObservable
{
    private readonly IPlannerApiClient api;
    private readonly string clientId;
    private readonly string userId;
    private PlannerToolPreparation data;
    private ToolPreparationToolViewModel? selectedTool;
    private string status = string.Empty;
    private string comment = string.Empty;
    private bool isBusy;
    private bool isDirty;

    internal ToolPreparationViewModel(IPlannerApiClient api, string clientId, string userId, PlannerToolPreparation data)
    {
        this.api = api;
        this.clientId = clientId;
        this.userId = userId;
        this.data = data;
        Tools = [];
        SaveCommand = new AsyncCommand(SaveAsync, () => !isBusy && isDirty);
        ReloadCommand = new AsyncCommand(ReloadAsync, () => !isBusy);
        AddComponentCommand = new AsyncCommand(() => { SelectedTool?.AddComponent(); return Task.CompletedTask; }, () => SelectedTool is not null);
        RemoveComponentCommand = new AsyncCommand(
            () => { if (SelectedTool is { SelectedComponent: { } component }) SelectedTool.RemoveComponent(component); return Task.CompletedTask; },
            () => SelectedTool?.SelectedComponent is not null);
        Apply(data);
    }

    public ObservableCollection<ToolPreparationToolViewModel> Tools { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand AddComponentCommand { get; }
    public AsyncCommand RemoveComponentCommand { get; }

    public string BatchOperationId => data.BatchOperationId;
    public string Title => $"Tool table — {data.MachineNumber} {data.MachineName}";
    public string ToolTableText => $"Tool Table release r{data.ToolTableRevision} ({data.ToolTableFileName})";
    public string OffsetKindText => data.ToolDiameterOffsetKind == "DIAMETER"
        ? "The control keeps cutter offsets as diameters; the package writes the measured diameter."
        : "The control keeps cutter offsets as radii; the package writes half of the measured diameter.";
    public string MeasurementHint => IsTurning
        ? "Measured length = Z offset, measured diameter = X offset (mm)."
        : "Measured length = tool length offset H, measured diameter = cutter offset D (mm).";
    public bool IsTurning => data.ProcessType.Contains("turn", StringComparison.OrdinalIgnoreCase)
        || data.ProcessType.Contains("lathe", StringComparison.OrdinalIgnoreCase);
    public int Version => data.Version;
    public string SavedText => data.SavedAt is null
        ? "No measurements saved yet."
        : $"Version {data.Version} saved {data.SavedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm} by {data.SavedBy}.";
    public bool ShowsOlderToolTableWarning =>
        data.SavedForToolTableReleaseId is not null && data.SavedForToolTableReleaseId != data.ToolTableReleaseId;
    public string OlderToolTableWarning =>
        "These measurements were saved for a previous Tool Table release. Check every tool, then save again for the current release.";
    public int CompleteCount => Tools.Count(tool => tool.IsComplete);
    public int RequiredMissingCount => Tools.Count(tool => tool.IsRequired && !tool.IsComplete);
    public string ProgressText => RequiredMissingCount == 0
        ? $"All required tools are measured ({CompleteCount} of {Tools.Count} tools)."
        : $"{RequiredMissingCount} required tool(s) still need length, diameter and offset number.";

    public ToolPreparationToolViewModel? SelectedTool
    {
        get => selectedTool;
        set
        {
            if (Set(ref selectedTool, value))
            {
                AddComponentCommand.RaiseCanExecuteChanged();
                RemoveComponentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Status { get => status; private set => Set(ref status, value); }
    public string Comment { get => comment; set { if (Set(ref comment, value ?? string.Empty)) MarkDirty(); } }
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) RaiseCommandStates(); } }
    public bool IsDirty { get => isDirty; private set { if (Set(ref isDirty, value)) RaiseCommandStates(); } }

    /// <summary>The next version as the Server expects it; validation errors name the tool.</summary>
    internal ToolPreparationUpdate BuildUpdate() => new(
        data.Version,
        data.ToolTableReleaseId,
        string.IsNullOrWhiteSpace(Comment) ? null : Comment.Trim(),
        Tools.Select(tool => tool.ToUpdate()).ToArray());

    internal async Task SaveAsync()
    {
        if (IsBusy) return;
        ToolPreparationUpdate update;
        try
        {
            update = BuildUpdate();
        }
        catch (ToolPreparationValidationException exception)
        {
            Status = exception.Message;
            return;
        }
        IsBusy = true;
        try
        {
            var saved = await api.SaveToolPreparationAsync(data.BatchOperationId, update, clientId, userId);
            Apply(saved);
            Comment = string.Empty;
            IsDirty = false;
            Status = $"Saved version {saved.Version}. {ProgressText}";
        }
        catch (PlannerApiException exception) when (exception.Code is "tool_preparation_version_conflict" or "tool_preparation_tool_table_changed")
        {
            Status = $"{exception.Message} Reload to see the current values; your entries are kept until then.";
        }
        catch (Exception exception) when (exception is PlannerApiException or HttpRequestException or TaskCanceledException)
        {
            Status = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task ReloadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Apply(await api.GetToolPreparationAsync(data.BatchOperationId));
            IsDirty = false;
            Status = SavedText;
        }
        catch (Exception exception) when (exception is PlannerApiException or HttpRequestException or TaskCanceledException)
        {
            Status = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal void Apply(PlannerToolPreparation value)
    {
        var selectedIdentifier = SelectedTool?.ToolIdentifier;
        data = value;
        Tools.Clear();
        foreach (var tool in value.Tools.OrderBy(tool => tool.RowNumber))
        {
            Tools.Add(new ToolPreparationToolViewModel(tool, MarkDirty));
        }
        SelectedTool = Tools.FirstOrDefault(tool => tool.ToolIdentifier == selectedIdentifier) ?? Tools.FirstOrDefault();
        foreach (var name in new[] { nameof(Title), nameof(ToolTableText), nameof(OffsetKindText), nameof(MeasurementHint), nameof(Version),
                     nameof(SavedText), nameof(ShowsOlderToolTableWarning), nameof(CompleteCount), nameof(RequiredMissingCount), nameof(ProgressText) })
        {
            Raise(name);
        }
    }

    private void MarkDirty()
    {
        IsDirty = true;
        Raise(nameof(CompleteCount));
        Raise(nameof(RequiredMissingCount));
        Raise(nameof(ProgressText));
    }

    private void RaiseCommandStates()
    {
        SaveCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
        AddComponentCommand.RaiseCanExecuteChanged();
        RemoveComponentCommand.RaiseCanExecuteChanged();
    }
}
