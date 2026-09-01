using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class PreparationQueueViewModelTests
{
    [Fact]
    public async Task Shared_queue_model_loads_only_its_configured_role_projection()
    {
        var item = Item();
        var api = new FakeApiClient([item]);
        var viewModel = new PreparationQueueViewModel(
            "TOOL_PREPARATION_PENDING", "Tool Room", "Tool preparation");

        viewModel.AttachSession(api);
        await viewModel.RefreshAsync();

        Assert.Equal("TOOL_PREPARATION_PENDING", api.RequestedStage);
        Assert.Same(item, Assert.Single(viewModel.Items));
        Assert.Contains("1 operation", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Preparation_queue_exposes_no_machine_mutation_command()
    {
        var commandProperties = typeof(PreparationQueueViewModel).GetProperties()
            .Where(property => typeof(System.Windows.Input.ICommand).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains("RefreshCommand", commandProperties);
        Assert.Contains("OpenCaseCommand", commandProperties);
        Assert.Contains("OpenOperationCommand", commandProperties);
        Assert.Contains("UploadGCodeCommand", commandProperties);
        Assert.Contains("OpenToolTableCommand", commandProperties);
        Assert.Contains("ViewNcFileCommand", commandProperties);
        Assert.Contains("CreateProductionPackageCommand", commandProperties);
        Assert.Contains("OpenProductionPackageCommand", commandProperties);
        Assert.DoesNotContain(commandProperties,
            name => name.Contains("Machine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Nc_creator_upload_action_routes_to_existing_operation_workflow()
    {
        var item = Item() with
        {
            Stage = "PROGRAMMING_PENDING",
            CaseId = "case-1",
            CaseOperationId = "case-operation-1"
        };
        var viewModel = new PreparationQueueViewModel(
            "PROGRAMMING_PENDING", "NC Creator", "Programming");
        viewModel.AttachSession(new FakeApiClient([item]), "client-1", "programmer-1");
        viewModel.Selected = item;
        PreparationQueueActionRequest? routed = null;
        viewModel.ActionRequested += (_, request) => routed = request;

        Assert.True(viewModel.UploadGCodeCommand.CanExecute(null));
        viewModel.UploadGCodeCommand.Execute(null);

        Assert.NotNull(routed);
        Assert.Equal("UPLOAD_GCODE", routed.Action);
        Assert.Equal("case-1", routed.Item.CaseId);
        Assert.Equal("case-operation-1", routed.Item.CaseOperationId);
    }

    private static PreparationQueueItem Item() => new(
        "TOOL_PREPARATION_PENDING", "operation-1", "run-1", "assignment-1",
        "machine-1", "M01", "Mill", "PN-1", "Part", "B1", 10, "Rough",
        "process-1", "gcode-1", "tools-1", "READY_FOR_SETUP",
        [new("toolOffsets", "Tool Offsets", "MISSING", "Offsets missing", false)]);

    private sealed class FakeApiClient(IReadOnlyList<PreparationQueueItem> items)
        : IPlannerApiClient
    {
        internal string? RequestedStage { get; private set; }

        public Task<IReadOnlyList<PreparationQueueItem>> ListPreparationQueueAsync(
            string stage,
            CancellationToken cancellationToken = default)
        {
            RequestedStage = stage;
            return Task.FromResult(items);
        }

        public Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EditModeStatus> GetEditModeAsync(string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EditModeStatus> RequestEditAsync(string clientId, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EditModeStatus> ReleaseEditAsync(string clientId, long generation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EditModeStatus> DecideTransferAsync(string clientId, long generation, string requestId, bool release, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlannerCase>> ListCasesAsync(CaseQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CaseResource> GetCaseAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CaseResource> UpdateCaseAsync(string caseId, CaseUpdate update, string entityTag, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CaseOperation>> ListCaseOperationsAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlannerOrder>> ListOrdersAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProductionBatch>> ListBatchesAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]?> GetCasePreviewAsync(string caseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlanningBoardSnapshot> GetPlanningBoardAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TimelineSnapshot> GetTimelineAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AssignOrMoveOperationAsync(string batchOperationId, string machineId, int backlogPosition, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UnassignOperationAsync(string batchOperationId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
