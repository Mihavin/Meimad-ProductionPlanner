using System.Collections.ObjectModel;
using System.Net.Http;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;

namespace Meimad.Planner.Client.Windows.Presentation.ToolCatalog;

/// <summary>One external id row of the catalog editor.</summary>
internal sealed class ToolExternalIdViewModel(ToolCatalogEditorViewModel owner) : ToolPreparationObservable
{
    private string system = string.Empty;
    private string value = string.Empty;

    public string System { get => system; set { if (Set(ref system, value ?? string.Empty)) owner.Changed(); } }
    public string Value { get => value; set { if (Set(ref this.value, value ?? string.Empty)) owner.Changed(); } }
}

/// <summary>The editor of one catalog tool (a new one until the first save).</summary>
internal sealed class ToolCatalogEditorViewModel : ToolPreparationObservable
{
    private static readonly string[] AttributeKeys = ["insertCode", "holderCode", "threadProfile", "material", "coating", "manufacturer"];
    private readonly ToolDimensionSet dimensions;
    private readonly Dictionary<string, string> attributes = AttributeKeys.ToDictionary(key => key, _ => string.Empty, StringComparer.Ordinal);
    private readonly Action changed;
    private string? catalogToolId;
    private int version;
    private string internalCode = string.Empty;
    private string name = string.Empty;
    private ToolShapeOption shape = ToolPreparationCatalog.Shapes[0];
    private ToolHandOption hand = ToolPreparationCatalog.Hands[0];
    private string description = string.Empty;
    private bool isActive = true;
    private ToolExternalIdViewModel? selectedExternalId;
    private bool loading;

    internal ToolCatalogEditorViewModel(Action changed)
    {
        this.changed = changed;
        dimensions = new ToolDimensionSet(Changed);
        dimensions.ShowFor(shape);
    }

    public IReadOnlyList<ToolShapeOption> Shapes => ToolPreparationCatalog.Shapes;
    public IReadOnlyList<ToolHandOption> Hands => ToolPreparationCatalog.Hands;
    public ObservableCollection<ToolDimensionFieldViewModel> DimensionFields => dimensions.Fields;
    public ObservableCollection<ToolExternalIdViewModel> ExternalIds { get; } = [];

    public string? CatalogToolId => catalogToolId;
    public int Version => version;
    public bool IsNew => catalogToolId is null;
    public string Heading => IsNew ? "New catalog tool" : $"{internalCode} (version {version})";
    public string InternalCode => IsNew ? "Assigned by the Server on save" : internalCode;

    public string Name { get => name; set { if (Set(ref name, value ?? string.Empty)) Changed(); } }
    public string Description { get => description; set { if (Set(ref description, value ?? string.Empty)) Changed(); } }
    public bool IsActive { get => isActive; set { if (Set(ref isActive, value)) Changed(); } }

    public ToolShapeOption Shape
    {
        get => shape;
        set
        {
            if (!Set(ref shape, value ?? ToolPreparationCatalog.Shapes[^1])) return;
            dimensions.ShowFor(shape);
            Raise(nameof(HasHand));
            Changed();
        }
    }

    public bool HasHand => shape.HasHand;

    public ToolHandOption Hand { get => hand; set { if (Set(ref hand, value ?? ToolPreparationCatalog.Hands[0])) Changed(); } }

    public string InsertCode { get => attributes["insertCode"]; set => SetAttribute("insertCode", value); }
    public string HolderCode { get => attributes["holderCode"]; set => SetAttribute("holderCode", value); }
    public string ThreadProfile { get => attributes["threadProfile"]; set => SetAttribute("threadProfile", value); }
    public string Material { get => attributes["material"]; set => SetAttribute("material", value); }
    public string Coating { get => attributes["coating"]; set => SetAttribute("coating", value); }
    public string Manufacturer { get => attributes["manufacturer"]; set => SetAttribute("manufacturer", value); }

    public ToolExternalIdViewModel? SelectedExternalId { get => selectedExternalId; set => Set(ref selectedExternalId, value); }

    public ToolShapeGeometry Geometry => ToolShapeBuilder.Build(shape.Id, dimensions.Preview(), [], null, null);

    internal void LoadNew()
    {
        loading = true;
        catalogToolId = null;
        version = 0;
        internalCode = string.Empty;
        name = string.Empty;
        description = string.Empty;
        isActive = true;
        shape = ToolPreparationCatalog.Shapes[0];
        hand = ToolPreparationCatalog.Hands[0];
        foreach (var key in AttributeKeys) attributes[key] = string.Empty;
        dimensions.Load(new Dictionary<string, double>());
        dimensions.ShowFor(shape);
        ExternalIds.Clear();
        selectedExternalId = null;
        loading = false;
        RaiseAll();
    }

    internal void Load(PlannerCatalogTool tool)
    {
        loading = true;
        catalogToolId = tool.CatalogToolId;
        version = tool.Version;
        internalCode = tool.InternalCode;
        name = tool.Name;
        description = tool.Description ?? string.Empty;
        isActive = tool.IsActive;
        shape = ToolPreparationCatalog.Shape(tool.ToolType);
        hand = ToolPreparationCatalog.Hand(tool.Hand);
        foreach (var key in AttributeKeys) attributes[key] = tool.Attributes.TryGetValue(key, out var value) ? value : string.Empty;
        dimensions.Load(tool.Shape);
        dimensions.ShowFor(shape);
        ExternalIds.Clear();
        foreach (var entry in tool.ExternalIds) ExternalIds.Add(new ToolExternalIdViewModel(this) { System = entry.System, Value = entry.Value });
        selectedExternalId = ExternalIds.FirstOrDefault();
        loading = false;
        RaiseAll();
    }

    internal ToolExternalIdViewModel AddExternalId()
    {
        var entry = new ToolExternalIdViewModel(this);
        ExternalIds.Add(entry);
        SelectedExternalId = entry;
        Changed();
        return entry;
    }

    internal void RemoveExternalId(ToolExternalIdViewModel entry)
    {
        if (!ExternalIds.Remove(entry)) return;
        SelectedExternalId = ExternalIds.LastOrDefault();
        Changed();
    }

    /// <summary>The create or replace request; a validation error names the field.</summary>
    internal CatalogToolUpdate BuildUpdate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ToolPreparationValidationException("The tool needs a name.");
        var externalIds = new List<PlannerCatalogToolExternalId>();
        foreach (var entry in ExternalIds)
        {
            if (string.IsNullOrWhiteSpace(entry.System) && string.IsNullOrWhiteSpace(entry.Value)) continue;
            if (string.IsNullOrWhiteSpace(entry.System) || string.IsNullOrWhiteSpace(entry.Value))
                throw new ToolPreparationValidationException("Every external id needs both the system and the value.");
            externalIds.Add(new PlannerCatalogToolExternalId(entry.System.Trim(), entry.Value.Trim()));
        }
        var attributeValues = attributes
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.Ordinal);
        return new CatalogToolUpdate(
            Name.Trim(), Shape.Id, HasHand ? Hand.Id : null,
            string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            dimensions.Parse(string.IsNullOrWhiteSpace(Name) ? "Tool" : Name.Trim()),
            attributeValues, externalIds, IsActive,
            IsNew ? null : version);
    }

    internal void Changed()
    {
        if (loading) return;
        Raise(nameof(Geometry));
        changed();
    }

    private void SetAttribute(string key, string? value)
    {
        var text = value ?? string.Empty;
        if (attributes[key] == text) return;
        attributes[key] = text;
        Changed();
    }

    private void RaiseAll()
    {
        foreach (var property in new[]
                 {
                     nameof(CatalogToolId), nameof(Version), nameof(IsNew), nameof(Heading), nameof(InternalCode), nameof(Name),
                     nameof(Description), nameof(IsActive), nameof(Shape), nameof(HasHand), nameof(Hand), nameof(InsertCode),
                     nameof(HolderCode), nameof(ThreadProfile), nameof(Material), nameof(Coating), nameof(Manufacturer),
                     nameof(SelectedExternalId), nameof(Geometry)
                 })
        {
            Raise(property);
        }
    }
}

/// <summary>
/// The tool catalog page: search the factory's tool definitions by internal code, name or an
/// external id, filter them by type, and create or edit them. Saving needs no Edit Mode; the
/// Server assigns the internal id and guards concurrent edits by version.
/// </summary>
internal sealed class ToolCatalogViewModel : ToolPreparationObservable
{
    private static readonly ToolShapeOption AllTypes = new("", "All types", "OTHER", []);
    private IPlannerApiClient? api;
    private string clientId = string.Empty;
    private string userId = string.Empty;
    private string searchText = string.Empty;
    private ToolShapeOption typeFilter = AllTypes;
    private bool includeInactive;
    private PlannerCatalogTool? selected;
    private string status = "Connect to the Server to see the tool catalog.";
    private bool isBusy;
    private bool isDirty;

    internal ToolCatalogViewModel()
    {
        Editor = new ToolCatalogEditorViewModel(MarkDirty);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => api is not null && !isBusy);
        NewCommand = new AsyncCommand(() => { BeginNew(); return Task.CompletedTask; }, () => api is not null && !isBusy);
        SaveCommand = new AsyncCommand(SaveAsync, () => api is not null && !isBusy && isDirty);
        DeleteCommand = new AsyncCommand(DeleteSelectedAsync, () => api is not null && !isBusy && selected is not null);
        AddExternalIdCommand = new AsyncCommand(() => { Editor.AddExternalId(); return Task.CompletedTask; }, () => api is not null);
        RemoveExternalIdCommand = new AsyncCommand(
            () => { if (Editor.SelectedExternalId is { } entry) Editor.RemoveExternalId(entry); return Task.CompletedTask; },
            () => Editor.SelectedExternalId is not null);
        TypeFilters = [AllTypes, .. ToolPreparationCatalog.Shapes];
    }

    public ObservableCollection<PlannerCatalogTool> Tools { get; } = [];
    public IReadOnlyList<ToolShapeOption> TypeFilters { get; }
    public ToolCatalogEditorViewModel Editor { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand NewCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand AddExternalIdCommand { get; }
    public AsyncCommand RemoveExternalIdCommand { get; }

    public bool IsConnected => api is not null;
    public string SearchText { get => searchText; set => Set(ref searchText, value ?? string.Empty); }
    public ToolShapeOption TypeFilter { get => typeFilter; set { if (Set(ref typeFilter, value ?? AllTypes)) _ = RefreshAsync(); } }
    public bool IncludeInactive { get => includeInactive; set { if (Set(ref includeInactive, value)) _ = RefreshAsync(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) RaiseCommandStates(); } }
    public bool IsDirty { get => isDirty; private set { if (Set(ref isDirty, value)) RaiseCommandStates(); } }
    public string CountText => Tools.Count == 1 ? "1 tool" : $"{Tools.Count} tools";

    public PlannerCatalogTool? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            if (value is not null) Editor.Load(value);
            IsDirty = false;
            RaiseCommandStates();
        }
    }

    internal void AttachSession(IPlannerApiClient? client, string? activeClientId, string? activeUserId)
    {
        api = client;
        clientId = activeClientId ?? string.Empty;
        userId = activeUserId ?? string.Empty;
        Raise(nameof(IsConnected));
        RaiseCommandStates();
        if (client is null)
        {
            Tools.Clear();
            Status = "Connect to the Server to see the tool catalog.";
            Raise(nameof(CountText));
            return;
        }
        _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (api is not { } client || IsBusy) return;
        IsBusy = true;
        try
        {
            var tools = await client.ListCatalogToolsAsync(
                string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                string.IsNullOrEmpty(TypeFilter.Id) ? null : TypeFilter.Id,
                IncludeInactive);
            var selectedId = selected?.CatalogToolId;
            Tools.Clear();
            foreach (var tool in tools) Tools.Add(tool);
            Raise(nameof(CountText));
            var keep = Tools.FirstOrDefault(tool => tool.CatalogToolId == selectedId);
            if (keep is not null)
            {
                selected = keep;
                Raise(nameof(Selected));
                if (!IsDirty) Editor.Load(keep);
            }
            else if (selected is not null)
            {
                Selected = Tools.FirstOrDefault();
            }
            Status = Tools.Count == 0
                ? (string.IsNullOrWhiteSpace(SearchText) ? "The catalog is empty. Add the first tool with New." : "No catalog tool matches the search.")
                : $"{CountText} listed.";
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

    internal void BeginNew()
    {
        selected = null;
        Raise(nameof(Selected));
        Editor.LoadNew();
        IsDirty = false;
        RaiseCommandStates();
        Status = "Describe the new tool and save it; the Server assigns its internal id.";
    }

    internal async Task SaveAsync()
    {
        if (api is not { } client || IsBusy) return;
        CatalogToolUpdate update;
        try
        {
            update = Editor.BuildUpdate();
        }
        catch (ToolPreparationValidationException exception)
        {
            Status = exception.Message;
            return;
        }
        IsBusy = true;
        try
        {
            var saved = Editor.IsNew
                ? await client.CreateCatalogToolAsync(update, clientId, userId)
                : await client.UpdateCatalogToolAsync(Editor.CatalogToolId!, update, clientId, userId);
            IsDirty = false;
            selected = saved;
            Editor.Load(saved);
            Raise(nameof(Selected));
            Status = $"Saved {saved.InternalCode} {saved.Name} (version {saved.Version}).";
            await RefreshListKeepingAsync(saved);
        }
        catch (PlannerApiException exception) when (exception.Code == "tool_catalog_version_conflict")
        {
            Status = $"{exception.Message} Refresh to see the current values; your entries are kept until then.";
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

    internal async Task DeleteSelectedAsync()
    {
        if (api is not { } client || selected is not { } tool || IsBusy) return;
        IsBusy = true;
        try
        {
            await client.DeleteCatalogToolAsync(tool.CatalogToolId, clientId, userId);
            Status = $"Deleted {tool.InternalCode} {tool.Name}.";
            selected = null;
            Raise(nameof(Selected));
            Editor.LoadNew();
            IsDirty = false;
            await RefreshListKeepingAsync(null);
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

    private async Task RefreshListKeepingAsync(PlannerCatalogTool? keep)
    {
        if (api is not { } client) return;
        try
        {
            var tools = await client.ListCatalogToolsAsync(
                string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                string.IsNullOrEmpty(TypeFilter.Id) ? null : TypeFilter.Id,
                IncludeInactive);
            Tools.Clear();
            foreach (var entry in tools) Tools.Add(entry);
            if (keep is not null && Tools.All(entry => entry.CatalogToolId != keep.CatalogToolId)) Tools.Add(keep);
            Raise(nameof(CountText));
            if (keep is not null)
            {
                selected = Tools.First(entry => entry.CatalogToolId == keep.CatalogToolId);
                Raise(nameof(Selected));
            }
        }
        catch (Exception exception) when (exception is PlannerApiException or HttpRequestException or TaskCanceledException)
        {
            Status = exception.Message;
        }
    }

    private void MarkDirty()
    {
        IsDirty = true;
        RemoveExternalIdCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        NewCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        AddExternalIdCommand.RaiseCanExecuteChanged();
        RemoveExternalIdCommand.RaiseCanExecuteChanged();
    }
}
