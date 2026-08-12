using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Formatting;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class CaseWorkspaceViewModel : INotifyPropertyChanged
{
    private readonly IWorkingFolderLauncher folderLauncher;
    private IPlannerApiClient? apiClient;
    private string clientId = string.Empty;
    private long editGeneration;
    private bool isEditor;
    private bool hasLoaded;
    private bool isBusy;
    private bool isCreating;
    private bool isCreatingOperation;
    private bool isEditingOperation;
    private bool isCreatingOrder;
    private bool isCreatingBatch;
    private bool selectedDetailsAreStale;
    private CasePoolItemViewModel? selectedCase;
    private CaseOperation? selectedOperation;
    private PlannerOrder? selectedOrder;
    private ProductionBatch? selectedBatch;
    private string? entityTag;
    private string searchText = string.Empty;
    private string customerFilter = string.Empty;
    private string activeFilter = "All";
    private string statusMessage = "Connect to the Server to load Cases.";
    private BitmapImage? detailPreview;
    private string partNumber = string.Empty;
    private string name = string.Empty;
    private string revision = string.Empty;
    private string customer = string.Empty;
    private string customerReference = string.Empty;
    private string previewPath = string.Empty;
    private string workingFolderPath = string.Empty;
    private string materialType = string.Empty;
    private string materialSpecification = string.Empty;
    private string rawMaterialForm = string.Empty;
    private string rawMaterialDimensions = string.Empty;
    private string currentSetupTime = DurationText.Format(0);
    private string currentCycleTimePerPart = DurationText.Format(0);
    private string notes = string.Empty;
    private string activeStateText = string.Empty;
    private string newOperationNumber = string.Empty;
    private string newOperationName = string.Empty;
    private string newOperationRequiredMachineType = string.Empty;
    private string newOperationSetupTime = string.Empty;
    private string newOperationCycleTimePerPart = string.Empty;
    private string newOperationDependencyType = "INDEPENDENT";
    private CaseOperation? newOperationPredecessor;
    private string newOperationSimultaneousGroupKey = string.Empty;
    private string newOrderNumber = string.Empty;
    private string newOrderQuantity = string.Empty;
    private string newOrderWorkFinishDate = string.Empty;
    private string newOrderStatus = "active";
    private string newOrderNotes = string.Empty;
    private string newBatchNumber = string.Empty;
    private string newBatchPlannedQuantity = string.Empty;
    private string newBatchStockQuantity = string.Empty;
    private string newBatchScrapAllowance = string.Empty;

    internal CaseWorkspaceViewModel(IWorkingFolderLauncher folderLauncher)
    {
        this.folderLauncher = folderLauncher;
        SearchCommand = new AsyncCommand(LoadCasesAsync, () => apiClient is not null && !IsBusy);
        ClearFiltersCommand = new AsyncCommand(ClearFiltersAsync, () => apiClient is not null && !IsBusy);
        SaveCommand = new AsyncCommand(SaveAsync, () => CanSave);
        BeginCreateCommand = new AsyncCommand(BeginCreateAsync, () => CanBeginCreate);
        CancelCreateCommand = new AsyncCommand(CancelCreateAsync, () => IsCreating && !IsBusy);
        RefreshDetailsCommand = new AsyncCommand(LoadSelectedCaseSafeAsync, () => SelectedCase is not null && !IsBusy);
        OpenWorkingFolderCommand = new AsyncCommand(OpenWorkingFolderAsync, () => CanOpenWorkingFolder);
        BeginCreateOperationCommand = new AsyncCommand(BeginCreateOperationAsync, () => CanBeginChildCreate);
        BeginEditOperationCommand = new AsyncCommand(BeginEditOperationAsync, () => CanBeginEditOperation);
        CancelCreateOperationCommand = new AsyncCommand(CancelCreateOperationAsync, () => IsCreatingOperation && !IsBusy);
        CreateOperationCommand = new AsyncCommand(CreateOperationAsync, () => CanCreateOperation);
        BeginCreateOrderCommand = new AsyncCommand(BeginCreateOrderAsync, () => CanBeginChildCreate);
        CancelCreateOrderCommand = new AsyncCommand(CancelCreateOrderAsync, () => IsCreatingOrder && !IsBusy);
        CreateOrderCommand = new AsyncCommand(CreateOrderAsync, () => CanCreateOrder);
        BeginCreateBatchCommand = new AsyncCommand(BeginCreateBatchAsync, () => CanBeginChildCreate);
        CancelCreateBatchCommand = new AsyncCommand(CancelCreateBatchAsync, () => IsCreatingBatch && !IsBusy);
        CreateBatchCommand = new AsyncCommand(CreateBatchAsync, () => CanCreateBatch);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? PlanChanged;

    public ObservableCollection<CasePoolItemViewModel> Cases { get; } = [];

    public ObservableCollection<CaseOperation> Operations { get; } = [];

    public ObservableCollection<CaseOperation> OperationReferenceOptions { get; } = [];

    public ObservableCollection<string> OperationMachineTypeOptions { get; } = [string.Empty];

    public ObservableCollection<PlannerOrder> Orders { get; } = [];

    public ObservableCollection<ProductionBatch> Batches { get; } = [];

    public ObservableCollection<BatchOrderAllocationViewModel> BatchOrderAllocations { get; } = [];

    public IReadOnlyList<string> ActiveFilters { get; } = ["All", "Active", "Inactive"];

    public IReadOnlyList<string> OrderStatuses { get; } = ["active", "complete", "cancelled"];

    public IReadOnlyList<string> OperationDependencyTypes { get; } =
        ["INDEPENDENT", "SEQUENTIAL", "PARALLEL_CAPABLE", "LOCKED_SIMULTANEOUS"];

    public AsyncCommand SearchCommand { get; }

    public AsyncCommand ClearFiltersCommand { get; }

    public AsyncCommand SaveCommand { get; }

    public AsyncCommand BeginCreateCommand { get; }

    public AsyncCommand CancelCreateCommand { get; }

    public AsyncCommand RefreshDetailsCommand { get; }

    public AsyncCommand OpenWorkingFolderCommand { get; }

    public AsyncCommand BeginCreateOperationCommand { get; }

    public AsyncCommand BeginEditOperationCommand { get; }

    public AsyncCommand CancelCreateOperationCommand { get; }

    public AsyncCommand CreateOperationCommand { get; }

    public AsyncCommand BeginCreateOrderCommand { get; }

    public AsyncCommand CancelCreateOrderCommand { get; }

    public AsyncCommand CreateOrderCommand { get; }

    public AsyncCommand BeginCreateBatchCommand { get; }

    public AsyncCommand CancelCreateBatchCommand { get; }

    public AsyncCommand CreateBatchCommand { get; }

    public string SearchText
    {
        get => searchText;
        set => SetField(ref searchText, value);
    }

    public string CustomerFilter
    {
        get => customerFilter;
        set => SetField(ref customerFilter, value);
    }

    public string ActiveFilter
    {
        get => activeFilter;
        set => SetField(ref activeFilter, value);
    }

    public CasePoolItemViewModel? SelectedCase
    {
        get => selectedCase;
        set
        {
            if (SetField(ref selectedCase, value))
            {
                if (value is not null)
                {
                    isCreating = false;
                }
                isCreatingOperation = false;
                isEditingOperation = false;
                isCreatingOrder = false;
                isCreatingBatch = false;
                ResetOperationForm();
                ResetOrderForm();
                ResetBatchForm();
                RaiseStateProperties();
                _ = LoadSelectedCaseSafeAsync();
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool HasSelection => SelectedCase is not null;

    public bool HasForm => HasSelection || IsCreating;

    public bool IsCreating => isCreating;

    public bool IsFormReadOnly => !isEditor;

    public bool CanSave => isEditor && HasForm && !IsBusy;

    public bool CanBeginCreate => isEditor && apiClient is not null && !IsBusy;

    public bool CanEditForm => isEditor && !IsBusy;
    public bool CanDelete => isEditor && apiClient is not null && !IsBusy;

    public CaseOperation? SelectedOperation
    {
        get => selectedOperation;
        set
        {
            if (SetField(ref selectedOperation, value))
            {
                OnPropertyChanged(nameof(CanBeginEditOperation));
                BeginEditOperationCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public PlannerOrder? SelectedOrder { get => selectedOrder; set => SetField(ref selectedOrder, value); }
    public ProductionBatch? SelectedBatch { get => selectedBatch; set => SetField(ref selectedBatch, value); }

    public string FormHeading => IsCreating ? "NEW CASE" : "CASE DETAILS";

    public string SaveButtonText => IsCreating ? "Create Case" : "Save Case";

    public bool CanOpenWorkingFolder =>
        SelectedCase is not null && !string.IsNullOrWhiteSpace(WorkingFolderPath) && !IsBusy;

    public bool IsCreatingOrder => isCreatingOrder;

    public bool IsCreatingBatch => isCreatingBatch;

    public bool IsCreatingOperation => isCreatingOperation || isEditingOperation;

    public bool IsEditingOperation => isEditingOperation;

    public string OperationFormHeading => isEditingOperation
        ? "EDIT CASE OPERATION"
        : "NEW CASE OPERATION";

    public string OperationSaveButtonText => isEditingOperation
        ? "Save Operation"
        : "Create Operation";

    public bool CanBeginChildCreate =>
        isEditor && apiClient is not null && SelectedCase is not null && !IsCreating && !IsBusy;

    public bool CanCreateOrder => IsCreatingOrder && CanBeginChildCreate;

    public bool CanCreateBatch => IsCreatingBatch && CanBeginChildCreate;

    public bool CanCreateOperation => IsCreatingOperation && CanBeginChildCreate;

    public bool CanBeginEditOperation =>
        CanBeginChildCreate && SelectedOperation is not null && !IsCreatingOperation;

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetField(ref statusMessage, value);
    }

    public BitmapImage? DetailPreview
    {
        get => detailPreview;
        private set => SetField(ref detailPreview, value);
    }

    public string PartNumber { get => partNumber; set => SetField(ref partNumber, value); }
    public string Name { get => name; set => SetField(ref name, value); }
    public string Revision { get => revision; set => SetField(ref revision, value); }
    public string Customer { get => customer; set => SetField(ref customer, value); }
    public string CustomerReference { get => customerReference; set => SetField(ref customerReference, value); }
    public string PreviewPath { get => previewPath; set => SetField(ref previewPath, value); }
    public string WorkingFolderPath
    {
        get => workingFolderPath;
        set
        {
            if (SetField(ref workingFolderPath, value))
            {
                RaiseStateProperties();
            }
        }
    }
    public string MaterialType { get => materialType; set => SetField(ref materialType, value); }
    public string MaterialSpecification { get => materialSpecification; set => SetField(ref materialSpecification, value); }
    public string RawMaterialForm { get => rawMaterialForm; set => SetField(ref rawMaterialForm, value); }
    public string RawMaterialDimensions { get => rawMaterialDimensions; set => SetField(ref rawMaterialDimensions, value); }
    public string CurrentSetupTime { get => currentSetupTime; private set => SetField(ref currentSetupTime, value); }
    public string CurrentCycleTimePerPart { get => currentCycleTimePerPart; private set => SetField(ref currentCycleTimePerPart, value); }
    public string Notes { get => notes; set => SetField(ref notes, value); }
    public string ActiveStateText { get => activeStateText; private set => SetField(ref activeStateText, value); }
    public string NewOperationNumber { get => newOperationNumber; set => SetField(ref newOperationNumber, value); }
    public string NewOperationName { get => newOperationName; set => SetField(ref newOperationName, value); }
    public string NewOperationRequiredMachineType { get => newOperationRequiredMachineType; set => SetField(ref newOperationRequiredMachineType, value); }
    public string NewOperationSetupTime { get => newOperationSetupTime; set => SetField(ref newOperationSetupTime, value); }
    public string NewOperationCycleTimePerPart { get => newOperationCycleTimePerPart; set => SetField(ref newOperationCycleTimePerPart, value); }
    public string NewOperationDependencyType
    {
        get => newOperationDependencyType;
        set
        {
            if (!SetField(ref newOperationDependencyType, value))
            {
                return;
            }

            if (value == "INDEPENDENT")
            {
                NewOperationPredecessor = null;
            }

            if (value != "LOCKED_SIMULTANEOUS")
            {
                NewOperationSimultaneousGroupKey = string.Empty;
            }
        }
    }
    public CaseOperation? NewOperationPredecessor { get => newOperationPredecessor; set => SetField(ref newOperationPredecessor, value); }
    public string NewOperationSimultaneousGroupKey { get => newOperationSimultaneousGroupKey; set => SetField(ref newOperationSimultaneousGroupKey, value); }
    public string NewOrderNumber { get => newOrderNumber; set => SetField(ref newOrderNumber, value); }
    public string NewOrderQuantity { get => newOrderQuantity; set => SetField(ref newOrderQuantity, value); }
    public string NewOrderWorkFinishDate { get => newOrderWorkFinishDate; set => SetField(ref newOrderWorkFinishDate, value); }
    public string NewOrderStatus { get => newOrderStatus; set => SetField(ref newOrderStatus, value); }
    public string NewOrderNotes { get => newOrderNotes; set => SetField(ref newOrderNotes, value); }
    public string NewBatchNumber { get => newBatchNumber; set => SetField(ref newBatchNumber, value); }
    public string NewBatchPlannedQuantity { get => newBatchPlannedQuantity; set => SetField(ref newBatchPlannedQuantity, value); }
    public string NewBatchStockQuantity { get => newBatchStockQuantity; set => SetField(ref newBatchStockQuantity, value); }
    public string NewBatchScrapAllowance { get => newBatchScrapAllowance; set => SetField(ref newBatchScrapAllowance, value); }

    internal void AttachSession(
        IPlannerApiClient? newApiClient,
        string newClientId,
        EditModeStatus? editStatus)
    {
        if (!ReferenceEquals(apiClient, newApiClient))
        {
            apiClient = newApiClient;
            hasLoaded = false;
            Cases.Clear();
            ClearDetails();
            OperationMachineTypeOptions.Clear();
            OperationMachineTypeOptions.Add(string.Empty);
        }

        clientId = newClientId;
        isEditor = editStatus?.State == ClientEditState.Editor;
        editGeneration = editStatus?.Generation ?? 0;
        RaiseStateProperties();
    }

    internal async Task EnsureLoadedAsync()
    {
        if (!hasLoaded && apiClient is not null)
        {
            await LoadCasesAsync();
        }
        else if (selectedDetailsAreStale && apiClient is not null && SelectedCase is not null)
        {
            await LoadSelectedCaseSafeAsync();
        }
    }

    internal void InvalidateSelectedDetails() => selectedDetailsAreStale = true;

    internal async Task LoadCasesAsync()
    {
        if (apiClient is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var active = ActiveFilter switch
            {
                "Active" => true,
                "Inactive" => false,
                _ => (bool?)null
            };
            var cases = await apiClient.ListCasesAsync(new CaseQuery(SearchText, CustomerFilter, active));
            var selectedId = SelectedCase?.CaseId;
            Cases.Clear();
            foreach (var plannerCase in cases)
            {
                var item = new CasePoolItemViewModel(plannerCase);
                Cases.Add(item);
                item.Thumbnail = ToBitmap(await apiClient.GetCasePreviewAsync(plannerCase.CaseId));
            }

            hasLoaded = true;
            StatusMessage = $"{Cases.Count} Case{(Cases.Count == 1 ? string.Empty : "s")} loaded from the Server.";
            SelectedCase = Cases.FirstOrDefault(item => item.CaseId == selectedId) ?? Cases.FirstOrDefault();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }

        if (SelectedCase is not null)
        {
            await LoadSelectedCaseSafeAsync();
        }
    }

    internal Task BeginCreateAsync()
    {
        if (!CanBeginCreate)
        {
            return Task.CompletedTask;
        }

        selectedCase = null;
        OnPropertyChanged(nameof(SelectedCase));
        isCreating = true;
        entityTag = null;
        ClearFormValues();
        ActiveStateText = "New Case";
        StatusMessage = "Enter the Case master data, external Working Folder, and optional picture path.";
        RaiseStateProperties();
        return Task.CompletedTask;
    }

    internal Task CancelCreateAsync()
    {
        if (!IsCreating)
        {
            return Task.CompletedTask;
        }

        isCreating = false;
        ClearDetails();
        SelectedCase = Cases.FirstOrDefault();
        StatusMessage = SelectedCase is null
            ? "No Case is selected."
            : "New Case entry cancelled.";
        return Task.CompletedTask;
    }

    internal Task BeginCreateOperationAsync()
    {
        if (!CanBeginChildCreate)
        {
            return Task.CompletedTask;
        }

        isCreatingOrder = false;
        isCreatingBatch = false;
        ResetOrderForm();
        ResetBatchForm();
        isEditingOperation = false;
        isCreatingOperation = true;
        ResetOperationForm();
        RebuildOperationReferenceOptions(null);
        StatusMessage = "Enter the next Case route operation. The Server appends it and validates all dependency semantics.";
        RaiseStateProperties();
        return Task.CompletedTask;
    }

    internal Task BeginEditOperationAsync()
    {
        var operation = SelectedOperation;
        if (!CanBeginEditOperation || operation is null)
        {
            return Task.CompletedTask;
        }

        isCreatingOrder = false;
        isCreatingBatch = false;
        ResetOrderForm();
        ResetBatchForm();
        isCreatingOperation = false;
        isEditingOperation = true;
        ResetOperationForm();
        RebuildOperationReferenceOptions(operation.CaseOperationId);
        NewOperationNumber = operation.OperationNumber.ToString(CultureInfo.InvariantCulture);
        NewOperationName = operation.Name;
        EnsureMachineTypeOption(operation.RequiredMachineType);
        NewOperationRequiredMachineType = operation.RequiredMachineType ?? string.Empty;
        NewOperationSetupTime = DurationText.FormatOptional(operation.SetupTimeSeconds);
        NewOperationCycleTimePerPart = DurationText.FormatOptional(operation.CycleTimePerPartSeconds);
        NewOperationDependencyType = operation.DependencyType;
        NewOperationPredecessor = OperationReferenceOptions.FirstOrDefault(value =>
            value.CaseOperationId == operation.PredecessorCaseOperationId);
        NewOperationSimultaneousGroupKey = operation.SimultaneousGroupKey ?? string.Empty;
        StatusMessage = $"Editing Case Operation {operation.OperationNumber}. Existing Production Batch snapshots will not be changed.";
        RaiseStateProperties();
        return Task.CompletedTask;
    }

    internal Task CancelCreateOperationAsync()
    {
        var wasEditing = isEditingOperation;
        isCreatingOperation = false;
        isEditingOperation = false;
        ResetOperationForm();
        StatusMessage = wasEditing
            ? "Case Operation edit cancelled."
            : "New Case Operation entry cancelled.";
        RaiseStateProperties();
        return Task.CompletedTask;
    }

    internal async Task CreateOperationAsync()
    {
        if (apiClient is null || SelectedCase is null || !CanCreateOperation)
        {
            return;
        }

        if (!TryParsePositiveQuantity(NewOperationNumber, "Operation number", out var operationNumber))
        {
            return;
        }

        if (!DurationText.TryParseOptional(NewOperationSetupTime, out var setupSeconds)
            || !DurationText.TryParseOptional(NewOperationCycleTimePerPart, out var cycleSeconds))
        {
            StatusMessage = "Setup and cycle time must use HH:mm:ss (hours may exceed 23), or be empty.";
            return;
        }

        IsBusy = true;
        try
        {
            CaseOperation saved;
            if (isEditingOperation && SelectedOperation is { } operation)
            {
                saved = await apiClient.UpdateCaseOperationAsync(
                    SelectedCase.CaseId,
                    operation.CaseOperationId,
                    new CaseOperationUpdate(
                        operationNumber,
                        NewOperationName,
                        NullIfBlank(NewOperationRequiredMachineType),
                        setupSeconds,
                        cycleSeconds,
                        NewOperationDependencyType,
                        NewOperationPredecessor?.CaseOperationId,
                        NullIfBlank(NewOperationSimultaneousGroupKey)),
                    $"\"case-operation:{operation.CaseOperationId}:v{operation.Version}\"",
                    clientId,
                    editGeneration);
                var index = Operations.IndexOf(operation);
                if (index >= 0)
                {
                    Operations[index] = saved;
                }
                SelectedOperation = saved;
                StatusMessage = $"Case Operation {saved.OperationNumber} ({saved.Name}) updated. Existing Production Batches were not changed.";
            }
            else
            {
                saved = await apiClient.CreateCaseOperationAsync(
                    SelectedCase.CaseId,
                    new CaseOperationCreate(
                        operationNumber,
                        NewOperationName,
                        NullIfBlank(NewOperationRequiredMachineType),
                        setupSeconds,
                        cycleSeconds,
                        NewOperationDependencyType,
                        NewOperationPredecessor?.CaseOperationId,
                        NullIfBlank(NewOperationSimultaneousGroupKey)),
                    clientId,
                    editGeneration);
                Operations.Add(saved);
                SelectedOperation = saved;
                StatusMessage = $"Case Operation {saved.OperationNumber} ({saved.Name}) created at route position {saved.RoutePosition + 1}. Existing Production Batches were not changed.";
            }

            isCreatingOperation = false;
            isEditingOperation = false;
            RecalculateCurrentTimeTotals();
            PlanChanged?.Invoke(this, EventArgs.Empty);
            ResetOperationForm();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
            RaiseStateProperties();
        }
    }

    internal Task BeginCreateOrderAsync()
    {
        if (!CanBeginChildCreate)
        {
            return Task.CompletedTask;
        }

        isCreatingOperation = false;
        isEditingOperation = false;
        isCreatingBatch = false;
        ResetOperationForm();
        ResetBatchForm();
        isCreatingOrder = true;
        ResetOrderForm();
        StatusMessage = "Enter demand for the selected Case. Orders are never assigned directly to Machines.";
        RaiseStateProperties();
        return Task.CompletedTask;
    }

    internal Task CancelCreateOrderAsync()
    {
        isCreatingOperation = false;
        isEditingOperation = false;
        isCreatingOrder = false;
        ResetOperationForm();
        ResetOrderForm();
        StatusMessage = "New Order entry cancelled.";
        RaiseStateProperties();
        return Task.CompletedTask;
    }

    internal async Task CreateOrderAsync()
    {
        if (apiClient is null || SelectedCase is null || !CanCreateOrder)
        {
            return;
        }

        if (!int.TryParse(NewOrderQuantity, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity)
            || quantity <= 0)
        {
            StatusMessage = "Order quantity must be a whole number greater than zero.";
            return;
        }

        if (!DateOnly.TryParseExact(
                NewOrderWorkFinishDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            StatusMessage = "Work Finish Date must use YYYY-MM-DD.";
            return;
        }

        IsBusy = true;
        try
        {
            var created = await apiClient.CreateOrderAsync(
                new OrderCreate(
                    SelectedCase.CaseId,
                    NewOrderNumber,
                    quantity,
                    NewOrderWorkFinishDate,
                    NewOrderStatus,
                    NullIfBlank(NewOrderNotes)),
                clientId,
                editGeneration);
            Orders.Add(created);
            isCreatingOrder = false;
            ResetOrderForm();
            await RefreshSelectedCaseSummaryAsync();
            StatusMessage = $"Order {created.OrderNumber} created by the Server.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
            RaiseStateProperties();
        }
    }

    internal Task BeginCreateBatchAsync()
    {
        if (!CanBeginChildCreate)
        {
            return Task.CompletedTask;
        }

        isCreatingOperation = false;
        isEditingOperation = false;
        isCreatingOrder = false;
        ResetOperationForm();
        ResetOrderForm();
        isCreatingBatch = true;
        ResetBatchForm();
        foreach (var order in Orders)
        {
            BatchOrderAllocations.Add(new BatchOrderAllocationViewModel(order));
        }
        StatusMessage = "Allocate the Batch explicitly to selected Orders, stock, and optional scrap allowance.";
        RaiseStateProperties();
        return Task.CompletedTask;
    }

    internal Task CancelCreateBatchAsync()
    {
        isCreatingBatch = false;
        ResetBatchForm();
        StatusMessage = "New Production Batch entry cancelled.";
        RaiseStateProperties();
        return Task.CompletedTask;
    }

    internal async Task CreateBatchAsync()
    {
        if (apiClient is null || SelectedCase is null || !CanCreateBatch)
        {
            return;
        }

        if (!TryParsePositiveQuantity(NewBatchPlannedQuantity, "Planned quantity", out var plannedQuantity))
        {
            return;
        }

        var allocations = new List<BatchAllocationCreate>();
        foreach (var row in BatchOrderAllocations)
        {
            if (!TryParseOptionalAllocation(row.AllocatedQuantity, $"Allocation for {row.OrderNumber}", out var quantity))
            {
                return;
            }

            if (quantity > 0)
            {
                allocations.Add(new BatchAllocationCreate("order", row.OrderId, quantity));
            }
        }

        if (!TryParseOptionalAllocation(NewBatchStockQuantity, "Stock quantity", out var stockQuantity)
            || !TryParseOptionalAllocation(NewBatchScrapAllowance, "Scrap allowance", out var scrapAllowance))
        {
            return;
        }

        if (stockQuantity > 0)
        {
            allocations.Add(new BatchAllocationCreate("stock", null, stockQuantity));
        }

        if (scrapAllowance > 0)
        {
            allocations.Add(new BatchAllocationCreate("scrapAllowance", null, scrapAllowance));
        }

        IsBusy = true;
        try
        {
            var created = await apiClient.CreateBatchAsync(
                new ProductionBatchCreate(
                    SelectedCase.CaseId,
                    NewBatchNumber,
                    "waiting",
                    plannedQuantity,
                    allocations),
                clientId,
                editGeneration);
            Batches.Add(created);
            isCreatingBatch = false;
            ResetBatchForm();
            await RefreshSelectedCaseSummaryAsync();
            StatusMessage = $"Production Batch {created.BatchNumber} created with {created.BatchOperationCount} route operation{(created.BatchOperationCount == 1 ? string.Empty : "s")}.";
            PlanChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
            RaiseStateProperties();
        }
    }

    internal void SetPreviewSelection(string path)
    {
        PreviewPath = path;
        try
        {
            DetailPreview = ToBitmap(File.ReadAllBytes(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DetailPreview = null;
            StatusMessage = $"The selected picture cannot be previewed locally: {exception.Message}";
        }
    }

    internal void SetWorkingFolderSelection(string path) => WorkingFolderPath = path;

    internal Task SelectCaseAsync(CasePoolItemViewModel item)
    {
        SelectedCase = item;
        return LoadSelectedCaseAsync();
    }

    private async Task ClearFiltersAsync()
    {
        SearchText = string.Empty;
        CustomerFilter = string.Empty;
        ActiveFilter = "All";
        await LoadCasesAsync();
    }

    private async Task LoadSelectedCaseSafeAsync()
    {
        try
        {
            await LoadSelectedCaseAsync();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
    }

    private async Task LoadSelectedCaseAsync()
    {
        if (apiClient is null || SelectedCase is null || IsBusy)
        {
            return;
        }

        var caseId = SelectedCase.CaseId;
        IsBusy = true;
        try
        {
            var caseTask = apiClient.GetCaseAsync(caseId);
            var operationsTask = apiClient.ListCaseOperationsAsync(caseId);
            var ordersTask = apiClient.ListOrdersAsync(caseId);
            var batchesTask = apiClient.ListBatchesAsync(caseId);
            var machinesTask = apiClient.ListMachinesAsync();
            var previewTask = apiClient.GetCasePreviewAsync(caseId);
            await Task.WhenAll(caseTask, operationsTask, ordersTask, batchesTask, machinesTask, previewTask);

            if (SelectedCase?.CaseId != caseId)
            {
                return;
            }

            var resource = await caseTask;
            entityTag = resource.EntityTag;
            ApplyCase(resource.Value);
            Replace(Operations, await operationsTask);
            RebuildOperationReferenceOptions(isEditingOperation ? SelectedOperation?.CaseOperationId : null);
            ApplyMachineTypeOptions(await machinesTask);
            Replace(Orders, await ordersTask);
            Replace(Batches, await batchesTask);
            DetailPreview = ToBitmap(await previewTask);
            selectedDetailsAreStale = false;
            StatusMessage = $"Case {resource.Value.PartNumber} loaded from the Server.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task SaveAsync()
    {
        if (apiClient is null || !isEditor || (!IsCreating && (SelectedCase is null || entityTag is null)))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var values = new CaseUpdate(
                PartNumber,
                Name,
                NullIfBlank(Revision),
                NullIfBlank(Customer),
                NullIfBlank(CustomerReference),
                NullIfBlank(PreviewPath),
                WorkingFolderPath,
                NullIfBlank(MaterialType),
                NullIfBlank(MaterialSpecification),
                NullIfBlank(RawMaterialForm),
                NullIfBlank(RawMaterialDimensions),
                NullIfBlank(Notes));
            var creating = IsCreating;
            var resource = creating
                ? await apiClient.CreateCaseAsync(values, clientId, editGeneration)
                : await apiClient.UpdateCaseAsync(
                    SelectedCase!.CaseId,
                    values,
                    entityTag!,
                    clientId,
                    editGeneration);
            entityTag = resource.EntityTag;
            ApplyCase(resource.Value);
            if (creating)
            {
                isCreating = false;
                var item = new CasePoolItemViewModel(resource.Value);
                Cases.Add(item);
                selectedCase = item;
                OnPropertyChanged(nameof(SelectedCase));
            }
            else
            {
                SelectedCase!.Update(resource.Value);
            }
            DetailPreview = ToBitmap(await apiClient.GetCasePreviewAsync(resource.Value.CaseId));
            StatusMessage = creating
                ? $"Case {resource.Value.PartNumber} created by the Server."
                : $"Case {resource.Value.PartNumber} saved by the Server.";
            PlanChanged?.Invoke(this, EventArgs.Empty);
            RaiseStateProperties();
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

    internal Task OpenWorkingFolderAsync()
    {
        try
        {
            folderLauncher.Open(WorkingFolderPath);
            StatusMessage = "Working Folder opened.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusMessage = $"Working Folder could not be opened: {exception.Message}";
        }

        return Task.CompletedTask;
    }

    internal async Task DeleteSelectedCaseAsync()
    {
        if (!CanDelete || SelectedCase is null || apiClient is null) return;
        var deleted = SelectedCase;
        await DeleteAsync(
            () => apiClient.DeleteCaseAsync(deleted.CaseId, clientId, editGeneration),
            $"Case {deleted.PartNumber} deleted.",
            () =>
            {
                Cases.Remove(deleted);
                selectedCase = null;
                OnPropertyChanged(nameof(SelectedCase));
                ClearDetails();
                SelectedCase = Cases.FirstOrDefault();
            });
    }

    internal Task DeleteSelectedOperationAsync()
    {
        var selectedCaseValue = SelectedCase;
        var operation = SelectedOperation;
        return selectedCaseValue is null || operation is null || apiClient is null
            ? Task.CompletedTask
            : DeleteAsync(
                () => apiClient.DeleteCaseOperationAsync(selectedCaseValue.CaseId, operation.CaseOperationId, clientId, editGeneration),
                $"Operation {operation.OperationNumber} deleted.",
                () =>
                {
                    Operations.Remove(operation);
                    SelectedOperation = null;
                    RebuildOperationReferenceOptions(null);
                    RecalculateCurrentTimeTotals();
                    PlanChanged?.Invoke(this, EventArgs.Empty);
                });
    }

    internal Task DeleteSelectedOrderAsync()
    {
        var order = SelectedOrder;
        return order is null || apiClient is null
            ? Task.CompletedTask
            : DeleteAsync(
                () => apiClient.DeleteOrderAsync(order.OrderId, clientId, editGeneration),
                $"Order {order.OrderNumber} deleted.",
                () => { Orders.Remove(order); SelectedOrder = null; });
    }

    internal Task DeleteSelectedBatchAsync()
    {
        var batch = SelectedBatch;
        return batch is null || apiClient is null
            ? Task.CompletedTask
            : DeleteAsync(
                () => apiClient.DeleteBatchAsync(batch.BatchId, clientId, editGeneration),
                $"Production Batch {batch.BatchNumber} deleted.",
                () =>
                {
                    Batches.Remove(batch);
                    SelectedBatch = null;
                    PlanChanged?.Invoke(this, EventArgs.Empty);
                });
    }

    private async Task DeleteAsync(Func<Task> delete, string successMessage, Action apply)
    {
        if (!CanDelete) return;
        IsBusy = true;
        try
        {
            await delete();
            apply();
            StatusMessage = successMessage;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
            RaiseStateProperties();
        }
    }

    private void ApplyCase(PlannerCase plannerCase)
    {
        PartNumber = plannerCase.PartNumber;
        Name = plannerCase.Name;
        Revision = plannerCase.Revision ?? string.Empty;
        Customer = plannerCase.Customer ?? string.Empty;
        CustomerReference = plannerCase.CustomerReference ?? string.Empty;
        PreviewPath = plannerCase.PreviewPath ?? string.Empty;
        WorkingFolderPath = plannerCase.WorkingFolderPath;
        MaterialType = plannerCase.MaterialType ?? string.Empty;
        MaterialSpecification = plannerCase.MaterialSpecification ?? string.Empty;
        RawMaterialForm = plannerCase.RawMaterialForm ?? string.Empty;
        RawMaterialDimensions = plannerCase.RawMaterialDimensions ?? string.Empty;
        CurrentSetupTime = DurationText.Format(plannerCase.CurrentSetupTimeSeconds ?? 0);
        CurrentCycleTimePerPart = DurationText.Format(plannerCase.CurrentCycleTimePerPartSeconds ?? 0);
        Notes = plannerCase.Notes ?? string.Empty;
        ActiveStateText = plannerCase.IsActive ? "Active" : "Inactive";
    }

    private async Task RefreshSelectedCaseSummaryAsync()
    {
        if (apiClient is null || SelectedCase is null)
        {
            return;
        }

        var resource = await apiClient.GetCaseAsync(SelectedCase.CaseId);
        entityTag = resource.EntityTag;
        ApplyCase(resource.Value);
        SelectedCase.Update(resource.Value);
    }

    private void ResetOrderForm()
    {
        NewOrderNumber = string.Empty;
        NewOrderQuantity = string.Empty;
        NewOrderWorkFinishDate = string.Empty;
        NewOrderStatus = "active";
        NewOrderNotes = string.Empty;
    }

    private void ResetOperationForm()
    {
        NewOperationNumber = string.Empty;
        NewOperationName = string.Empty;
        NewOperationRequiredMachineType = string.Empty;
        NewOperationSetupTime = string.Empty;
        NewOperationCycleTimePerPart = string.Empty;
        NewOperationDependencyType = "INDEPENDENT";
        NewOperationPredecessor = null;
        NewOperationSimultaneousGroupKey = string.Empty;
    }

    private void ApplyMachineTypeOptions(IReadOnlyList<PlannerMachine> machines)
    {
        var selected = NewOperationRequiredMachineType;
        var values = machines
            .SelectMany(machine => new[] { machine.ProcessType, machine.AxisType }
                .Concat(machine.Capabilities))
            .Concat(Operations.Select(operation => operation.RequiredMachineType))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        OperationMachineTypeOptions.Clear();
        OperationMachineTypeOptions.Add(string.Empty);
        foreach (var value in values)
        {
            OperationMachineTypeOptions.Add(value);
        }

        EnsureMachineTypeOption(selected);
        NewOperationRequiredMachineType = selected;
    }

    private void EnsureMachineTypeOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || OperationMachineTypeOptions.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        OperationMachineTypeOptions.Add(value.Trim());
    }

    private void RebuildOperationReferenceOptions(string? excludedOperationId)
    {
        OperationReferenceOptions.Clear();
        foreach (var operation in Operations.Where(operation =>
                     operation.CaseOperationId != excludedOperationId))
        {
            OperationReferenceOptions.Add(operation);
        }
    }

    private void RecalculateCurrentTimeTotals()
    {
        var setupSeconds = Operations.Sum(operation => (long)(operation.SetupTimeSeconds ?? 0));
        var cycleSeconds = Operations.Sum(operation => (long)(operation.CycleTimePerPartSeconds ?? 0));
        CurrentSetupTime = DurationText.Format(setupSeconds);
        CurrentCycleTimePerPart = DurationText.Format(cycleSeconds);
    }

    private void ResetBatchForm()
    {
        NewBatchNumber = string.Empty;
        NewBatchPlannedQuantity = string.Empty;
        NewBatchStockQuantity = string.Empty;
        NewBatchScrapAllowance = string.Empty;
        BatchOrderAllocations.Clear();
    }

    private bool TryParsePositiveQuantity(string text, string label, out int quantity)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out quantity)
            && quantity > 0)
        {
            return true;
        }

        StatusMessage = $"{label} must be a whole number greater than zero.";
        return false;
    }

    private bool TryParseOptionalAllocation(string text, string label, out int quantity)
    {
        quantity = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out quantity)
            && quantity >= 0)
        {
            return true;
        }

        StatusMessage = $"{label} must be a whole non-negative quantity.";
        return false;
    }

    private void ClearDetails()
    {
        selectedCase = null;
        isCreating = false;
        isCreatingOperation = false;
        isEditingOperation = false;
        isCreatingOrder = false;
        isCreatingBatch = false;
        selectedDetailsAreStale = false;
        entityTag = null;
        ResetOperationForm();
        ResetOrderForm();
        ResetBatchForm();
        ClearFormValues();
        RaiseStateProperties();
    }

    private void ClearFormValues()
    {
        DetailPreview = null;
        Operations.Clear();
        OperationReferenceOptions.Clear();
        Orders.Clear();
        Batches.Clear();
        RecalculateCurrentTimeTotals();
        ApplyCase(new PlannerCase(
            string.Empty, string.Empty, string.Empty, null, null, null, null, string.Empty,
            null, null, null, null, null, null, null, false, 0, default, default));
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static BitmapImage? ToBitmap(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

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
        catch (Exception exception) when (exception is
            IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsExpected(Exception exception) => exception is
        PlannerApiException or PlannerProtocolException or HttpRequestException or TaskCanceledException;

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        TaskCanceledException => "The Server did not respond before the client timeout.",
        HttpRequestException => "The configured Server could not be reached.",
        PlannerApiException api => $"{api.Message} ({api.Code})",
        _ => exception.Message
    };

    private void RaiseStateProperties()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasForm));
        OnPropertyChanged(nameof(IsCreating));
        OnPropertyChanged(nameof(IsFormReadOnly));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanBeginCreate));
        OnPropertyChanged(nameof(CanEditForm));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(FormHeading));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(CanOpenWorkingFolder));
        OnPropertyChanged(nameof(IsCreatingOrder));
        OnPropertyChanged(nameof(IsCreatingBatch));
        OnPropertyChanged(nameof(IsCreatingOperation));
        OnPropertyChanged(nameof(IsEditingOperation));
        OnPropertyChanged(nameof(OperationFormHeading));
        OnPropertyChanged(nameof(OperationSaveButtonText));
        OnPropertyChanged(nameof(CanBeginChildCreate));
        OnPropertyChanged(nameof(CanCreateOrder));
        OnPropertyChanged(nameof(CanCreateBatch));
        OnPropertyChanged(nameof(CanCreateOperation));
        OnPropertyChanged(nameof(CanBeginEditOperation));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        SearchCommand.RaiseCanExecuteChanged();
        ClearFiltersCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        BeginCreateCommand.RaiseCanExecuteChanged();
        CancelCreateCommand.RaiseCanExecuteChanged();
        RefreshDetailsCommand.RaiseCanExecuteChanged();
        OpenWorkingFolderCommand.RaiseCanExecuteChanged();
        BeginCreateOperationCommand.RaiseCanExecuteChanged();
        BeginEditOperationCommand.RaiseCanExecuteChanged();
        CancelCreateOperationCommand.RaiseCanExecuteChanged();
        CreateOperationCommand.RaiseCanExecuteChanged();
        BeginCreateOrderCommand.RaiseCanExecuteChanged();
        CancelCreateOrderCommand.RaiseCanExecuteChanged();
        CreateOrderCommand.RaiseCanExecuteChanged();
        BeginCreateBatchCommand.RaiseCanExecuteChanged();
        CancelCreateBatchCommand.RaiseCanExecuteChanged();
        CreateBatchCommand.RaiseCanExecuteChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class BatchOrderAllocationViewModel : INotifyPropertyChanged
{
    private string allocatedQuantity = string.Empty;

    internal BatchOrderAllocationViewModel(PlannerOrder order)
    {
        OrderId = order.OrderId;
        OrderNumber = order.OrderNumber;
        DemandQuantity = order.Quantity;
        Status = order.Status;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string OrderId { get; }

    public string OrderNumber { get; }

    public int DemandQuantity { get; }

    public string Status { get; }

    public string AllocatedQuantity
    {
        get => allocatedQuantity;
        set
        {
            if (allocatedQuantity == value)
            {
                return;
            }

            allocatedQuantity = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AllocatedQuantity)));
        }
    }
}

internal sealed class CasePoolItemViewModel : INotifyPropertyChanged
{
    private BitmapImage? thumbnail;
    private string partNumber;
    private string customer;
    private string activeStateText;

    internal CasePoolItemViewModel(PlannerCase plannerCase)
    {
        CaseId = plannerCase.CaseId;
        partNumber = plannerCase.PartNumber;
        customer = plannerCase.Customer ?? "No customer";
        activeStateText = plannerCase.IsActive ? "Active" : "Inactive";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CaseId { get; }
    public string PartNumber => partNumber;
    public string Customer => customer;
    public string ActiveStateText => activeStateText;
    public string PreviewStatus => Thumbnail is null ? "No preview" : "Preview available";

    public BitmapImage? Thumbnail
    {
        get => thumbnail;
        set
        {
            if (ReferenceEquals(thumbnail, value))
            {
                return;
            }

            thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewStatus)));
        }
    }

    internal void Update(PlannerCase plannerCase)
    {
        partNumber = plannerCase.PartNumber;
        customer = plannerCase.Customer ?? "No customer";
        activeStateText = plannerCase.IsActive ? "Active" : "Inactive";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PartNumber)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Customer)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveStateText)));
    }
}
