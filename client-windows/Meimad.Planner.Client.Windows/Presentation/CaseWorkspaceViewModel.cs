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
    private bool isEditingOrder;
    private bool isCreatingBatch;
    private bool isEditingBatch;
    private string? originalOrderStatus;
    private string? editingOrderId;
    private string? editingOrderEntityTag;
    private bool selectedDetailsAreStale;
    private CasePoolItemViewModel? selectedCase;
    private CaseOperation? selectedOperation;
    private PlannerOrder? selectedOrder;
    private ProductionBatch? selectedBatch;
    private CaseComponent? selectedComponent;
    private CasePoolItemViewModel? selectedComponentCase;
    private string? entityTag;
    private string searchText = string.Empty;
    private string customerFilter = string.Empty;
    private string activeFilter = "All";
    private string caseSort = "Part Number";
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
    private string newOperationQaTime = string.Empty;
    private string newOperationLoadUnloadTime = string.Empty;
    private string newOperationLoadUnloadEveryNParts = string.Empty;
    private bool newOperationLoadUnloadRequiresWorker;
    private bool newOperationAutomaticLoading;
    private bool newOperationDayShiftOnly;
    private bool newOperationHasExternalDelay;
    private string? newOperationExternalDelayDescription;
    private string newOperationExternalDelayDuration = "0";
    private string newOperationExternalDelayDurationUnit = "hours";
    private string? newOperationExternalDelayCalendarId;
    private bool newOperationRespectMasterCalendar = true;
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
    private string componentQuantityPerParent = "1";
    private string componentNotes = string.Empty;
    private string componentDemandQuantity = "1";
    private bool isParentCase;
    private bool isChildCase;

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
        BeginCreateOperationCommand = new AsyncCommand(BeginCreateOperationAsync, () => CanManageOperations);
        BeginEditOperationCommand = new AsyncCommand(BeginEditOperationAsync, () => CanBeginEditOperation);
        CancelCreateOperationCommand = new AsyncCommand(CancelCreateOperationAsync, () => IsCreatingOperation && !IsBusy);
        CreateOperationCommand = new AsyncCommand(CreateOperationAsync, () => CanCreateOperation);
        BeginCreateOrderCommand = new AsyncCommand(BeginCreateOrderAsync, () => CanManageDirectOrders);
        BeginEditOrderCommand = new AsyncCommand(BeginEditOrderAsync, () => CanBeginEditOrder);
        CancelCreateOrderCommand = new AsyncCommand(CancelCreateOrderAsync, () => IsOrderFormOpen && !IsBusy);
        CreateOrderCommand = new AsyncCommand(CreateOrderAsync, () => CanCreateOrder);
        BeginCreateBatchCommand = new AsyncCommand(BeginCreateBatchAsync, () => CanManageBatches && Operations.Count > 0);
        BeginEditBatchCommand = new AsyncCommand(BeginEditBatchAsync, () => CanBeginEditBatch);
        CancelCreateBatchCommand = new AsyncCommand(CancelCreateBatchAsync, () => IsCreatingBatch && !IsBusy);
        CreateBatchCommand = new AsyncCommand(CreateBatchAsync, () => CanCreateBatch);
        SaveComponentCommand = new AsyncCommand(SaveComponentAsync,
            () => CanBeginChildCreate && (SelectedComponent is not null || SelectedComponentCase is not null));
        RemoveComponentCommand = new AsyncCommand(RemoveComponentAsync,
            () => CanBeginChildCreate && SelectedComponent is not null);
        PreviewComponentDemandCommand = new AsyncCommand(PreviewComponentDemandAsync,
            () => SelectedCase is not null && apiClient is not null && !IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? PlanChanged;

    public ObservableCollection<CasePoolItemViewModel> Cases { get; } = [];

    public ObservableCollection<CaseOperation> Operations { get; } = [];

    public ObservableCollection<CaseOperation> OperationReferenceOptions { get; } = [];

    public ObservableCollection<string> OperationMachineTypeOptions { get; } = [string.Empty];

    public ObservableCollection<PlannerOrder> Orders { get; } = [];

    public ObservableCollection<DerivedCaseOrder> DerivedOrders { get; } = [];

    public ObservableCollection<ProductionBatch> Batches { get; } = [];

    public ObservableCollection<CaseComponent> Components { get; } = [];

    public ObservableCollection<CaseComponent> WhereUsed { get; } = [];

    public ObservableCollection<ComponentDemandRow> ComponentDemand { get; } = [];

    public ObservableCollection<CasePoolItemViewModel> ComponentCaseOptions { get; } = [];

    public ObservableCollection<WorkingCalendar> WorkingCalendars { get; } = [];

    public ObservableCollection<BatchOrderAllocationViewModel> BatchOrderAllocations { get; } = [];

    public IReadOnlyList<string> ActiveFilters { get; } = ["All", "Active", "Inactive"];

    public IReadOnlyList<string> CaseSortOptions { get; } = ["Part Number", "Closest Order delivery date", "Customer name"];

    public IReadOnlyList<string> OrderStatuses => isEditingOrder
        ? ["active", "in_production", "complete", "cancelled"]
        : ["active", "cancelled"];

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

    public AsyncCommand BeginEditOrderCommand { get; }

    public AsyncCommand CancelCreateOrderCommand { get; }

    public AsyncCommand CreateOrderCommand { get; }

    public AsyncCommand BeginCreateBatchCommand { get; }

    public AsyncCommand BeginEditBatchCommand { get; }

    public AsyncCommand CancelCreateBatchCommand { get; }

    public AsyncCommand CreateBatchCommand { get; }

    public AsyncCommand SaveComponentCommand { get; }

    public AsyncCommand RemoveComponentCommand { get; }

    public AsyncCommand PreviewComponentDemandCommand { get; }

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
                isEditingOrder = false;
                isCreatingBatch = false;
                isEditingBatch = false;
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
    public PlannerOrder? SelectedOrder
    {
        get => selectedOrder;
        set
        {
            if (SetField(ref selectedOrder, value))
            {
                OnPropertyChanged(nameof(CanBeginEditOrder));
                BeginEditOrderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CaseSort
    {
        get => caseSort;
        set => SetField(ref caseSort, value);
    }
    public ProductionBatch? SelectedBatch
    {
        get => selectedBatch;
        set
        {
            if (SetField(ref selectedBatch, value))
            {
                OnPropertyChanged(nameof(CanBeginEditBatch));
                BeginEditBatchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public CaseComponent? SelectedComponent
    {
        get => selectedComponent;
        set
        {
            if (SetField(ref selectedComponent, value))
            {
                if (value is not null)
                {
                    SelectedComponentCase = ComponentCaseOptions.FirstOrDefault(item => item.CaseId == value.ChildCaseId);
                    ComponentQuantityPerParent = value.QuantityPerParent.ToString(CultureInfo.InvariantCulture);
                    ComponentNotes = value.Notes ?? string.Empty;
                }
                RaiseCommandStates();
            }
        }
    }

    public CasePoolItemViewModel? SelectedComponentCase
    {
        get => selectedComponentCase;
        set { if (SetField(ref selectedComponentCase, value)) RaiseCommandStates(); }
    }

    public string ComponentQuantityPerParent
    {
        get => componentQuantityPerParent;
        set => SetField(ref componentQuantityPerParent, value);
    }

    public string ComponentNotes
    {
        get => componentNotes;
        set => SetField(ref componentNotes, value);
    }

    public string ComponentDemandQuantity
    {
        get => componentDemandQuantity;
        set => SetField(ref componentDemandQuantity, value);
    }

    public string FormHeading => IsCreating ? "NEW CASE" : "CASE DETAILS";

    public string SaveButtonText => IsCreating ? "Create Case" : "Save Case";

    public bool CanOpenWorkingFolder =>
        SelectedCase is not null && !string.IsNullOrWhiteSpace(WorkingFolderPath) && !IsBusy;

    public bool IsCreatingOrder => isCreatingOrder || isEditingOrder;

    public bool IsEditingOrder => isEditingOrder;

    public bool IsOrderFormOpen => IsCreatingOrder;

    public bool IsOrderListEnabled => !IsOrderFormOpen;

    public string OrderFormHeading => isEditingOrder ? "EDIT ORDER" : "NEW ORDER";

    public string OrderSaveButtonText => isEditingOrder ? "Save Order" : "Create Order";

    public string OrderAuthorityText => isEditingOrder
        ? "The Server enforces allocation-safe quantity and production-derived status rules."
        : "New Orders may be active or cancelled; production status is derived by the Server.";

    public bool IsCreatingBatch => isCreatingBatch || isEditingBatch;

    public bool IsEditingBatch => isEditingBatch;

    public string BatchFormHeading => isEditingBatch ? "EDIT PRODUCTION BATCH" : "NEW PRODUCTION BATCH";

    public string BatchSaveButtonText => isEditingBatch ? "Save Batch" : "Create Batch";

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

    public bool IsParentCase => isParentCase;

    public bool IsChildCase => isChildCase;

    public bool CanManageOperations => CanBeginChildCreate && !IsParentCase;

    public bool CanManageDirectOrders => CanBeginChildCreate && (!IsChildCase || IsParentCase);

    public bool CanManageBatches => CanBeginChildCreate && !IsParentCase;

    public bool CanCreateOrder => IsCreatingOrder && CanManageDirectOrders;

    public bool CanBeginEditOrder =>
        CanManageDirectOrders && SelectedOrder is not null && !IsOrderFormOpen;

    public bool CanCreateBatch => IsCreatingBatch && CanManageBatches;

    public bool CanBeginEditBatch => CanManageBatches && SelectedBatch is not null && !IsCreatingBatch;

    public bool CanCreateOperation => IsCreatingOperation && CanManageOperations;

    public bool CanBeginEditOperation =>
        CanManageOperations && SelectedOperation is not null && !IsCreatingOperation;

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
    public string NewOperationQaTime { get => newOperationQaTime; set => SetField(ref newOperationQaTime, value); }
    public string NewOperationLoadUnloadTime { get => newOperationLoadUnloadTime; set => SetField(ref newOperationLoadUnloadTime, value); }
    public string NewOperationLoadUnloadEveryNParts { get => newOperationLoadUnloadEveryNParts; set => SetField(ref newOperationLoadUnloadEveryNParts, value); }
    public bool NewOperationLoadUnloadRequiresWorker { get => newOperationLoadUnloadRequiresWorker; set => SetField(ref newOperationLoadUnloadRequiresWorker, value); }
    public bool NewOperationAutomaticLoading { get => newOperationAutomaticLoading; set => SetField(ref newOperationAutomaticLoading, value); }
    public bool NewOperationDayShiftOnly { get => newOperationDayShiftOnly; set => SetField(ref newOperationDayShiftOnly, value); }
    public bool NewOperationHasExternalDelay { get => newOperationHasExternalDelay; set => SetField(ref newOperationHasExternalDelay, value); }
    public string? NewOperationExternalDelayDescription { get => newOperationExternalDelayDescription; set => SetField(ref newOperationExternalDelayDescription, value); }
    public string NewOperationExternalDelayDuration { get => newOperationExternalDelayDuration; set => SetField(ref newOperationExternalDelayDuration, value); }
    public string NewOperationExternalDelayDurationUnit { get => newOperationExternalDelayDurationUnit; set => SetField(ref newOperationExternalDelayDurationUnit, value); }
    public string? NewOperationExternalDelayCalendarId { get => newOperationExternalDelayCalendarId; set => SetField(ref newOperationExternalDelayCalendarId, value); }
    public bool NewOperationRespectMasterCalendar { get => newOperationRespectMasterCalendar; set => SetField(ref newOperationRespectMasterCalendar, value); }
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
            var sort = CaseSort switch
            {
                "Closest Order delivery date" => "closestOrderDeliveryDate",
                "Customer name" => "customerName",
                _ => "partNumber"
            };
            var cases = await apiClient.ListCasesAsync(new CaseQuery(SearchText, CustomerFilter, active, sort));
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
        isEditingOrder = false;
        isCreatingBatch = false;
        isEditingBatch = false;
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
        isEditingOrder = false;
        isCreatingBatch = false;
        isEditingBatch = false;
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
        NewOperationQaTime = DurationText.FormatOptional(operation.QaTimeAfterSetupSeconds);
        NewOperationLoadUnloadTime = DurationText.FormatOptional(operation.LoadUnloadTimeSeconds);
        NewOperationLoadUnloadRequiresWorker = operation.LoadUnloadRequiresWorker;
        NewOperationAutomaticLoading = operation.AutomaticLoading;
        NewOperationLoadUnloadEveryNParts = operation.LoadUnloadEveryNParts?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        NewOperationDayShiftOnly = operation.DayShiftOnly;
        NewOperationHasExternalDelay = operation.HasExternalDelay;
        NewOperationExternalDelayDescription = operation.ExternalDelayDescription;
        NewOperationExternalDelayDuration = operation.ExternalDelayDuration.ToString(CultureInfo.InvariantCulture);
        NewOperationExternalDelayDurationUnit = operation.ExternalDelayDurationUnit;
        NewOperationExternalDelayCalendarId = operation.ExternalDelayCalendarId;
        NewOperationRespectMasterCalendar = operation.RespectMasterCalendar;
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
            || !DurationText.TryParseOptional(NewOperationCycleTimePerPart, out var cycleSeconds)
            || !DurationText.TryParseOptional(NewOperationQaTime, out var qaSeconds)
            || !DurationText.TryParseOptional(NewOperationLoadUnloadTime, out var loadUnloadSeconds))
        {
            StatusMessage = "Setup, QA, cycle, and load/unload time must use HH:mm:ss (hours may exceed 23), or be empty.";
            return;
        }
        int? everyNParts = null;
        if (!string.IsNullOrWhiteSpace(NewOperationLoadUnloadEveryNParts))
        {
            if (!int.TryParse(NewOperationLoadUnloadEveryNParts, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedEveryN) || parsedEveryN <= 0)
            {
                StatusMessage = "Automatic load/unload frequency must be a positive number of parts.";
                return;
            }
            everyNParts = parsedEveryN;
        }
        if (!double.TryParse(NewOperationExternalDelayDuration, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var externalDelayDuration)
            || externalDelayDuration < 0)
        {
            StatusMessage = "External delay duration must be zero or a positive number.";
            return;
        }
        if (NewOperationHasExternalDelay
            && string.Equals(NewOperationExternalDelayDurationUnit, "working_days", StringComparison.Ordinal)
            && (externalDelayDuration != Math.Truncate(externalDelayDuration)
                || string.IsNullOrWhiteSpace(NewOperationExternalDelayCalendarId)))
        {
            StatusMessage = "Working-day external delay requires a whole number of days and a selected Calendar ID.";
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
                        NullIfBlank(NewOperationSimultaneousGroupKey),
                        qaSeconds ?? 0, loadUnloadSeconds ?? 0,
                        NewOperationLoadUnloadRequiresWorker, NewOperationAutomaticLoading,
                        everyNParts, NewOperationDayShiftOnly,
                        NewOperationHasExternalDelay,
                        NewOperationExternalDelayDescription?.Trim() is { Length: > 0 } updateExternalDescription ? updateExternalDescription : null,
                        externalDelayDuration,
                        NewOperationExternalDelayDurationUnit,
                        NewOperationExternalDelayCalendarId?.Trim() is { Length: > 0 } updateExternalCalendar ? updateExternalCalendar : null,
                        NewOperationRespectMasterCalendar),
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
                        NullIfBlank(NewOperationSimultaneousGroupKey),
                        qaSeconds ?? 0, loadUnloadSeconds ?? 0,
                        NewOperationLoadUnloadRequiresWorker, NewOperationAutomaticLoading,
                        everyNParts, NewOperationDayShiftOnly,
                        NewOperationHasExternalDelay,
                        NewOperationExternalDelayDescription?.Trim() is { Length: > 0 } externalDescription ? externalDescription : null,
                        externalDelayDuration,
                        NewOperationExternalDelayDurationUnit,
                        NewOperationExternalDelayCalendarId?.Trim() is { Length: > 0 } externalCalendar ? externalCalendar : null,
                        NewOperationRespectMasterCalendar),
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
        isEditingBatch = false;
        ResetOperationForm();
        ResetBatchForm();
        isCreatingOrder = true;
        isEditingOrder = false;
        ResetOrderForm();
        StatusMessage = "Enter demand for the selected Case. Orders are never assigned directly to Machines.";
        RaiseStateProperties();
        return Task.CompletedTask;
    }

    internal Task BeginEditOrderAsync()
    {
        var order = SelectedOrder;
        if (!CanBeginEditOrder || order is null)
        {
            return Task.CompletedTask;
        }

        isCreatingOperation = false;
        isEditingOperation = false;
        isCreatingBatch = false;
        isEditingBatch = false;
        ResetOperationForm();
        ResetBatchForm();
        isCreatingOrder = false;
        isEditingOrder = true;
        NewOrderNumber = order.OrderNumber;
        NewOrderQuantity = order.Quantity.ToString(CultureInfo.InvariantCulture);
        NewOrderWorkFinishDate = order.WorkFinishDate;
        NewOrderStatus = order.Status;
        originalOrderStatus = order.Status;
        editingOrderId = order.OrderId;
        editingOrderEntityTag = $"\"order:{order.OrderId}:v{order.Version}\"";
        NewOrderNotes = order.Notes ?? string.Empty;
        StatusMessage = $"Editing Order {order.OrderNumber}. The Server protects existing Batch allocations and production-derived status.";
        RaiseStateProperties();
        return Task.CompletedTask;
    }

    internal Task CancelCreateOrderAsync()
    {
        isCreatingOperation = false;
        isEditingOperation = false;
        isCreatingOrder = false;
        isEditingOrder = false;
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
            PlannerOrder saved;
            if (isEditingOrder)
            {
                if (editingOrderId is null || editingOrderEntityTag is null)
                {
                    StatusMessage = "The Order edit target is no longer available. Cancel and reopen the Order editor.";
                    return;
                }

                var orderId = editingOrderId;
                saved = await apiClient.UpdateOrderAsync(
                    orderId,
                    new OrderUpdate(
                        NewOrderNumber,
                        quantity,
                        NewOrderWorkFinishDate,
                        string.Equals(
                            NewOrderStatus,
                            originalOrderStatus,
                            StringComparison.OrdinalIgnoreCase)
                            ? null
                            : NewOrderStatus,
                        NullIfBlank(NewOrderNotes)),
                    editingOrderEntityTag,
                    clientId,
                    editGeneration);
                var authoritativeOrders = await apiClient.ListOrdersAsync(saved.CaseId);
                Replace(Orders, authoritativeOrders);
                SelectedOrder = Orders.FirstOrDefault(order => order.OrderId == orderId);
            }
            else
            {
                saved = await apiClient.CreateOrderAsync(
                    new OrderCreate(
                        SelectedCase.CaseId,
                        NewOrderNumber,
                        quantity,
                        NewOrderWorkFinishDate,
                        NewOrderStatus,
                        NullIfBlank(NewOrderNotes)),
                    clientId,
                    editGeneration);
                Orders.Add(saved);
                SelectedOrder = saved;
            }

            var edited = isEditingOrder;
            isCreatingOrder = false;
            isEditingOrder = false;
            ResetOrderForm();
            await RefreshSelectedCaseSummaryAsync();
            StatusMessage = edited
                ? $"Order {saved.OrderNumber} updated by the Server."
                : $"Order {saved.OrderNumber} created by the Server.";
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

    internal Task BeginCreateBatchAsync()
    {
        if (CanManageBatches && Operations.Count == 0)
        {
            StatusMessage = "Cannot generate Production Batch because this Case has no defined operations. Create operations first.";
            return Task.CompletedTask;
        }
        if (!CanManageBatches)
        {
            return Task.CompletedTask;
        }

        isCreatingOperation = false;
        isEditingOperation = false;
        isCreatingOrder = false;
        isEditingOrder = false;
        ResetOperationForm();
        ResetOrderForm();
        isEditingBatch = false;
        isCreatingBatch = true;
        ResetBatchForm();
        if (IsChildCase)
            foreach (var order in DerivedOrders.Where(order => order.Status != "cancelled" && order.RemainingQuantity > 0))
                BatchOrderAllocations.Add(new BatchOrderAllocationViewModel(order));
        else
            foreach (var order in Orders)
                BatchOrderAllocations.Add(new BatchOrderAllocationViewModel(order));
        StatusMessage = "Allocate the child Batch to read-only parent-derived demand, stock, and optional scrap allowance.";
        RaiseStateProperties();
        return Task.CompletedTask;
    }

    internal Task BeginEditBatchAsync()
    {
        if (!CanBeginEditBatch || SelectedBatch is null)
        {
            return Task.CompletedTask;
        }

        isCreatingOperation = false;
        isEditingOperation = false;
        isCreatingOrder = false;
        isEditingOrder = false;
        ResetOperationForm();
        ResetOrderForm();
        isCreatingBatch = false;
        isEditingBatch = true;
        ResetBatchForm();
        NewBatchNumber = SelectedBatch.BatchNumber;
        NewBatchPlannedQuantity = SelectedBatch.PlannedQuantity.ToString(CultureInfo.InvariantCulture);
        var allocations = SelectedBatch.Allocations ?? [];
        NewBatchStockQuantity = allocations.FirstOrDefault(value => value.AllocationType == "stock")?.Quantity.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        NewBatchScrapAllowance = allocations.FirstOrDefault(value => value.AllocationType == "scrapAllowance")?.Quantity.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        if (IsChildCase)
        {
            foreach (var order in DerivedOrders)
            {
                var row = new BatchOrderAllocationViewModel(order);
                row.AllocatedQuantity = allocations.FirstOrDefault(value => value.AllocationType == "derivedOrder" && value.DerivedOrderKey == order.DerivedOrderKey)?.Quantity.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                BatchOrderAllocations.Add(row);
            }
        }
        else
            foreach (var order in Orders)
            {
                var row = new BatchOrderAllocationViewModel(order);
                row.AllocatedQuantity = allocations.FirstOrDefault(value => value.AllocationType == "order" && value.OrderId == order.OrderId)?.Quantity.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                BatchOrderAllocations.Add(row);
            }
        StatusMessage = $"Editing Production Batch {SelectedBatch.BatchNumber}. Its instantiated route and execution records are preserved.";
        RaiseStateProperties();
        return Task.CompletedTask;
    }

    internal Task CancelCreateBatchAsync()
    {
        isCreatingBatch = false;
        isEditingBatch = false;
        ResetBatchForm();
        StatusMessage = "Production Batch edit cancelled.";
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
                allocations.Add(row.DerivedOrderKey is null
                    ? new BatchAllocationCreate("order", row.OrderId, quantity)
                    : new BatchAllocationCreate("derivedOrder", null, quantity, row.DerivedOrderKey));
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
            var editing = isEditingBatch;
            var original = SelectedBatch;
            var saved = editing && original is not null
                ? await apiClient.UpdateBatchAsync(
                    original.BatchId,
                    new ProductionBatchUpdate(NewBatchNumber, plannedQuantity, allocations),
                    $"\"batch:{original.BatchId}:v{original.Version}\"",
                    clientId,
                    editGeneration)
                : await apiClient.CreateBatchAsync(
                    new ProductionBatchCreate(
                        SelectedCase.CaseId,
                        NewBatchNumber,
                        "waiting",
                        plannedQuantity,
                        allocations),
                    clientId,
                    editGeneration);
            if (editing && original is not null)
            {
                var index = Batches.IndexOf(original);
                if (index >= 0) Batches[index] = saved;
                SelectedBatch = saved;
            }
            else
            {
                Batches.Add(saved);
            }
            isCreatingBatch = false;
            isEditingBatch = false;
            ResetBatchForm();
            await RefreshSelectedCaseSummaryAsync();
            StatusMessage = editing
                ? $"Production Batch {saved.BatchNumber} saved; its {saved.BatchOperationCount} route operation{(saved.BatchOperationCount == 1 ? string.Empty : "s")} remain unchanged."
                : $"Production Batch {saved.BatchNumber} created with {saved.BatchOperationCount} route operation{(saved.BatchOperationCount == 1 ? string.Empty : "s")}.";
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
        CaseSort = "Part Number";
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

    private async Task SaveComponentAsync()
    {
        if (apiClient is null || SelectedCase is null || !isEditor) return;
        if (!double.TryParse(ComponentQuantityPerParent, NumberStyles.Float, CultureInfo.InvariantCulture, out var quantity)
            || !double.IsFinite(quantity) || quantity <= 0)
        {
            StatusMessage = "Quantity per parent must be a number greater than zero.";
            return;
        }
        IsBusy = true;
        try
        {
            CaseComponent saved;
            if (SelectedComponent is null)
            {
                if (SelectedComponentCase is null) return;
                saved = await apiClient.CreateCaseComponentAsync(
                    SelectedCase.CaseId,
                    new CaseComponentCreate(SelectedComponentCase.CaseId, quantity, Components.Count, NullIfBlank(ComponentNotes)),
                    clientId, editGeneration);
                Components.Add(saved);
            }
            else
            {
                saved = await apiClient.UpdateCaseComponentAsync(
                    SelectedCase.CaseId, SelectedComponent.CaseComponentId,
                    new CaseComponentUpdate(quantity, SelectedComponent.SortOrder, NullIfBlank(ComponentNotes), true),
                    SelectedComponent.EntityTag, clientId, editGeneration);
                var index = Components.IndexOf(SelectedComponent);
                if (index >= 0) Components[index] = saved;
            }
            SelectedComponent = saved;
            Replace(WhereUsed, await apiClient.ListCaseWhereUsedAsync(SelectedCase.CaseId));
            StatusMessage = $"Component {saved.ChildPartNumber} saved.";
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = FriendlyMessage(exception); }
        finally { IsBusy = false; }
    }

    private async Task RemoveComponentAsync()
    {
        if (apiClient is null || SelectedCase is null || SelectedComponent is null || !isEditor) return;
        IsBusy = true;
        try
        {
            var removed = SelectedComponent;
            await apiClient.DeactivateCaseComponentAsync(
                SelectedCase.CaseId, removed.CaseComponentId, removed.EntityTag,
                clientId, editGeneration);
            var refreshed = await apiClient.ListCaseComponentsAsync(SelectedCase.CaseId);
            Replace(Components, refreshed);
            SelectedComponent = null;
            SelectedComponentCase = null;
            ComponentQuantityPerParent = "1";
            ComponentNotes = string.Empty;
            StatusMessage = $"Component {removed.ChildPartNumber} was deactivated; its Case remains available.";
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = FriendlyMessage(exception); }
        finally { IsBusy = false; }
    }

    private async Task PreviewComponentDemandAsync()
    {
        if (apiClient is null || SelectedCase is null) return;
        if (!double.TryParse(ComponentDemandQuantity, NumberStyles.Float, CultureInfo.InvariantCulture, out var quantity)
            || !double.IsFinite(quantity) || quantity <= 0)
        {
            StatusMessage = "Demand quantity must be a number greater than zero.";
            return;
        }
        IsBusy = true;
        try
        {
            var preview = await apiClient.PreviewCaseComponentDemandAsync(SelectedCase.CaseId, quantity);
            Replace(ComponentDemand, preview.Items);
            StatusMessage = $"Component demand preview for {preview.OrderQuantity:G} × {preview.PartNumber}.";
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = FriendlyMessage(exception); }
        finally { IsBusy = false; }
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
            var derivedOrdersTask = apiClient.ListDerivedCaseOrdersAsync(caseId);
            var batchesTask = apiClient.ListBatchesAsync(caseId);
            var componentsTask = apiClient.ListCaseComponentsAsync(caseId);
            var whereUsedTask = apiClient.ListCaseWhereUsedAsync(caseId);
            var machinesTask = apiClient.ListMachinesAsync();
            var machineTypesTask = apiClient.ListMachineTypesAsync();
            var calendarsTask = apiClient.ListWorkingCalendarsAsync();
            var previewTask = apiClient.GetCasePreviewAsync(caseId);
            await Task.WhenAll(
                caseTask,
                operationsTask,
                ordersTask,
                derivedOrdersTask,
                batchesTask,
                componentsTask,
                whereUsedTask,
                machinesTask,
                machineTypesTask,
                calendarsTask,
                previewTask);

            if (SelectedCase?.CaseId != caseId)
            {
                return;
            }

            var resource = await caseTask;
            entityTag = resource.EntityTag;
            ApplyCase(resource.Value);
            Replace(Operations, await operationsTask);
            RebuildOperationReferenceOptions(isEditingOperation ? SelectedOperation?.CaseOperationId : null);
            ApplyMachineTypeOptions(await machinesTask, await machineTypesTask);
            Replace(WorkingCalendars, await calendarsTask);
            Replace(Orders, await ordersTask);
            Replace(DerivedOrders, await derivedOrdersTask);
            Replace(Batches, await batchesTask);
            Replace(Components, await componentsTask);
            Replace(WhereUsed, await whereUsedTask);
            Replace(ComponentCaseOptions, Cases.Where(item => item.CaseId != caseId).ToArray());
            SelectedComponent = null;
            SelectedComponentCase = null;
            ComponentDemand.Clear();
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
        isParentCase = plannerCase.IsParent;
        isChildCase = plannerCase.IsChild;
        RaiseStateProperties();
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
        originalOrderStatus = null;
        editingOrderId = null;
        editingOrderEntityTag = null;
    }

    private void ResetOperationForm()
    {
        NewOperationNumber = string.Empty;
        NewOperationName = string.Empty;
        NewOperationRequiredMachineType = string.Empty;
        NewOperationSetupTime = string.Empty;
        NewOperationCycleTimePerPart = string.Empty;
        NewOperationQaTime = string.Empty;
        NewOperationLoadUnloadTime = string.Empty;
        NewOperationLoadUnloadRequiresWorker = false;
        NewOperationAutomaticLoading = false;
        NewOperationLoadUnloadEveryNParts = string.Empty;
        NewOperationDayShiftOnly = false;
        NewOperationHasExternalDelay = false;
        NewOperationExternalDelayDescription = null;
        NewOperationExternalDelayDuration = "0";
        NewOperationExternalDelayDurationUnit = "hours";
        NewOperationExternalDelayCalendarId = null;
        NewOperationRespectMasterCalendar = true;
        NewOperationDependencyType = "INDEPENDENT";
        NewOperationPredecessor = null;
        NewOperationSimultaneousGroupKey = string.Empty;
    }

    private void ApplyMachineTypeOptions(
        IReadOnlyList<PlannerMachine> machines,
        IReadOnlyList<PlannerMachineType> machineTypes)
    {
        var selected = NewOperationRequiredMachineType;
        var values = machines
            .SelectMany(machine => new[] { machine.ProcessType, machine.AxisType }
                .Concat(machine.Capabilities))
            .Concat(machineTypes.SelectMany(machineType =>
                new[] { machineType.Name }.Concat(machineType.Capabilities)))
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
        isEditingOrder = false;
        isCreatingBatch = false;
        isEditingBatch = false;
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
        Components.Clear();
        WhereUsed.Clear();
        ComponentDemand.Clear();
        ComponentCaseOptions.Clear();
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
        OnPropertyChanged(nameof(IsEditingOrder));
        OnPropertyChanged(nameof(IsOrderFormOpen));
        OnPropertyChanged(nameof(IsOrderListEnabled));
        OnPropertyChanged(nameof(OrderFormHeading));
        OnPropertyChanged(nameof(OrderSaveButtonText));
        OnPropertyChanged(nameof(OrderAuthorityText));
        OnPropertyChanged(nameof(OrderStatuses));
        OnPropertyChanged(nameof(IsCreatingBatch));
        OnPropertyChanged(nameof(IsEditingBatch));
        OnPropertyChanged(nameof(BatchFormHeading));
        OnPropertyChanged(nameof(BatchSaveButtonText));
        OnPropertyChanged(nameof(IsCreatingOperation));
        OnPropertyChanged(nameof(IsEditingOperation));
        OnPropertyChanged(nameof(OperationFormHeading));
        OnPropertyChanged(nameof(OperationSaveButtonText));
        OnPropertyChanged(nameof(CanBeginChildCreate));
        OnPropertyChanged(nameof(IsParentCase));
        OnPropertyChanged(nameof(IsChildCase));
        OnPropertyChanged(nameof(CanManageOperations));
        OnPropertyChanged(nameof(CanManageDirectOrders));
        OnPropertyChanged(nameof(CanManageBatches));
        OnPropertyChanged(nameof(CanCreateOrder));
        OnPropertyChanged(nameof(CanBeginEditOrder));
        OnPropertyChanged(nameof(CanCreateBatch));
        OnPropertyChanged(nameof(CanBeginEditBatch));
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
        BeginEditOrderCommand.RaiseCanExecuteChanged();
        CancelCreateOrderCommand.RaiseCanExecuteChanged();
        CreateOrderCommand.RaiseCanExecuteChanged();
        BeginCreateBatchCommand.RaiseCanExecuteChanged();
        BeginEditBatchCommand.RaiseCanExecuteChanged();
        CancelCreateBatchCommand.RaiseCanExecuteChanged();
        CreateBatchCommand.RaiseCanExecuteChanged();
        SaveComponentCommand.RaiseCanExecuteChanged();
        RemoveComponentCommand.RaiseCanExecuteChanged();
        PreviewComponentDemandCommand.RaiseCanExecuteChanged();
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

    internal BatchOrderAllocationViewModel(DerivedCaseOrder order)
    {
        OrderId = order.SourceOrderId;
        DerivedOrderKey = order.DerivedOrderKey;
        OrderNumber = $"{order.SourceOrderNumber} ({order.SourceParentPartNumber})";
        DemandQuantity = order.RemainingQuantity;
        Status = order.Status;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string OrderId { get; }

    public string? DerivedOrderKey { get; }

    public string OrderNumber { get; }

    public double DemandQuantity { get; }

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
