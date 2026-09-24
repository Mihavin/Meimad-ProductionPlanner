using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Presentation.ToolPreparation;
using System.Security.Cryptography;

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
        Assert.Contains("ViewToolTableFileCommand", commandProperties);
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

    [Fact]
    public async Task Setup_export_downloads_exact_current_package_artifacts_without_a_workflow_command()
    {
        var bytes = "immutable package file"u8.ToArray();
        var artifact = new ProductionPackageArtifactInfo(
            "artifact-1", "RUNNABLE_NC", "nc/main.nc", bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), "gcode-1");
        var package = new ProductionPackageInfo(
            "package-1", 1, "operation-1", "run-1", "assignment-1", "machine-1",
            "gcode-1", "tools-1", null, "CNC_GCODE", false, null, null,
            new string('a', 64), DateTimeOffset.Parse("2026-09-01T10:00:00Z"),
            "tool-room-user", null, true, false, false, [artifact]);
        var api = new FakeApiClient([Item()], bytes);
        api.CurrentPackage = package;
        var viewModel = new PreparationQueueViewModel("SETUP_PENDING", "Setup", "Ready");
        viewModel.AttachSession(api);
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.PackageExport.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            await viewModel.ExportCurrentProductionPackageAsync(package, root);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "nc", "main.nc")));
            Assert.Equal("artifact-1", api.RequestedArtifactId);
            Assert.Contains("No workflow state changed", viewModel.Status, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Tool_room_open_tool_table_opens_the_editable_preparation_for_the_selected_operation()
    {
        var item = Item();
        var api = new FakeApiClient([item]) { ToolPreparation = ToolPreparationViewModelTests.Preparation() };
        var viewModel = new PreparationQueueViewModel(
            "TOOL_PREPARATION_PENDING", "Tool Room", "Tool preparation");
        viewModel.AttachSession(api, "client-1", "tool-room-1");
        viewModel.Selected = item;
        PreparationQueueActionRequest? routed = null;
        viewModel.ActionRequested += (_, request) => routed = request;

        Assert.True(viewModel.OpenToolTableCommand.CanExecute(null));
        await viewModel.OpenToolTableAsync();

        Assert.NotNull(routed);
        Assert.Equal("OPEN_TOOL_PREPARATION", routed.Action);
        Assert.Equal("operation-1", api.RequestedToolPreparationOperationId);
        var editor = Assert.IsType<ToolPreparationViewModel>(routed.Payload);
        Assert.Equal("operation-1", editor.BatchOperationId);
        Assert.Equal(3, editor.Tools.Count);
        Assert.Contains("no measurements", viewModel.Status, StringComparison.Ordinal);

        // Without a released Tool Table there is nothing to prepare.
        viewModel.Selected = item with { ToolTableReleaseId = null };
        Assert.False(viewModel.OpenToolTableCommand.CanExecute(null));
        Assert.False(viewModel.ViewToolTableFileCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("PROGRAMMING_PENDING")]
    [InlineData("TOOL_PREPARATION_PENDING")]
    [InlineData("SETUP_PENDING")]
    public void Every_queue_can_view_the_nc_release_when_one_exists(string stage)
    {
        var withRelease = Item() with { Stage = stage, CaseId = "case-1", CaseOperationId = "case-op-1" };
        var viewModel = new PreparationQueueViewModel(stage, "Queue", "Queue");
        viewModel.AttachSession(new FakeApiClient([]));

        viewModel.Selected = withRelease;
        Assert.True(viewModel.ViewNcFileCommand.CanExecute(null));

        viewModel.Selected = withRelease with { GCodeReleaseId = null };
        Assert.False(viewModel.ViewNcFileCommand.CanExecute(null));
    }

    private static PreparationQueueItem Item() => new(
        "TOOL_PREPARATION_PENDING", "operation-1", "run-1", "assignment-1",
        "machine-1", "M01", "Mill", "PN-1", "Part", "B1", 10, "Rough",
        "process-1", "gcode-1", "tools-1", "READY_FOR_SETUP",
        [new("toolOffsets", "Tool Offsets", "MISSING", "Offsets missing", false)]);

    internal sealed class FakeApiClient(
        IReadOnlyList<PreparationQueueItem> items,
        byte[]? artifactBytes = null)
        : IPlannerApiClient
    {
        internal string? RequestedStage { get; private set; }
        internal string? RequestedArtifactId { get; private set; }
        internal ProductionPackageInfo? CurrentPackage { get; set; }
        internal PlannerToolPreparation? ToolPreparation { get; set; }
        internal string? RequestedToolPreparationOperationId { get; private set; }
        internal List<ToolPreparationUpdate> SavedUpdates { get; } = [];
        internal string? SavedClientId { get; private set; }
        internal string? SavedUserId { get; private set; }
        internal Exception? SaveError { get; set; }

        public Task<PlannerToolPreparation> GetToolPreparationAsync(
            string batchOperationId,
            CancellationToken cancellationToken = default)
        {
            RequestedToolPreparationOperationId = batchOperationId;
            return Task.FromResult(ToolPreparation ?? throw new NotSupportedException());
        }

        /// <summary>Appends the version the way the Server does: the released rows keep their identity and take the saved values.</summary>
        public Task<PlannerToolPreparation> SaveToolPreparationAsync(
            string batchOperationId, ToolPreparationUpdate update, string clientId, string userId,
            CancellationToken cancellationToken = default)
        {
            SavedUpdates.Add(update);
            SavedClientId = clientId;
            SavedUserId = userId;
            if (SaveError is not null) throw SaveError;
            var current = ToolPreparation ?? throw new NotSupportedException();
            var version = update.ExpectedVersion + 1;
            var tools = current.Tools.Select(tool =>
            {
                var saved = update.Tools.FirstOrDefault(candidate => candidate.ToolIdentifier == tool.ToolIdentifier);
                return saved is null
                    ? tool
                    : tool with
                    {
                        OffsetNumber = saved.OffsetNumber,
                        MeasuredLength = saved.MeasuredLength,
                        MeasuredDiameter = saved.MeasuredDiameter,
                        ShapeType = saved.ShapeType,
                        Shape = saved.Shape,
                        Notes = saved.Notes,
                        Components = saved.Components.Select(component => new PlannerToolPreparationComponent(
                            component.Sequence, component.ComponentType, component.Name, component.CatalogNumber,
                            component.Length, component.Diameter, component.Notes)).ToArray()
                    };
            }).ToArray();
            ToolPreparation = current with
            {
                Version = version,
                ToolPreparationId = $"prep-{version}",
                SavedAt = DateTimeOffset.Parse("2026-09-24T09:30:00Z"),
                SavedBy = userId,
                Comment = update.Comment,
                SavedForToolTableReleaseId = update.ToolTableReleaseId,
                Tools = tools
            };
            return Task.FromResult(ToolPreparation);
        }

        public Task<ProductionPackageInfo?> GetCurrentProductionPackageAsync(
            string batchOperationId,
            CancellationToken cancellationToken = default) => Task.FromResult(CurrentPackage);

        public Task<IReadOnlyList<PreparationQueueItem>> ListPreparationQueueAsync(
            string stage,
            CancellationToken cancellationToken = default)
        {
            RequestedStage = stage;
            return Task.FromResult(items);
        }

        public Task<byte[]> ReadProductionPackageArtifactAsync(
            string batchOperationId, string artifactId,
            CancellationToken cancellationToken = default)
        {
            RequestedArtifactId = artifactId;
            return Task.FromResult(artifactBytes ?? []);
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
