using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class OperationRequirementsViewModelTests
{
    [Fact]
    public async Task Loading_an_operation_lists_its_steps_before_the_machine_first_with_resource_names()
    {
        var api = new FakeApiClient(
            Requirement("req-final", 1, "WORKSTATION", "FORWARD", "Final inspection", 50, 900, 0, workstationTypeId: "type-inspection", predecessorId: "req-deburr", kitaron: true),
            Requirement("req-deburr", 0, "WORKSTATION", "FORWARD", "Deburr", 40, 600, 180, workstationTypeId: "type-deburr"),
            Requirement("req-receipt", 0, "WORKSTATION", "BACKWARD", "Receipt", 20, 300, 0, workstationTypeId: "type-inspection"),
            Requirement("req-chrome", 2, "EXTERNAL", "FORWARD", "Chrome", 60, 0, 0, externalResourceId: "external-chrome"));
        var viewModel = new OperationRequirementsViewModel();
        viewModel.AttachSession(api, "client", 1, editor: true);

        await viewModel.LoadAsync("case-op-1");

        Assert.Equal(["Receipt", "Deburr", "Final inspection", "Chrome"], viewModel.Requirements.Select(row => row.Name).ToArray());
        Assert.Contains("4 auxiliary step(s): 1 before and 3 after the Machine; 1 from Kitaron", viewModel.Summary);
        var deburr = viewModel.Requirements.Single(row => row.Name == "Deburr");
        Assert.Equal("Deburring", deburr.ResourceText);
        Assert.Equal("10", deburr.PerBatchText);
        Assert.Equal("3", deburr.PerPartText);
        Assert.Equal("Manual", deburr.OriginLabel);
        var final = viewModel.Requirements.Single(row => row.Name == "Final inspection");
        Assert.Equal("Deburr", final.PredecessorText);
        Assert.Equal("Kitaron", final.OriginLabel);
        var chrome = viewModel.Requirements.Single(row => row.Name == "Chrome");
        Assert.Equal("Chrome plating", chrome.ResourceText);
        Assert.Equal("lead time", chrome.PerBatchText);
        Assert.True(viewModel.BeginAddCommand.CanExecute(null));

        await viewModel.LoadAsync(null);
        Assert.Empty(viewModel.Requirements);
        Assert.False(viewModel.HasOperation);
    }

    [Fact]
    public async Task Adding_a_step_converts_minutes_to_seconds_and_creates_it_on_the_operation()
    {
        var api = new FakeApiClient();
        var viewModel = new OperationRequirementsViewModel();
        viewModel.AttachSession(api, "client", 3, editor: true);
        await viewModel.LoadAsync("case-op-1");

        viewModel.BeginAddCommand.Execute(null);
        await WaitForAsync(() => viewModel.IsEditing);
        viewModel.Name = "Deburr";
        viewModel.StepNumber = "40";
        viewModel.ResourceClass = "WORKSTATION";
        viewModel.SelectedWorkstationType = viewModel.WorkstationTypes.Single(type => type.Id == "type-deburr");
        viewModel.Direction = "FORWARD";
        viewModel.MinutesPerBatch = "10";
        viewModel.MinutesPerPart = "2.5";
        viewModel.Capacity = "1";

        var (create, update, problem) = viewModel.BuildPayload();
        Assert.Null(problem);
        Assert.Null(update);
        Assert.NotNull(create);
        Assert.Equal(600, create.EstimatedDurationSeconds);
        Assert.Equal(150, create.DurationPerUnitSeconds);
        Assert.Equal(40, create.StepNumber);
        Assert.Equal("type-deburr", create.WorkstationTypeId);
        Assert.Null(create.ExternalResourceId);

        viewModel.SaveCommand.Execute(null);
        await WaitForAsync(() => api.Created.Count == 1);
        Assert.Equal("case-op-1", api.Created[0].OperationId);
        Assert.Equal("Deburr", api.Created[0].Create.Name);
        await WaitForAsync(() => viewModel.Requirements.Count == 1);
        Assert.False(viewModel.IsEditing);
        Assert.Equal("Auxiliary step saved.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Editing_and_deleting_send_the_version_and_the_editor_validates_the_class_target()
    {
        var api = new FakeApiClient(
            Requirement("req-chrome", 0, "EXTERNAL", "FORWARD", "Chrome", 60, 0, 0, externalResourceId: "external-chrome", version: 4));
        var viewModel = new OperationRequirementsViewModel();
        viewModel.AttachSession(api, "client", 1, editor: true);
        await viewModel.LoadAsync("case-op-1");
        viewModel.SelectedRequirement = viewModel.Requirements.Single();

        viewModel.BeginEditCommand.Execute(null);
        await WaitForAsync(() => viewModel.IsEditing);
        Assert.Equal("EXTERNAL", viewModel.ResourceClass);
        Assert.False(viewModel.HasDurations);
        Assert.Equal("Chrome", viewModel.Name);
        viewModel.SelectedExternalResource = null;
        Assert.Contains("External Resource", viewModel.BuildPayload().Problem);

        viewModel.SelectedExternalResource = viewModel.ExternalResources.Single();
        viewModel.IsActive = false;
        var (create, update, problem) = viewModel.BuildPayload();
        Assert.Null(problem);
        Assert.Null(create);
        Assert.NotNull(update);
        Assert.Equal(4, update.ExpectedVersion);
        Assert.False(update.IsActive);
        Assert.Equal("external-chrome", update.ExternalResourceId);

        viewModel.SaveCommand.Execute(null);
        await WaitForAsync(() => api.Updated.Count == 1);
        Assert.Equal("req-chrome", api.Updated[0].RequirementId);

        await WaitForAsync(() => !viewModel.IsBusy);
        viewModel.SelectedRequirement = viewModel.Requirements.Single();
        viewModel.DeleteCommand.Execute(null);
        await WaitForAsync(() => api.Deleted.Count == 1);
        Assert.Equal(("req-chrome", 5), api.Deleted[0]);
    }

    [Fact]
    public async Task Viewers_see_the_steps_but_cannot_change_them()
    {
        var api = new FakeApiClient(Requirement("req-deburr", 0, "WORKSTATION", "FORWARD", "Deburr", 40, 600, 0, workstationTypeId: "type-deburr"));
        var viewModel = new OperationRequirementsViewModel();
        viewModel.AttachSession(api, "client", 0, editor: false);
        await viewModel.LoadAsync("case-op-1");
        viewModel.SelectedRequirement = viewModel.Requirements.Single();

        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.BeginAddCommand.CanExecute(null));
        Assert.False(viewModel.BeginEditCommand.CanExecute(null));
        Assert.False(viewModel.DeleteCommand.CanExecute(null));
    }

    private static PlannerOperationRequirement Requirement(
        string id, int position, string resourceClass, string direction, string name, int step, int perBatch, int perUnit,
        string? workstationTypeId = null, string? externalResourceId = null, string? predecessorId = null, bool kitaron = false, int version = 1) =>
        new(id, "case-op-1", position, resourceClass, workstationTypeId, externalResourceId, null, null, 1, perBatch, direction, null,
            predecessorId, true, version, name, step, perUnit, kitaron);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeApiClient(params PlannerOperationRequirement[] initial) : StubPlannerApiClient, IPlannerApiClient
    {
        private readonly List<PlannerOperationRequirement> requirements = [.. initial];

        internal List<(string OperationId, OperationRequirementCreate Create)> Created { get; } = [];

        internal List<(string RequirementId, OperationRequirementUpdate Update)> Updated { get; } = [];

        internal List<(string RequirementId, int Version)> Deleted { get; } = [];

        public Task<IReadOnlyList<PlannerOperationRequirement>> ListOperationRequirementsAsync(string caseOperationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerOperationRequirement>>(requirements.Where(item => item.CaseOperationId == caseOperationId).ToArray());

        public Task<PlannerOperationRequirement> CreateOperationRequirementAsync(string caseOperationId, OperationRequirementCreate create, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            Created.Add((caseOperationId, create));
            var value = new PlannerOperationRequirement($"req-{Created.Count}", caseOperationId, create.SequencePosition, create.ResourceClass,
                create.WorkstationTypeId, create.ExternalResourceId, create.RequiredCapability, create.RequiredSkillId, create.CapacityRequired,
                create.EstimatedDurationSeconds, create.Direction, null, create.PredecessorRequirementId, true, 1, create.Name, create.StepNumber,
                create.DurationPerUnitSeconds);
            requirements.Add(value);
            return Task.FromResult(value);
        }

        public Task<PlannerOperationRequirement> UpdateOperationRequirementAsync(string requirementId, OperationRequirementUpdate update, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            Updated.Add((requirementId, update));
            var index = requirements.FindIndex(item => item.Id == requirementId);
            var value = requirements[index] with
            {
                ExternalResourceId = update.ExternalResourceId, WorkstationTypeId = update.WorkstationTypeId, IsActive = update.IsActive,
                Version = update.ExpectedVersion + 1, Name = update.Name
            };
            requirements[index] = value;
            return Task.FromResult(value);
        }

        public Task DeleteOperationRequirementAsync(string requirementId, int version, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            Deleted.Add((requirementId, version));
            requirements.RemoveAll(item => item.Id == requirementId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlannerWorkstationType>> ListWorkstationTypesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerWorkstationType>>(
            [
                new("type-inspection", "Inspection", null, "{}", true, 1),
                new("type-deburr", "Deburring", null, "{}", true, 1)
            ]);

        public Task<IReadOnlyList<PlannerExternalResource>> ListExternalResourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerExternalResource>>([new("external-chrome", "Chrome plating", "Chromate", 4320, 0, "CALENDAR_TIME", null, "{}", true, 1)]);

        public Task<IReadOnlyList<PlannerSkill>> ListSkillsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerSkill>>([new("skill-cmm", "CMM operator", null, true, 1)]);

    }
}
