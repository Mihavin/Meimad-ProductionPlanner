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
        Assert.Equal(1, api.PreviewReadCount);
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
    public async Task Incompatible_drop_without_confirmed_reason_keeps_board_unchanged()
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
        Assert.Equal("warning", viewModel.Feedback[0].Severity);
        Assert.Equal("Assignment override cancelled", viewModel.Feedback[0].Title);
        Assert.Contains("reason is required", viewModel.Feedback[0].Message, StringComparison.Ordinal);
        Assert.Equal(1, api.BoardReadCount);
    }

    [Fact]
    public async Task Cross_type_drop_prompts_then_resubmits_confirmation_and_reason()
    {
        AssignmentOverridePrompt? shownPrompt = null;
        var crossTypeOperation = Operation(null, null) with { RequiredMachineType = "stale 2-axis" };
        var crossTypeMachine = Machine([]) with { ProcessType = "stale 4-axis" };
        var before = BoardBefore() with
        {
            Pool = [crossTypeOperation],
            Machines = [crossTypeMachine]
        };
        var after = before with
        {
            Pool = [],
            Machines = [crossTypeMachine with
            {
                Backlog = [crossTypeOperation with { MachineId = "machine-1", BacklogPosition = 0 }]
            }]
        };
        var api = new FakeApiClient(before)
        {
            RejectAsIncompatible = true,
            OverrideRequiredMachineType = "3-axis",
            OverrideSelectedMachineType = "5-axis milling",
            SnapshotAfterAssignment = after
        };
        var viewModel = new MachinePlanningBoardViewModel(prompt =>
        {
            shownPrompt = prompt;
            return "Approved because the 3-axis Machine is unavailable.";
        });
        viewModel.AttachSession(api, "windows-1", EditorStatus(4));
        await viewModel.EnsureLoadedAsync();

        await viewModel.AssignOrMoveAsync(
            viewModel.Pool.Single(), viewModel.Machines.Single(), 0);

        Assert.NotNull(shownPrompt);
        Assert.Equal("3-axis", shownPrompt!.RequiredMachineType);
        Assert.Equal("5-axis milling", shownPrompt.SelectedMachineType);
        Assert.True(api.LastCompatibilityOverride?.Confirmed);
        Assert.Equal(
            "Approved because the 3-axis Machine is unavailable.",
            api.LastCompatibilityOverride?.Reason);
        Assert.Empty(viewModel.Pool);
        Assert.Single(viewModel.Machines.Single().Backlog);
        Assert.Equal("Cross-type assignment confirmed", viewModel.Feedback[0].Title);
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
    public async Task Viewer_cannot_change_assignment_planning_mode()
    {
        var assigned = Operation("machine-1", 0);
        var snapshot = BoardBefore() with
        {
            Pool = [],
            Machines = [Machine([assigned])]
        };
        var api = new FakeApiClient(snapshot);
        var viewModel = new MachinePlanningBoardViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(3) with
        {
            State = ClientEditState.Viewer
        });
        await viewModel.EnsureLoadedAsync();

        var operation = viewModel.Machines.Single().Backlog.Single();
        await viewModel.ChangePlanningModeAsync(operation, "backward");

        Assert.Null(api.PlanningModeAssignmentId);
        Assert.False(viewModel.CanUndo);
        Assert.False(viewModel.CanRedo);
        Assert.Equal("Edit Mode required", viewModel.Feedback[0].Title);
    }

    [Fact]
    public async Task Editor_changes_mode_on_same_assignment_refreshes_board_and_invalidates_timeline()
    {
        var assigned = Operation("machine-1", 0);
        var before = BoardBefore() with
        {
            Pool = [],
            Machines = [Machine([assigned])]
        };
        var afterOperation = assigned with { PlanningMode = "backward", AssignmentVersion = 4 };
        var api = new FakeApiClient(before)
        {
            SnapshotAfterPlanningMode = before with
            {
                Machines = [Machine([afterOperation])]
            }
        };
        var viewModel = new MachinePlanningBoardViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(13));
        await viewModel.EnsureLoadedAsync();
        var planChanged = 0;
        viewModel.PlanChanged += (_, _) => planChanged++;
        var original = viewModel.Machines.Single().Backlog.Single();
        Assert.True(original.CanScheduleBackward);
        Assert.True(original.CanScheduleForward);
        Assert.False(original.CanSetManualMode);

        await viewModel.ChangePlanningModeAsync(
            original,
            "backward");

        Assert.Equal("assignment-1", api.PlanningModeAssignmentId);
        Assert.Equal(3, api.PlanningModeAssignmentVersion);
        Assert.Equal("backward", api.PlanningMode);
        Assert.Equal("windows-1", api.ClientId);
        Assert.Equal(13, api.Generation);
        var refreshed = viewModel.Machines.Single().Backlog.Single();
        Assert.Equal("operation-1", refreshed.BatchOperationId);
        Assert.Equal("assignment-1", refreshed.MachineAssignmentId);
        Assert.Equal("backward", refreshed.PlanningMode);
        Assert.Equal("Mode Backward", refreshed.PlanningModeText);
        Assert.False(refreshed.CanScheduleBackward);
        Assert.True(refreshed.CanSetManualMode);
        Assert.Equal(1, planChanged);
        Assert.False(viewModel.CanUndo);
        Assert.False(viewModel.CanRedo);
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
        Assert.False(viewModel.Machines.Single().Backlog.Single().CanReset);
        Assert.Equal(2, api.BoardReadCount);
    }

    [Fact]
    public async Task Editor_resets_paused_operation_through_server_and_refreshes_status()
    {
        var paused = Operation("machine-1", 0) with { Status = "suspended" };
        var before = BoardBefore() with { Pool = [], Machines = [Machine([paused])] };
        var after = before with
        {
            Machines = [Machine([paused with { Status = "not_started" }])]
        };
        var api = new FakeApiClient(before) { SnapshotAfterExecution = after };
        var viewModel = new MachinePlanningBoardViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(23));
        await viewModel.EnsureLoadedAsync();

        var operation = viewModel.Machines.Single().Backlog.Single();
        Assert.True(operation.CanReset);
        await viewModel.ChangeExecutionStatusAsync(operation, "reset");

        Assert.Equal("operation-1", api.ExecutionOperationId);
        Assert.Equal("reset", api.ExecutionAction);
        Assert.Equal("not_started", viewModel.Machines.Single().Backlog.Single().Status);
        Assert.Equal(2, api.BoardReadCount);
    }

    [Fact]
    public void Compact_operation_row_exposes_quantity_orders_time_status_and_valid_actions()
    {
        var operation = new PlanningOperationViewModel(new PlanningBoardOperation(
            "operation-compact",
            "batch-1",
            "B-1",
            "case-1",
            "PN-1",
            10,
            "Mill",
            "mill",
            600,
            120,
            "suspended",
            "machine-1",
            0,
            4,
            ["SO-1", "SO-2"]) with { CaseName = "Widget case" });

        Assert.Equal("Qty 4", operation.PlannedQuantityText);
        Assert.Equal("SO-1, SO-2", operation.OrderReferencesText);
        Assert.Equal(1_080, operation.EstimatedTimeSeconds);
        Assert.Equal("Time 00:18:00", operation.EstimatedTimeText);
        Assert.Equal("Paused", operation.StatusText);
        Assert.False(string.IsNullOrWhiteSpace(operation.StatusGlyph));
        Assert.True(operation.CanStart);
        Assert.False(operation.CanSuspend);
        Assert.False(operation.CanFinish);
        Assert.True(operation.CanReset);
        Assert.Equal("PN-1 / Widget case", operation.PartCaseText);
        Assert.Equal("OP10 Mill", operation.OperationText);
        Assert.Equal("B-1 / SO-1, SO-2", operation.BatchOrderText);
    }

    [Fact]
    public void Compact_operation_row_prefers_server_calculated_time_and_handles_missing_values()
    {
        var serverCalculated = new PlanningOperationViewModel(new PlanningBoardOperation(
            "operation-calculated", "batch-1", "B-1", "case-1", "PN-1", 10, "Mill",
            null, 600, 120, "in_progress", "machine-1", 0,
            4, [], 999));
        var unavailable = new PlanningOperationViewModel(new PlanningBoardOperation(
            "operation-missing", "batch-1", "B-1", "case-1", "PN-1", 20, "Inspect",
            null, null, null, "not_started", null, null));

        Assert.Equal("Time 00:16:39", serverCalculated.EstimatedTimeText);
        Assert.Equal("Stock / no Order", serverCalculated.OrderReferencesText);
        Assert.Equal("In progress", serverCalculated.StatusText);
        Assert.True(serverCalculated.CanSuspend);
        Assert.True(serverCalculated.CanFinish);
        Assert.Equal("Time unavailable", unavailable.EstimatedTimeText);
        Assert.Equal("Not started", unavailable.StatusText);
        Assert.False(unavailable.CanStart);
    }

    [Theory]
    [InlineData("not_started")]
    [InlineData("suspended")]
    public void Start_or_resume_is_enabled_only_for_the_head_of_the_machine_backlog(string status)
    {
        var head = new PlanningOperationViewModel(Operation("machine-1", 0) with { Status = status });
        var later = new PlanningOperationViewModel(Operation("machine-1", 1) with { Status = status });

        Assert.True(head.CanStart);
        Assert.False(later.CanStart);
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

    private static PlanningBoardOperation Operation(string? machineId, int? position)
    {
        var operation = new PlanningBoardOperation(
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
        return machineId is null
            ? operation
            : operation with
            {
                MachineAssignmentId = "assignment-1",
                AssignmentVersion = 3,
                PlanningMode = "manual"
            };
    }

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
        internal string? OverrideRequiredMachineType { get; init; }
        internal string? OverrideSelectedMachineType { get; init; }
        internal MachineAssignmentCompatibilityOverride? LastCompatibilityOverride { get; private set; }
        internal string? AssignedOperationId { get; private set; }
        internal string? TargetMachineId { get; private set; }
        internal int TargetPosition { get; private set; }
        internal string? ClientId { get; private set; }
        internal long Generation { get; private set; }
        internal int BoardReadCount { get; private set; }
        internal int PreviewReadCount { get; private set; }
        internal MachineCreate? CreatedMachine { get; private set; }
        internal WorkingCalendarCreate? CreatedCalendar { get; private set; }
        internal PlanningBoardSnapshot? SnapshotAfterExecution { get; init; }
        internal PlanningBoardSnapshot? SnapshotAfterPlanningMode { get; init; }
        internal string? ExecutionOperationId { get; private set; }
        internal string? ExecutionAction { get; private set; }
        internal MachineCreate? UpdatedMachine { get; private set; }
        internal string? UpdatedMachineId { get; private set; }
        internal string? UpdatedEntityTag { get; private set; }
        internal string? DeletedMachineId { get; private set; }
        internal string? PlanningModeAssignmentId { get; private set; }
        internal int PlanningModeAssignmentVersion { get; private set; }
        internal string? PlanningMode { get; private set; }

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
                    HttpStatusCode.Conflict,
                    "machine_type_override_required",
                    "Confirm the cross-type assignment and provide a reason.",
                    OverrideRequiredMachineType,
                    OverrideSelectedMachineType);
            }

            if (SnapshotAfterAssignment is not null)
            {
                snapshot = SnapshotAfterAssignment;
            }

            return Task.CompletedTask;
        }

        public Task AssignOrMoveOperationAsync(
            string batchOperationId,
            string machineId,
            int backlogPosition,
            string clientId,
            long editGeneration,
            MachineAssignmentCompatibilityOverride compatibilityOverride,
            CancellationToken cancellationToken = default)
        {
            AssignedOperationId = batchOperationId;
            TargetMachineId = machineId;
            TargetPosition = backlogPosition;
            ClientId = clientId;
            Generation = editGeneration;
            LastCompatibilityOverride = compatibilityOverride;
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

        public Task<MachineAssignment> ChangeMachineAssignmentPlanningModeAsync(
            string machineAssignmentId,
            int assignmentVersion,
            string planningMode,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            PlanningModeAssignmentId = machineAssignmentId;
            PlanningModeAssignmentVersion = assignmentVersion;
            PlanningMode = planningMode;
            ClientId = clientId;
            Generation = editGeneration;
            if (SnapshotAfterPlanningMode is not null)
            {
                snapshot = SnapshotAfterPlanningMode;
            }

            return Task.FromResult(new MachineAssignment(
                machineAssignmentId,
                "operation-1",
                "machine-1",
                0,
                assignmentVersion + 1,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                planningMode));
        }

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
                "reset" => "not_started",
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
            CancellationToken cancellationToken = default)
        {
            PreviewReadCount++;
            return Task.FromResult<byte[]?>(null);
        }

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
