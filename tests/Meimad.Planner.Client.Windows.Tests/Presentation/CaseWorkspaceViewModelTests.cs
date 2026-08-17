using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class CaseWorkspaceViewModelTests
{
    [Fact]
    public async Task Loads_case_pool_details_and_tabs_only_through_api_client()
    {
        var api = new FakeApiClient(CreateCase());
        var launcher = new FakeFolderLauncher();
        var viewModel = new CaseWorkspaceViewModel(launcher);
        viewModel.AttachSession(api, "windows-1", EditorStatus(7));
        viewModel.SearchText = "PN";
        viewModel.CustomerFilter = "Acme";
        viewModel.ActiveFilter = "Active";
        viewModel.CaseSort = "Closest Order delivery date";

        await viewModel.EnsureLoadedAsync();

        Assert.Single(viewModel.Cases);
        Assert.Equal("PN-100", viewModel.PartNumber);
        Assert.Equal("Acme", viewModel.Customer);
        Assert.Single(viewModel.Operations);
        Assert.Single(viewModel.Orders);
        Assert.Single(viewModel.Batches);
        Assert.Equal("00:10:00", viewModel.CurrentSetupTime);
        Assert.Equal("00:02:00", viewModel.CurrentCycleTimePerPart);
        Assert.Contains("mill", viewModel.OperationMachineTypeOptions);
        Assert.Contains("5-axis", viewModel.OperationMachineTypeOptions);
        Assert.Contains("probe", viewModel.OperationMachineTypeOptions);
        Assert.Contains("turning", viewModel.OperationMachineTypeOptions);
        Assert.Contains("automated", viewModel.OperationMachineTypeOptions);
        Assert.Contains(string.Empty, viewModel.OperationMachineTypeOptions);
        Assert.Single(viewModel.OperationMachineTypeOptions, value =>
            string.Equals(value, "mill", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new CaseQuery("PN", "Acme", true, "closestOrderDeliveryDate"), api.LastQuery);
        Assert.Contains("Customer name", viewModel.CaseSortOptions);
        Assert.Equal(2, api.PreviewReads); // pool thumbnail and selected Case detail
        Assert.False(viewModel.IsFormReadOnly);

        viewModel.InvalidateSelectedDetails();
        await viewModel.EnsureLoadedAsync();
        Assert.Equal(3, api.PreviewReads);
    }

    [Fact]
    public async Task Saves_with_server_edit_generation_and_opens_api_supplied_folder_path()
    {
        var api = new FakeApiClient(CreateCase());
        var launcher = new FakeFolderLauncher();
        var viewModel = new CaseWorkspaceViewModel(launcher);
        viewModel.AttachSession(api, "windows-1", EditorStatus(11));
        await viewModel.EnsureLoadedAsync();
        viewModel.Customer = "Updated Customer";

        await viewModel.SaveAsync();
        await viewModel.OpenWorkingFolderAsync();

        Assert.NotNull(api.LastUpdate);
        Assert.Equal("Updated Customer", api.LastUpdate!.Customer);
        Assert.Equal("windows-1", api.LastClientId);
        Assert.Equal(11, api.LastGeneration);
        Assert.Equal("\"case:case-1:v1\"", api.LastEntityTag);
        Assert.Equal(@"C:\Cases\PN-100", launcher.OpenedPath);
    }

    [Fact]
    public async Task Viewer_can_read_details_but_cannot_save()
    {
        var api = new FakeApiClient(CreateCase());
        var viewModel = new CaseWorkspaceViewModel(new FakeFolderLauncher());
        viewModel.AttachSession(api, "windows-1", EditorStatus(4) with
        {
            State = ClientEditState.Viewer
        });

        await viewModel.EnsureLoadedAsync();

        Assert.True(viewModel.IsFormReadOnly);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.BeginCreateOperationCommand.CanExecute(null));
        Assert.False(viewModel.BeginCreateOrderCommand.CanExecute(null));
        viewModel.SelectedOrder = viewModel.Orders.Single();
        Assert.False(viewModel.BeginEditOrderCommand.CanExecute(null));
        Assert.False(viewModel.BeginCreateBatchCommand.CanExecute(null));
    }

    [Fact]
    public async Task Editor_creates_case_with_complete_master_folder_and_picture_paths()
    {
        var api = new FakeApiClient(CreateCase());
        var viewModel = new CaseWorkspaceViewModel(new FakeFolderLauncher());
        viewModel.AttachSession(api, "windows-1", EditorStatus(15));
        await viewModel.EnsureLoadedAsync();

        await viewModel.BeginCreateAsync();
        viewModel.PartNumber = "PN-200";
        viewModel.Name = "New housing";
        viewModel.Revision = "B";
        viewModel.Customer = "Beta";
        viewModel.CustomerReference = "PO-200";
        viewModel.SetWorkingFolderSelection(@"C:\Cases\PN-200");
        viewModel.PreviewPath = @"C:\Cases\PN-200\picture.jpg";
        viewModel.MaterialType = "Steel";
        viewModel.MaterialSpecification = "4140";
        viewModel.RawMaterialForm = "Bar";
        viewModel.RawMaterialDimensions = "D80 x 200";
        viewModel.Notes = "Created in client";

        await viewModel.SaveAsync();

        Assert.NotNull(api.LastCreate);
        Assert.Equal(@"C:\Cases\PN-200", api.LastCreate!.WorkingFolderPath);
        Assert.Equal(@"C:\Cases\PN-200\picture.jpg", api.LastCreate.PreviewPath);
        Assert.Equal("Steel", api.LastCreate.MaterialType);
        Assert.Equal(15, api.LastGeneration);
        Assert.False(viewModel.IsCreating);
        Assert.Equal("PN-200", viewModel.SelectedCase?.PartNumber);
        Assert.Equal(2, viewModel.Cases.Count);
    }

    [Fact]
    public async Task Editor_creates_order_and_batch_with_explicit_combined_allocations()
    {
        var api = new FakeApiClient(CreateCase());
        var viewModel = new CaseWorkspaceViewModel(new FakeFolderLauncher());
        viewModel.AttachSession(api, "windows-1", EditorStatus(21));
        await viewModel.EnsureLoadedAsync();

        await viewModel.BeginCreateOrderAsync();
        Assert.Equal(["active", "cancelled"], viewModel.OrderStatuses);
        viewModel.NewOrderNumber = "SO-2";
        viewModel.NewOrderQuantity = "9";
        viewModel.NewOrderWorkFinishDate = "2026-09-30";
        viewModel.NewOrderStatus = "active";
        viewModel.NewOrderNotes = "Second demand";
        await viewModel.CreateOrderAsync();

        Assert.NotNull(api.LastOrderCreate);
        Assert.Equal("case-1", api.LastOrderCreate!.CaseId);
        Assert.Equal(9, api.LastOrderCreate.Quantity);
        Assert.Equal(2, viewModel.Orders.Count);
        Assert.False(viewModel.IsCreatingOrder);

        await viewModel.BeginCreateBatchAsync();
        Assert.Equal(2, viewModel.BatchOrderAllocations.Count);
        viewModel.NewBatchNumber = "B-2";
        viewModel.NewBatchPlannedQuantity = "12";
        viewModel.BatchOrderAllocations[0].AllocatedQuantity = "3";
        viewModel.BatchOrderAllocations[1].AllocatedQuantity = "6";
        viewModel.NewBatchStockQuantity = "2";
        viewModel.NewBatchScrapAllowance = "1";
        await viewModel.CreateBatchAsync();

        Assert.NotNull(api.LastBatchCreate);
        Assert.Equal("waiting", api.LastBatchCreate!.Status);
        Assert.Equal(4, api.LastBatchCreate.Allocations.Count);
        Assert.Contains(api.LastBatchCreate.Allocations, allocation =>
            allocation.AllocationType == "order" && allocation.OrderId == "order-2" && allocation.Quantity == 6);
        Assert.Contains(api.LastBatchCreate.Allocations, allocation =>
            allocation.AllocationType == "stock" && allocation.Quantity == 2);
        Assert.Contains(api.LastBatchCreate.Allocations, allocation =>
            allocation.AllocationType == "scrapAllowance" && allocation.Quantity == 1);
        Assert.Equal(2, viewModel.Batches.Count);
        Assert.False(viewModel.IsCreatingBatch);
        Assert.Equal(21, api.LastGeneration);
    }

    [Fact]
    public async Task Editor_edits_existing_order_with_etag_and_refreshes_visible_demand()
    {
        var api = new FakeApiClient(CreateCase());
        var viewModel = new CaseWorkspaceViewModel(new FakeFolderLauncher());
        viewModel.AttachSession(api, "windows-1", EditorStatus(23));
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectedOrder = viewModel.Orders.Single();
        var planChanged = 0;
        viewModel.PlanChanged += (_, _) => planChanged++;

        await viewModel.BeginEditOrderAsync();

        Assert.True(viewModel.IsEditingOrder);
        Assert.Contains("in_production", viewModel.OrderStatuses);
        Assert.Contains("complete", viewModel.OrderStatuses);
        Assert.Equal("EDIT ORDER", viewModel.OrderFormHeading);
        Assert.Equal("Save Order", viewModel.OrderSaveButtonText);
        Assert.Equal("SO-1", viewModel.NewOrderNumber);
        Assert.Equal("5", viewModel.NewOrderQuantity);
        Assert.Equal("2026-08-20", viewModel.NewOrderWorkFinishDate);
        Assert.Equal("ACTIVE", viewModel.NewOrderStatus);

        viewModel.NewOrderNumber = "SO-1-REVISED";
        viewModel.NewOrderQuantity = "8";
        viewModel.NewOrderWorkFinishDate = "2026-09-01";
        viewModel.NewOrderStatus = "in_production";
        viewModel.NewOrderNotes = "Planner revision";
        await viewModel.CreateOrderAsync();

        Assert.NotNull(api.LastOrderUpdate);
        Assert.Equal("SO-1-REVISED", api.LastOrderUpdate!.OrderNumber);
        Assert.Equal(8, api.LastOrderUpdate.Quantity);
        Assert.Equal("2026-09-01", api.LastOrderUpdate.WorkFinishDate);
        Assert.Equal("in_production", api.LastOrderUpdate.Status);
        Assert.Equal("Planner revision", api.LastOrderUpdate.Notes);
        Assert.Equal("\"order:order-1:v1\"", api.LastOrderEntityTag);
        Assert.Equal("windows-1", api.LastClientId);
        Assert.Equal(23, api.LastGeneration);
        Assert.Equal("SO-1-REVISED", Assert.Single(viewModel.Orders).OrderNumber);
        Assert.Equal(2, viewModel.SelectedOrder?.Version);
        Assert.False(viewModel.IsOrderFormOpen);
        Assert.Equal(1, planChanged);
    }

    [Fact]
    public async Task Editor_edits_existing_batch_with_etag_and_prefilled_allocations()
    {
        var api = new FakeApiClient(CreateCase());
        var viewModel = new CaseWorkspaceViewModel(new FakeFolderLauncher());
        viewModel.AttachSession(api, "windows-1", EditorStatus(24));
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectedBatch = viewModel.Batches.Single();

        await viewModel.BeginEditBatchAsync();

        Assert.True(viewModel.IsEditingBatch);
        Assert.Equal("EDIT PRODUCTION BATCH", viewModel.BatchFormHeading);
        Assert.Equal("B-1", viewModel.NewBatchNumber);
        Assert.Equal("5", viewModel.NewBatchPlannedQuantity);
        Assert.Equal("5", Assert.Single(viewModel.BatchOrderAllocations).AllocatedQuantity);

        viewModel.NewBatchNumber = "B-1-EDITED";
        viewModel.NewBatchPlannedQuantity = "7";
        viewModel.BatchOrderAllocations.Single().AllocatedQuantity = "4";
        viewModel.NewBatchStockQuantity = "3";
        await viewModel.CreateBatchAsync();

        Assert.NotNull(api.LastBatchUpdate);
        Assert.Equal("B-1-EDITED", api.LastBatchUpdate!.BatchNumber);
        Assert.Equal(7, api.LastBatchUpdate.PlannedQuantity);
        Assert.Equal("\"batch:batch-1:v1\"", api.LastBatchEntityTag);
        Assert.Equal(24, api.LastGeneration);
        Assert.Equal("B-1-EDITED", viewModel.SelectedBatch?.BatchNumber);
        Assert.False(viewModel.IsCreatingBatch);
    }

    [Fact]
    public async Task Order_edit_omits_unchanged_status_so_server_can_rederive_it_after_quantity_changes()
    {
        var api = new FakeApiClient(CreateCase());
        var viewModel = new CaseWorkspaceViewModel(new FakeFolderLauncher());
        viewModel.AttachSession(api, "windows-1", EditorStatus(23));
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectedOrder = viewModel.Orders.Single();
        await viewModel.BeginEditOrderAsync();
        viewModel.NewOrderQuantity = "8";

        await viewModel.CreateOrderAsync();

        Assert.NotNull(api.LastOrderUpdate);
        Assert.Null(api.LastOrderUpdate!.Status);
        Assert.Equal(8, api.LastOrderUpdate.Quantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Order_edit_keeps_its_original_identity_when_the_grid_selection_changes(bool clearSelection)
    {
        var api = new FakeApiClient(CreateCase());
        var viewModel = new CaseWorkspaceViewModel(new FakeFolderLauncher());
        viewModel.AttachSession(api, "windows-1", EditorStatus(23));
        await viewModel.EnsureLoadedAsync();
        var original = viewModel.Orders.Single();
        viewModel.SelectedOrder = original;
        await viewModel.BeginEditOrderAsync();
        Assert.False(viewModel.IsOrderListEnabled);

        if (clearSelection)
        {
            viewModel.SelectedOrder = null;
        }
        else
        {
            var other = new PlannerOrder(
                "order-other", "case-1", "SO-OTHER", 2, "2026-08-22", "active", null);
            viewModel.Orders.Add(other);
            viewModel.SelectedOrder = other;
        }

        viewModel.NewOrderNumber = "SO-1-SAFE";
        await viewModel.CreateOrderAsync();

        Assert.Equal("order-1", api.LastOrderId);
        Assert.Equal("\"order:order-1:v1\"", api.LastOrderEntityTag);
        Assert.Null(api.LastOrderCreate);
        Assert.Equal("SO-1-SAFE", api.LastOrderUpdate?.OrderNumber);
        Assert.True(viewModel.IsOrderListEnabled);
    }

    [Fact]
    public async Task Order_edit_reloads_server_sort_and_reselects_immutable_target_when_due_date_crosses_another_order()
    {
        var api = new FakeApiClient(
            CreateCase(),
            [
                new("order-1", "case-1", "SO-1", 5, "2026-08-20", "active", null),
                new("order-2", "case-1", "SO-2", 7, "2026-09-10", "active", null)
            ]);
        var viewModel = new CaseWorkspaceViewModel(new FakeFolderLauncher());
        viewModel.AttachSession(api, "windows-1", EditorStatus(23));
        await viewModel.EnsureLoadedAsync();
        var editTarget = viewModel.Orders.Single(order => order.OrderId == "order-2");
        viewModel.SelectedOrder = editTarget;
        await viewModel.BeginEditOrderAsync();

        viewModel.SelectedOrder = viewModel.Orders.Single(order => order.OrderId == "order-1");
        viewModel.NewOrderWorkFinishDate = "2026-08-10";
        await viewModel.CreateOrderAsync();

        Assert.Equal("order-2", api.LastOrderId);
        Assert.Equal(2, api.OrderListReads);
        Assert.Equal(["order-2", "order-1"], viewModel.Orders.Select(order => order.OrderId));
        Assert.Equal("order-2", viewModel.SelectedOrder?.OrderId);
        Assert.Equal("2026-08-10", viewModel.SelectedOrder?.WorkFinishDate);
    }

    [Theory]
    [InlineData("0", "2026-09-01", "whole number")]
    [InlineData("5", "09/01/2026", "YYYY-MM-DD")]
    public async Task Invalid_order_edit_stays_open_and_does_not_call_server(
        string quantity,
        string finishDate,
        string expectedFeedback)
    {
        var api = new FakeApiClient(CreateCase());
        var viewModel = new CaseWorkspaceViewModel(new FakeFolderLauncher());
        viewModel.AttachSession(api, "windows-1", EditorStatus(23));
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectedOrder = viewModel.Orders.Single();
        await viewModel.BeginEditOrderAsync();
        viewModel.NewOrderQuantity = quantity;
        viewModel.NewOrderWorkFinishDate = finishDate;

        await viewModel.CreateOrderAsync();

        Assert.Null(api.LastOrderUpdate);
        Assert.True(viewModel.IsEditingOrder);
        Assert.Contains(expectedFeedback, viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editor_creates_case_operation_with_dependency_through_server_api()
    {
        var api = new FakeApiClient(CreateCase());
        var viewModel = new CaseWorkspaceViewModel(new FakeFolderLauncher());
        viewModel.AttachSession(api, "windows-1", EditorStatus(24));
        await viewModel.EnsureLoadedAsync();

        await viewModel.BeginCreateOperationAsync();
        viewModel.NewOperationNumber = "20";
        viewModel.NewOperationName = "Finish mill";
        viewModel.NewOperationRequiredMachineType = "fiveAxisMill";
        viewModel.NewOperationSetupTime = "01:02:03";
        viewModel.NewOperationCycleTimePerPart = "00:00:45";
        viewModel.NewOperationDependencyType = "SEQUENTIAL";
        viewModel.NewOperationPredecessor = viewModel.Operations[0];
        await viewModel.CreateOperationAsync();

        Assert.NotNull(api.LastOperationCreate);
        Assert.Equal("operation-1", api.LastOperationCreate!.PredecessorCaseOperationId);
        Assert.Equal("SEQUENTIAL", api.LastOperationCreate.DependencyType);
        Assert.Equal(3723, api.LastOperationCreate.SetupTimeSeconds);
        Assert.Equal(45, api.LastOperationCreate.CycleTimePerPartSeconds);
        Assert.Equal(2, viewModel.Operations.Count);
        Assert.Equal(1, viewModel.Operations[1].RoutePosition);
        Assert.False(viewModel.IsCreatingOperation);
        Assert.Equal("01:02:13", viewModel.CurrentSetupTime);
        Assert.Equal("00:01:05", viewModel.CurrentCycleTimePerPart);
        Assert.Equal(24, api.LastGeneration);
    }

    [Fact]
    public async Task Editor_edits_operation_with_etag_legacy_machine_value_and_total_hours_duration()
    {
        var api = new FakeApiClient(CreateCase());
        var viewModel = new CaseWorkspaceViewModel(new FakeFolderLauncher());
        viewModel.AttachSession(api, "windows-1", EditorStatus(25));
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectedOperation = viewModel.Operations.Single();

        await viewModel.BeginEditOperationAsync();

        Assert.True(viewModel.IsEditingOperation);
        Assert.Contains("SAW", viewModel.OperationMachineTypeOptions);
        Assert.Empty(viewModel.OperationReferenceOptions);
        viewModel.NewOperationName = "Saw revised";
        viewModel.NewOperationRequiredMachineType = "5-axis";
        viewModel.NewOperationSetupTime = "25:00:01";
        viewModel.NewOperationCycleTimePerPart = string.Empty;
        viewModel.NewOperationDependencyType = "INDEPENDENT";
        await viewModel.CreateOperationAsync();

        Assert.NotNull(api.LastOperationUpdate);
        Assert.Equal(90_001, api.LastOperationUpdate!.SetupTimeSeconds);
        Assert.Null(api.LastOperationUpdate.CycleTimePerPartSeconds);
        Assert.Equal("\"case-operation:operation-1:v1\"", api.LastOperationEntityTag);
        Assert.Equal("Saw revised", Assert.Single(viewModel.Operations).Name);
        Assert.Equal("25:00:01", viewModel.CurrentSetupTime);
        Assert.Equal("00:00:00", viewModel.CurrentCycleTimePerPart);
        Assert.False(viewModel.IsCreatingOperation);
    }

    private static PlannerCase CreateCase() => new(
        "case-1",
        "PN-100",
        "Bearing housing",
        "A",
        "Acme",
        "PO-1",
        null,
        @"C:\Cases\PN-100",
        "Aluminium",
        "7075-T6",
        "Plate",
        "30 x 100 x 100 mm",
        600,
        120,
        "Test",
        true,
        1,
        DateTimeOffset.Parse("2026-08-11T00:00:00Z"),
        DateTimeOffset.Parse("2026-08-11T00:00:00Z"));

    private static EditModeStatus EditorStatus(long generation) => new(
        ClientEditState.Editor,
        generation,
        new EditModeHolder("windows-1", "planner", generation, DateTimeOffset.UtcNow),
        null,
        DateTimeOffset.UtcNow,
        30);

    private sealed class FakeFolderLauncher : IWorkingFolderLauncher
    {
        internal string? OpenedPath { get; private set; }

        public void Open(string path) => OpenedPath = path;
    }

    private sealed class FakeApiClient : IPlannerApiClient
    {
        private PlannerCase plannerCase;
        private readonly List<PlannerOrder> orders;

        internal FakeApiClient(
            PlannerCase plannerCase,
            IReadOnlyList<PlannerOrder>? initialOrders = null)
        {
            this.plannerCase = plannerCase;
            orders = (initialOrders ??
            [
                new PlannerOrder(
                    "order-1",
                    plannerCase.CaseId,
                    "SO-1",
                    5,
                    "2026-08-20",
                    "ACTIVE",
                    null)
            ]).ToList();
        }

        internal CaseQuery? LastQuery { get; private set; }
        internal CaseUpdate? LastUpdate { get; private set; }
        internal CaseUpdate? LastCreate { get; private set; }
        internal OrderCreate? LastOrderCreate { get; private set; }
        internal OrderUpdate? LastOrderUpdate { get; private set; }
        internal string? LastOrderId { get; private set; }
        internal string? LastOrderEntityTag { get; private set; }
        internal ProductionBatchCreate? LastBatchCreate { get; private set; }
        internal ProductionBatchUpdate? LastBatchUpdate { get; private set; }
        internal string? LastBatchEntityTag { get; private set; }
        internal CaseOperationCreate? LastOperationCreate { get; private set; }
        internal CaseOperationUpdate? LastOperationUpdate { get; private set; }
        internal string? LastOperationEntityTag { get; private set; }
        internal string? LastEntityTag { get; private set; }
        internal string? LastClientId { get; private set; }
        internal long LastGeneration { get; private set; }
        internal int PreviewReads { get; private set; }
        internal int OrderListReads { get; private set; }

        public Task<IReadOnlyList<PlannerCase>> ListCasesAsync(
            CaseQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<PlannerCase>>([plannerCase]);
        }

        public Task<CaseResource> GetCaseAsync(
            string caseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CaseResource(plannerCase, $"\"case:{caseId}:v{plannerCase.Version}\""));

        public Task<CaseResource> UpdateCaseAsync(
            string caseId,
            CaseUpdate update,
            string entityTag,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastUpdate = update;
            LastEntityTag = entityTag;
            LastClientId = clientId;
            LastGeneration = editGeneration;
            plannerCase = plannerCase with
            {
                PartNumber = update.PartNumber,
                Name = update.Name,
                Customer = update.Customer,
                Version = plannerCase.Version + 1
            };
            return Task.FromResult(new CaseResource(
                plannerCase,
                $"\"case:{caseId}:v{plannerCase.Version}\""));
        }

        public Task<CaseResource> CreateCaseAsync(
            CaseUpdate create,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastCreate = create;
            LastClientId = clientId;
            LastGeneration = editGeneration;
            plannerCase = new PlannerCase(
                "case-2",
                create.PartNumber,
                create.Name,
                create.Revision,
                create.Customer,
                create.CustomerReference,
                create.PreviewPath,
                create.WorkingFolderPath,
                create.MaterialType,
                create.MaterialSpecification,
                create.RawMaterialForm,
                create.RawMaterialDimensions,
                null,
                null,
                create.Notes,
                false,
                1,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            return Task.FromResult(new CaseResource(plannerCase, "\"case:case-2:v1\""));
        }

        public Task<IReadOnlyList<CaseOperation>> ListCaseOperationsAsync(
            string caseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaseOperation>>([
                new("operation-1", caseId, 10, 0, "Saw", "SAW", 10, 20,
                    "SEQUENTIAL", null, null)
            ]);

        public Task<CaseOperation> CreateCaseOperationAsync(
            string caseId,
            CaseOperationCreate create,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastOperationCreate = create;
            LastClientId = clientId;
            LastGeneration = editGeneration;
            return Task.FromResult(new CaseOperation(
                "operation-2",
                caseId,
                create.OperationNumber,
                1,
                create.Name,
                create.RequiredMachineType,
                create.SetupTimeSeconds,
                create.CycleTimePerPartSeconds,
                create.DependencyType,
                create.PredecessorCaseOperationId,
                create.SimultaneousGroupKey));
        }

        public Task<CaseOperation> UpdateCaseOperationAsync(
            string caseId,
            string operationId,
            CaseOperationUpdate update,
            string entityTag,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastOperationUpdate = update;
            LastOperationEntityTag = entityTag;
            LastClientId = clientId;
            LastGeneration = editGeneration;
            return Task.FromResult(new CaseOperation(
                operationId,
                caseId,
                update.OperationNumber,
                0,
                update.Name,
                update.RequiredMachineType,
                update.SetupTimeSeconds,
                update.CycleTimePerPartSeconds,
                update.DependencyType,
                update.PredecessorCaseOperationId,
                update.SimultaneousGroupKey,
                2));
        }

        public Task<IReadOnlyList<PlannerMachine>> ListMachinesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerMachine>>([
                new(
                    "machine-1", "M-1", "Mill", "mill", "5-axis", ["probe", "MILL"],
                    "calendar-1", true, true, null, null, 0, 1,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]);

        public Task<IReadOnlyList<PlannerMachineType>> ListMachineTypesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerMachineType>>([
                new(
                    "machine-type-turning",
                    "turning",
                    ["automated"],
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow)
            ]);

        public Task<IReadOnlyList<PlannerOrder>> ListOrdersAsync(
            string caseId,
            CancellationToken cancellationToken = default)
        {
            OrderListReads++;
            return Task.FromResult<IReadOnlyList<PlannerOrder>>(orders
                .Where(order => order.CaseId == caseId)
                .OrderBy(order => order.WorkFinishDate, StringComparer.Ordinal)
                .ThenBy(order => order.OrderNumber, StringComparer.OrdinalIgnoreCase)
                .ThenBy(order => order.OrderId, StringComparer.Ordinal)
                .ToArray());
        }

        public Task<PlannerOrder> CreateOrderAsync(
            OrderCreate create,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastOrderCreate = create;
            LastClientId = clientId;
            LastGeneration = editGeneration;
            var created = new PlannerOrder(
                "order-2",
                create.CaseId,
                create.OrderNumber,
                create.Quantity,
                create.WorkFinishDate,
                create.Status,
                create.Notes);
            orders.Add(created);
            return Task.FromResult(created);
        }

        public Task<PlannerOrder> UpdateOrderAsync(
            string orderId,
            OrderUpdate update,
            string entityTag,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastOrderId = orderId;
            LastOrderUpdate = update;
            LastOrderEntityTag = entityTag;
            LastClientId = clientId;
            LastGeneration = editGeneration;
            var current = orders.Single(order => order.OrderId == orderId);
            var updated = new PlannerOrder(
                orderId,
                current.CaseId,
                update.OrderNumber,
                update.Quantity,
                update.WorkFinishDate,
                update.Status ?? current.Status,
                update.Notes,
                current.Version + 1);
            orders[orders.IndexOf(current)] = updated;
            return Task.FromResult(updated);
        }

        public Task<IReadOnlyList<ProductionBatch>> ListBatchesAsync(
            string caseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProductionBatch>>([
                new("batch-1", caseId, "B-1", "waiting", 5, null, 1, 1,
                    [new("allocation-1", "order", "order-1", 5)])
            ]);

        public Task<ProductionBatch> CreateBatchAsync(
            ProductionBatchCreate create,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastBatchCreate = create;
            LastClientId = clientId;
            LastGeneration = editGeneration;
            return Task.FromResult(new ProductionBatch(
                "batch-2",
                create.CaseId,
                create.BatchNumber,
                create.Status,
                create.PlannedQuantity,
                1,
                2));
        }

        public Task<ProductionBatch> UpdateBatchAsync(
            string batchId,
            ProductionBatchUpdate update,
            string entityTag,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastBatchUpdate = update;
            LastBatchEntityTag = entityTag;
            LastClientId = clientId;
            LastGeneration = editGeneration;
            return Task.FromResult(new ProductionBatch(
                batchId, plannerCase.CaseId, update.BatchNumber, "waiting", update.PlannedQuantity,
                null, 1, 2,
                update.Allocations.Select((value, index) => new BatchAllocation(
                    $"allocation-{index}", value.AllocationType, value.OrderId, value.Quantity)).ToArray()));
        }

        public Task<byte[]?> GetCasePreviewAsync(
            string caseId,
            CancellationToken cancellationToken = default)
        {
            PreviewReads++;
            return Task.FromResult<byte[]?>(null);
        }

        public Task<PlanningBoardSnapshot> GetPlanningBoardAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TimelineSnapshot> GetTimelineAsync(
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AssignOrMoveOperationAsync(
            string batchOperationId,
            string machineId,
            int backlogPosition,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UnassignOperationAsync(
            string batchOperationId,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EditModeStatus> GetEditModeAsync(
            string clientId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EditModeStatus> RequestEditAsync(
            string clientId,
            string userId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EditModeStatus> ReleaseEditAsync(
            string clientId,
            long generation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EditModeStatus> DecideTransferAsync(
            string clientId,
            long generation,
            string requestId,
            bool release,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
