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

        await viewModel.EnsureLoadedAsync();

        Assert.Single(viewModel.Cases);
        Assert.Equal("PN-100", viewModel.PartNumber);
        Assert.Equal("Acme", viewModel.Customer);
        Assert.Single(viewModel.Operations);
        Assert.Single(viewModel.Orders);
        Assert.Single(viewModel.Batches);
        Assert.Equal(new CaseQuery("PN", "Acme", true), api.LastQuery);
        Assert.Equal(2, api.PreviewReads); // pool thumbnail and selected Case detail
        Assert.False(viewModel.IsFormReadOnly);
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
        viewModel.CurrentSetupTimeSeconds = "900";
        viewModel.CurrentCycleTimePerPartSeconds = "120";
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
        Assert.Equal("planned", api.LastBatchCreate!.Status);
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
        viewModel.NewOperationSetupTimeSeconds = "120";
        viewModel.NewOperationCycleTimePerPartSeconds = "45";
        viewModel.NewOperationDependencyType = "SEQUENTIAL";
        viewModel.NewOperationPredecessor = viewModel.Operations[0];
        await viewModel.CreateOperationAsync();

        Assert.NotNull(api.LastOperationCreate);
        Assert.Equal("operation-1", api.LastOperationCreate!.PredecessorCaseOperationId);
        Assert.Equal("SEQUENTIAL", api.LastOperationCreate.DependencyType);
        Assert.Equal(2, viewModel.Operations.Count);
        Assert.Equal(1, viewModel.Operations[1].RoutePosition);
        Assert.False(viewModel.IsCreatingOperation);
        Assert.Equal(24, api.LastGeneration);
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

        internal FakeApiClient(PlannerCase plannerCase)
        {
            this.plannerCase = plannerCase;
        }

        internal CaseQuery? LastQuery { get; private set; }
        internal CaseUpdate? LastUpdate { get; private set; }
        internal CaseUpdate? LastCreate { get; private set; }
        internal OrderCreate? LastOrderCreate { get; private set; }
        internal ProductionBatchCreate? LastBatchCreate { get; private set; }
        internal CaseOperationCreate? LastOperationCreate { get; private set; }
        internal string? LastEntityTag { get; private set; }
        internal string? LastClientId { get; private set; }
        internal long LastGeneration { get; private set; }
        internal int PreviewReads { get; private set; }

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
                create.CurrentSetupTimeSeconds,
                create.CurrentCycleTimePerPartSeconds,
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

        public Task<IReadOnlyList<PlannerOrder>> ListOrdersAsync(
            string caseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerOrder>>([
                new("order-1", caseId, "SO-1", 5, "2026-08-20", "ACTIVE", null)
            ]);

        public Task<PlannerOrder> CreateOrderAsync(
            OrderCreate create,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastOrderCreate = create;
            LastClientId = clientId;
            LastGeneration = editGeneration;
            return Task.FromResult(new PlannerOrder(
                "order-2",
                create.CaseId,
                create.OrderNumber,
                create.Quantity,
                create.WorkFinishDate,
                create.Status,
                create.Notes));
        }

        public Task<IReadOnlyList<ProductionBatch>> ListBatchesAsync(
            string caseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProductionBatch>>([
                new("batch-1", caseId, "B-1", "planned", 5, null, 1)
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
