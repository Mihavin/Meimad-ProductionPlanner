using System.Net;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class MachinePlanningBoardViewModelTests
{
    [Fact]
    public async Task Loads_pool_machine_columns_and_explicit_conflict_unavailability()
    {
        var api = new FakeApiClient(BoardBefore());
        var viewModel = new MachinePlanningBoardViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(5));

        await viewModel.EnsureLoadedAsync();

        Assert.Single(viewModel.Pool);
        Assert.Single(viewModel.Machines);
        Assert.Empty(viewModel.Machines[0].Backlog);
        Assert.Contains("Unavailable", viewModel.ConflictCalculationStatus, StringComparison.Ordinal);
        Assert.True(viewModel.CanDrag);
    }

    [Fact]
    public async Task Manual_assignment_sends_exact_target_and_refreshes_server_order()
    {
        var api = new FakeApiClient(BoardBefore())
        {
            SnapshotAfterAssignment = BoardAfterAssignment()
        };
        var viewModel = new MachinePlanningBoardViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(9));
        await viewModel.EnsureLoadedAsync();
        var operation = viewModel.Pool.Single();
        var machine = viewModel.Machines.Single();

        await viewModel.AssignOrMoveAsync(operation, machine, 0);

        Assert.Equal("operation-1", api.AssignedOperationId);
        Assert.Equal("machine-1", api.TargetMachineId);
        Assert.Equal(0, api.TargetPosition);
        Assert.Equal("windows-1", api.ClientId);
        Assert.Equal(9, api.Generation);
        Assert.Empty(viewModel.Pool);
        Assert.Equal("operation-1", viewModel.Machines.Single().Backlog.Single().BatchOperationId);
        Assert.Equal("Manual assignment accepted", viewModel.Feedback[0].Title);
    }

    [Fact]
    public async Task Incompatible_drop_shows_blocking_feedback_and_keeps_board_unchanged()
    {
        var api = new FakeApiClient(BoardBefore())
        {
            RejectAsIncompatible = true
        };
        var viewModel = new MachinePlanningBoardViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(3));
        await viewModel.EnsureLoadedAsync();

        await viewModel.AssignOrMoveAsync(viewModel.Pool.Single(), viewModel.Machines.Single(), 0);

        Assert.Single(viewModel.Pool);
        Assert.Empty(viewModel.Machines.Single().Backlog);
        Assert.Equal("blocking", viewModel.Feedback[0].Severity);
        Assert.Equal("Incompatible Machine", viewModel.Feedback[0].Title);
        Assert.Contains("kept the board unchanged", viewModel.Feedback[0].Message, StringComparison.Ordinal);
        Assert.Equal(1, api.BoardReadCount);
    }

    [Fact]
    public async Task Viewer_drop_is_rejected_locally_without_assignment_command()
    {
        var api = new FakeApiClient(BoardBefore());
        var viewModel = new MachinePlanningBoardViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(3) with
        {
            State = ClientEditState.Viewer
        });
        await viewModel.EnsureLoadedAsync();

        await viewModel.AssignOrMoveAsync(viewModel.Pool.Single(), viewModel.Machines.Single(), 0);

        Assert.Null(api.AssignedOperationId);
        Assert.Equal("Edit Mode required", viewModel.Feedback[0].Title);
        Assert.False(viewModel.CanDrag);
    }

    [Fact]
    public async Task Same_machine_drop_position_is_translated_to_final_stable_index()
    {
        var first = Operation("machine-1", 0);
        var second = first with
        {
            BatchOperationId = "operation-2",
            OperationNumber = 20,
            OperationName = "Mill second side",
            BacklogPosition = 1
        };
        var snapshot = BoardBefore() with
        {
            Pool = [],
            Machines = [Machine([first, second])]
        };
        var api = new FakeApiClient(snapshot);
        var viewModel = new MachinePlanningBoardViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(6));
        await viewModel.EnsureLoadedAsync();

        await viewModel.AssignOrMoveAsync(
            viewModel.Machines[0].Backlog[0],
            viewModel.Machines[0],
            2);

        Assert.Equal(1, api.TargetPosition);
    }

    [Fact]
    public async Task Editor_creates_machine_with_complete_master_and_picture_path()
    {
        var api = new FakeApiClient(BoardBefore());
        var viewModel = new MachinePlanningBoardViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(16));
        await viewModel.EnsureLoadedAsync();
        await viewModel.BeginAddMachineAsync();
        viewModel.MachineNumber = "M-22";
        viewModel.MachineName = "Five Axis 22";
        viewModel.MachineProcessType = "mill";
        viewModel.MachineAxisType = "5-axis";
        viewModel.MachineCapabilitiesText = "probe, high-speed";
        viewModel.SelectedWorkingCalendar = viewModel.WorkingCalendars.Single();
        viewModel.SetMachinePictureSelection(@"C:\MachinePictures\M-22.png");

        await viewModel.SaveMachineAsync();

        Assert.NotNull(api.CreatedMachine);
        Assert.Equal("M-22", api.CreatedMachine!.Number);
        Assert.Equal(["probe", "high-speed"], api.CreatedMachine.Capabilities);
        Assert.Equal("calendar-day", api.CreatedMachine.WorkingCalendarId);
        Assert.Equal(@"C:\MachinePictures\M-22.png", api.CreatedMachine.PicturePath);
        Assert.Equal("windows-1", api.ClientId);
        Assert.Equal(16, api.Generation);
        Assert.False(viewModel.IsAddingMachine);
        Assert.Equal(2, api.BoardReadCount);
    }

    [Fact]
    public async Task Editor_creates_calendar_from_fixed_options_and_selects_it_for_machine()
    {
        var api = new FakeApiClient(BoardBefore());
        var viewModel = new MachinePlanningBoardViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(17));
        await viewModel.EnsureLoadedAsync();
        await viewModel.BeginAddCalendarAsync();
        viewModel.CalendarName = "Extended shift";
        viewModel.CalendarTimeZoneId = "Asia/Jerusalem";
        viewModel.SelectedCalendarWorkweek = viewModel.CalendarWorkweeks[0];
        viewModel.SelectedCalendarShift = viewModel.CalendarShifts[1];

        await viewModel.SaveCalendarAsync();

        Assert.NotNull(api.CreatedCalendar);
        Assert.Equal("06:00", api.CreatedCalendar!.ShiftStartsAtLocal);
        Assert.Equal("22:00", api.CreatedCalendar.ShiftEndsAtLocal);
        Assert.Equal(5, api.CreatedCalendar.Workdays.Count);
        Assert.Equal("calendar-created", viewModel.SelectedWorkingCalendar?.WorkingCalendarId);
        Assert.False(viewModel.IsAddingCalendar);
    }

    [Fact]
    public async Task Editor_loads_and_updates_machine_through_api()
    {
        var api = new FakeApiClient(BoardBefore());
        var viewModel = new MachinePlanningBoardViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(24));
        await viewModel.EnsureLoadedAsync();

        await viewModel.BeginEditMachineAsync(viewModel.Machines.Single());
        viewModel.MachineName = "Updated Mill";
        await viewModel.SaveMachineAsync();

        Assert.Equal("machine-1", api.UpdatedMachineId);
        Assert.Equal("Updated Mill", api.UpdatedMachine?.Name);
        Assert.Equal("\"machine:machine-1:v1\"", api.UpdatedEntityTag);
        Assert.False(viewModel.IsAddingMachine);
    }

    [Fact]
    public async Task Editor_deletes_machine_and_refreshes_board()
    {
        var api = new FakeApiClient(BoardBefore());
        var viewModel = new MachinePlanningBoardViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(25));
        await viewModel.EnsureLoadedAsync();

        await viewModel.DeleteMachineAsync(viewModel.Machines.Single());

        Assert.Equal("machine-1", api.DeletedMachineId);
        Assert.Equal(2, api.BoardReadCount);
    }

    [Fact]
    public async Task Editor_starts_assigned_operation_through_server_and_refreshes_status()
    {
        var queued = Operation("machine-1", 0);
        var before = BoardBefore() with { Pool = [], Machines = [Machine([queued])] };
        var after = before with
        {
            Machines = [Machine([queued with { Status = "in_progress" }])]
        };
        var api = new FakeApiClient(before) { SnapshotAfterExecution = after };
        var viewModel = new MachinePlanningBoardViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(22));
        await viewModel.EnsureLoadedAsync();

        await viewModel.ChangeExecutionStatusAsync(
            viewModel.Machines.Single().Backlog.Single(), "start");

        Assert.Equal("operation-1", api.ExecutionOperationId);
        Assert.Equal("start", api.ExecutionAction);
        Assert.Equal("windows-1", api.ClientId);
        Assert.Equal(22, api.Generation);
        Assert.Equal("in_progress", viewModel.Machines.Single().Backlog.Single().Status);
        Assert.True(viewModel.Machines.Single().Backlog.Single().CanSuspend);
        Assert.True(viewModel.Machines.Single().Backlog.Single().CanFinish);
        Assert.Equal(2, api.BoardReadCount);
    }

    private static PlanningBoardSnapshot BoardBefore() => new(
        DateTimeOffset.Parse("2026-08-11T10:00:00Z"),
        "unavailable",
        "The pure time engine is not connected to the planning-board projection yet.",
        [],
        [Operation(null, null)],
        [Machine([])]);

    private static PlanningBoardSnapshot BoardAfterAssignment() => new(
        DateTimeOffset.Parse("2026-08-11T10:01:00Z"),
        "unavailable",
        "The pure time engine is not connected to the planning-board projection yet.",
        [],
        [],
        [Machine([Operation("machine-1", 0)])]);

    private static PlanningBoardOperation Operation(string? machineId, int? position) => new(
        "operation-1",
        "batch-1",
        "B-1",
        "case-1",
        "PN-1",
        10,
        "Mill first side",
        "mill",
        300,
        60,
        "not_started",
        machineId,
        position);

    private static PlanningBoardMachine Machine(IReadOnlyList<PlanningBoardOperation> backlog) => new(
        "machine-1",
        "M-1",
        "Mill One",
        "mill",
        "3-axis",
        ["probe"],
        true,
        backlog);

    private static EditModeStatus EditorStatus(long generation) => new(
        ClientEditState.Editor,
        generation,
        new EditModeHolder("windows-1", "planner", generation, DateTimeOffset.UtcNow),
        null,
        DateTimeOffset.UtcNow,
        30);

    private sealed class FakeApiClient : IPlannerApiClient
    {
        private PlanningBoardSnapshot snapshot;

        internal FakeApiClient(PlanningBoardSnapshot snapshot)
        {
            this.snapshot = snapshot;
        }

        internal PlanningBoardSnapshot? SnapshotAfterAssignment { get; init; }
        internal bool RejectAsIncompatible { get; init; }
        internal string? AssignedOperationId { get; private set; }
        internal string? TargetMachineId { get; private set; }
        internal int TargetPosition { get; private set; }
        internal string? ClientId { get; private set; }
        internal long Generation { get; private set; }
        internal int BoardReadCount { get; private set; }
        internal MachineCreate? CreatedMachine { get; private set; }
        internal WorkingCalendarCreate? CreatedCalendar { get; private set; }
        internal PlanningBoardSnapshot? SnapshotAfterExecution { get; init; }
        internal string? ExecutionOperationId { get; private set; }
        internal string? ExecutionAction { get; private set; }
        internal MachineCreate? UpdatedMachine { get; private set; }
        internal string? UpdatedMachineId { get; private set; }
        internal string? UpdatedEntityTag { get; private set; }
        internal string? DeletedMachineId { get; private set; }

        public Task<IReadOnlyList<WorkingCalendar>> ListWorkingCalendarsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkingCalendar>>([Calendar("calendar-day", "Day shift")]);

        public Task<WorkingCalendar> CreateWorkingCalendarAsync(
            WorkingCalendarCreate create,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            CreatedCalendar = create;
            ClientId = clientId;
            Generation = editGeneration;
            return Task.FromResult(Calendar("calendar-created", create.Name) with
            {
                TimeZoneId = create.TimeZoneId,
                Workdays = create.Workdays,
                ShiftStartsAtLocal = create.ShiftStartsAtLocal,
                ShiftEndsAtLocal = create.ShiftEndsAtLocal
            });
        }

        public Task<PlanningBoardSnapshot> GetPlanningBoardAsync(
            CancellationToken cancellationToken = default)
        {
            BoardReadCount++;
            return Task.FromResult(snapshot);
        }

        public Task<TimelineSnapshot> GetTimelineAsync(
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TimelineSnapshot(
                DateTimeOffset.UtcNow, from, to, [], [], [], []));

        public Task AssignOrMoveOperationAsync(
            string batchOperationId,
            string machineId,
            int backlogPosition,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            AssignedOperationId = batchOperationId;
            TargetMachineId = machineId;
            TargetPosition = backlogPosition;
            ClientId = clientId;
            Generation = editGeneration;
            if (RejectAsIncompatible)
            {
                throw new PlannerApiException(
                    HttpStatusCode.UnprocessableEntity,
                    "incompatible_machine",
                    "The operation is not compatible with this Machine.");
            }

            if (SnapshotAfterAssignment is not null)
            {
                snapshot = SnapshotAfterAssignment;
            }

            return Task.CompletedTask;
        }

        public Task UnassignOperationAsync(
            string batchOperationId,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<BatchOperationExecution> ChangeOperationExecutionAsync(
            string batchOperationId,
            string action,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            ExecutionOperationId = batchOperationId;
            ExecutionAction = action;
            ClientId = clientId;
            Generation = editGeneration;
            if (SnapshotAfterExecution is not null)
            {
                snapshot = SnapshotAfterExecution;
            }

            var status = action switch
            {
                "start" => "in_progress",
                "suspend" => "suspended",
                "finish" => "completed",
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult(new BatchOperationExecution(
                batchOperationId, "machine-1", status, 2));
        }

        public Task<PlannerMachine> CreateMachineAsync(
            MachineCreate create,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            CreatedMachine = create;
            ClientId = clientId;
            Generation = editGeneration;
            return Task.FromResult(new PlannerMachine(
                "machine-2",
                create.Number,
                create.Name,
                create.ProcessType,
                create.AxisType,
                create.Capabilities,
                create.WorkingCalendarId,
                create.IsActive,
                create.DisplayEnabled,
                create.PicturePath,
                null,
                0,
                1,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }

        public Task<MachineResource> GetMachineAsync(
            string machineId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MachineResource(
                new PlannerMachine(
                    machineId, "M-1", "Mill One", "mill", "3-axis", ["probe"],
                    "calendar-day", true, true, null, null, 0, 1,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                "\"machine:machine-1:v1\""));

        public Task<MachineResource> UpdateMachineAsync(
            string machineId,
            MachineCreate update,
            string entityTag,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            UpdatedMachineId = machineId;
            UpdatedMachine = update;
            UpdatedEntityTag = entityTag;
            ClientId = clientId;
            Generation = editGeneration;
            return Task.FromResult(new MachineResource(
                new PlannerMachine(
                    machineId, update.Number, update.Name, update.ProcessType, update.AxisType,
                    update.Capabilities, update.WorkingCalendarId, update.IsActive,
                    update.DisplayEnabled, update.PicturePath, null, 0, 2,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                "\"machine:machine-1:v2\""));
        }

        public Task DeleteMachineAsync(
            string machineId,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            DeletedMachineId = machineId;
            ClientId = clientId;
            Generation = editGeneration;
            return Task.CompletedTask;
        }

        public Task<byte[]?> GetMachinePictureAsync(
            string machineId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

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

        public Task<IReadOnlyList<PlannerCase>> ListCasesAsync(
            CaseQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CaseResource> GetCaseAsync(
            string caseId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CaseResource> UpdateCaseAsync(
            string caseId,
            CaseUpdate update,
            string entityTag,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CaseOperation>> ListCaseOperationsAsync(
            string caseId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PlannerOrder>> ListOrdersAsync(
            string caseId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProductionBatch>> ListBatchesAsync(
            string caseId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<byte[]?> GetCasePreviewAsync(
            string caseId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose()
        {
        }

        private static WorkingCalendar Calendar(string id, string name) => new(
            id,
            name,
            "Asia/Jerusalem",
            ["sunday", "monday", "tuesday", "wednesday", "thursday"],
            "06:00",
            "18:00",
            "weekly",
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }
}
