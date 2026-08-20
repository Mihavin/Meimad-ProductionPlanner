using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class SetupViewModelTests
{
    [Fact]
    public void Connection_commands_delegate_and_settings_are_exposed_from_setup()
    {
        var connectCalls = 0;
        var saveCalls = 0;
        var refreshCalls = 0;
        var viewModel = CreateViewModel(
            connect: () => { connectCalls++; return Task.CompletedTask; },
            save: () => { saveCalls++; return Task.CompletedTask; },
            refresh: () => { refreshCalls++; return Task.CompletedTask; });
        viewModel.ApplyConnectionSettings("http://planner:5080/", "Miriam");

        viewModel.ConnectCommand.Execute(null);
        viewModel.SaveConnectionCommand.Execute(null);
        viewModel.RefreshConnectionCommand.Execute(null);

        Assert.Equal("http://planner:5080/", viewModel.ServerAddress);
        Assert.Equal("Miriam", viewModel.LocalUserName);
        Assert.Equal(1, connectCalls);
        Assert.Equal(1, saveCalls);
        Assert.Equal(1, refreshCalls);
    }

    [Fact]
    public async Task Editor_can_create_update_select_and_delete_working_calendar()
    {
        var api = new FakeApiClient();
        var viewModel = CreateViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(7));
        await viewModel.EnsureLoadedAsync();

        await viewModel.BeginNewCalendarAsync();
        viewModel.CalendarName = "Setup shift";
        viewModel.CalendarTimeZoneId = "Asia/Jerusalem";
        viewModel.CalendarShiftStartsAt = "07:00";
        viewModel.CalendarShiftEndsAt = "16:00";
        viewModel.CalendarWindowsText = "07:00-12:00\n12:30-16:00";
        viewModel.CalendarBreakWindowsText = "10:00-10:15\n14:00-14:15";
        viewModel.CalendarExceptionsText = "2026-09-13 | closed | Holiday\n2026-09-14 | 08:00-15:00 | 12:00-12:30 | Short day";
        viewModel.CalendarUsageQaWorker = false;
        viewModel.CalendarUseIsraeliHolidays = true;
        await viewModel.SaveCalendarAsync();

        Assert.Equal("Setup shift", api.LastCalendarCreate?.Name);
        Assert.Equal(5, api.LastCalendarCreate?.Workdays.Count);
        Assert.Equal(2, api.LastCalendarCreate?.Windows?.Count);
        Assert.Equal(2, api.LastCalendarCreate?.BreakWindows?.Count);
        Assert.Equal(2, api.LastCalendarCreate?.Exceptions?.Count);
        Assert.Equal(["machine", "setup_worker", "regular_worker"], api.LastCalendarCreate?.Usages);
        Assert.True(api.LastCalendarCreate?.UseIsraeliHolidays);
        var created = Assert.Single(viewModel.WorkingCalendars, value => value.Name == "Setup shift");
        viewModel.SelectedSetupCalendar = created;
        await viewModel.SetSetupCalendarAsync();
        Assert.Equal(created.WorkingCalendarId, api.SetupCalendarId);

        viewModel.SelectedCalendar = created;
        viewModel.CalendarName = "Setup shift revised";
        await viewModel.SaveCalendarAsync();
        Assert.Equal("Setup shift revised", api.LastCalendarUpdate?.Name);
        Assert.Equal($"\"working-calendar:{created.WorkingCalendarId}:v1\"", api.LastCalendarEntityTag);

        viewModel.SelectedSetupCalendar = viewModel.WorkingCalendars.Single(value =>
            value.WorkingCalendarId == created.WorkingCalendarId);
        await viewModel.ClearSetupCalendarAsync();
        Assert.Null(api.SetupCalendarId);
        viewModel.SelectedCalendar = viewModel.WorkingCalendars.Single(value =>
            value.WorkingCalendarId == created.WorkingCalendarId);
        await viewModel.DeleteSelectedCalendarAsync();

        Assert.DoesNotContain(viewModel.WorkingCalendars, value =>
            value.WorkingCalendarId == created.WorkingCalendarId);
    }

    [Fact]
    public async Task Editor_can_create_edit_deactivate_and_delete_machine_with_reusable_type()
    {
        var api = new FakeApiClient();
        var viewModel = CreateViewModel();
        var configurationChanges = 0;
        viewModel.ConfigurationChanged += (_, _) => configurationChanges++;
        viewModel.AttachSession(api, "windows-1", EditorStatus(11));
        await viewModel.EnsureLoadedAsync();
        await viewModel.BeginNewMachineAsync();
        viewModel.MachineNumber = "M07";
        viewModel.MachineName = "Haas VF-5";
        viewModel.SelectedMachineTypeForMachine = viewModel.MachineTypes.Single();
        viewModel.SelectedMachineCalendar = viewModel.WorkingCalendars.Single();
        viewModel.MachineAxisType = "5-axis";
        viewModel.MachineCapabilitiesText = "probe, high-speed";
        viewModel.MachineExecutionMode = "CNC_GCODE";
        viewModel.MachineUsableToolPositions = "30";
        viewModel.MachineRapidRateMillimetersPerMinute = "24000";
        viewModel.MachineToolChangeTimeSeconds = "4.5";
        viewModel.MachineTimeFactor = "1.15";
        Assert.Single(viewModel.MachinePostprocessors).IsSelected = true;

        await viewModel.SaveMachineAsync();

        Assert.NotNull(api.LastMachineCreate);
        Assert.Equal("type-mill", api.LastMachineCreate!.MachineTypeId);
        Assert.Equal("5-axis milling", api.LastMachineCreate.ProcessType);
        Assert.Equal("CNC_GCODE", api.LastMachineCreate.ExecutionMode);
        Assert.Equal(["post-default"], api.LastMachineCreate.SupportedPostprocessorIds);
        Assert.Equal(30, api.LastMachineCreate.UsableToolPositions);
        Assert.Equal(24000, api.LastMachineCreate.RapidRateMillimetersPerMinute);
        Assert.Equal(4.5, api.LastMachineCreate.ToolChangeTimeSeconds);
        Assert.Equal(1.15, api.LastMachineCreate.MachineTimeFactor);
        var machine = Assert.Single(viewModel.Machines);
        Assert.Equal("M07 — Haas VF-5", machine.DisplayName);
        viewModel.SelectedMachine = machine;
        viewModel.MachineName = "Haas VF-5 updated";
        await viewModel.SaveMachineAsync();

        Assert.Equal("Haas VF-5 updated", api.LastMachineUpdate?.Name);
        Assert.Equal($"\"machine:{machine.MachineId}:v1\"", api.LastMachineEntityTag);
        await viewModel.DeactivateSelectedMachineAsync();

        Assert.False(api.LastMachineUpdate!.IsActive);
        Assert.False(viewModel.Machines.Single().IsActive);
        await viewModel.DeleteSelectedMachineAsync();

        Assert.Empty(viewModel.Machines);
        Assert.Equal(4, configurationChanges);
    }

    [Fact]
    public async Task Editor_can_plan_maintenance_report_breakdown_and_restore_machine()
    {
        var api = new FakeApiClient();
        var viewModel = CreateViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(11));
        await viewModel.EnsureLoadedAsync();
        await viewModel.BeginNewMachineAsync();
        viewModel.MachineNumber = "M08";
        viewModel.MachineName = "Mill Eight";
        viewModel.SelectedMachineTypeForMachine = viewModel.MachineTypes.Single();
        viewModel.SelectedMachineCalendar = viewModel.WorkingCalendars.Single();
        await viewModel.SaveMachineAsync();

        await viewModel.BeginNewPlannedMaintenanceAsync();
        viewModel.DowntimeStartsAt = "2026-08-18 09:00";
        viewModel.DowntimeEndsAt = "2026-08-18 11:00";
        viewModel.DowntimeReason = "Spindle service";
        viewModel.DowntimeActor = "Maintenance lead";
        await viewModel.SaveDowntimeAsync();

        Assert.Equal("planned_maintenance", api.LastDowntimeCreate?.DowntimeType);
        Assert.NotNull(api.LastDowntimeCreate?.EndsAt);

        await viewModel.BeginNewBreakdownAsync();
        viewModel.DowntimeStartsAt = "2026-08-18 12:00";
        viewModel.DowntimeReason = "Hydraulic leak";
        viewModel.DowntimeActor = "Operator A";
        await viewModel.SaveDowntimeAsync();
        var breakdown = Assert.Single(viewModel.Downtimes, value => value.DowntimeType == "breakdown");
        viewModel.SelectedDowntime = breakdown;
        viewModel.DowntimeRestoredAt = "2026-08-18 13:00";
        viewModel.DowntimeRepairNote = "Hose replaced";
        await viewModel.RestoreSelectedBreakdownAsync();

        Assert.Equal("Operator A", api.LastDowntimeCreate?.ReportedBy);
        Assert.Equal("Hose replaced", api.LastBreakdownRestore?.RepairNote);
        Assert.Equal("restored", viewModel.SelectedDowntime?.Status);
    }

    [Fact]
    public async Task Editor_can_create_prefill_edit_and_reload_employee_details()
    {
        var api = new FakeApiClient();
        var viewModel = CreateViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(11));
        await viewModel.EnsureLoadedAsync();
        await viewModel.BeginNewMachineAsync();
        viewModel.MachineNumber = "M41";
        viewModel.MachineName = "Employee skill mill";
        viewModel.SelectedMachineTypeForMachine = viewModel.MachineTypes.Single();
        viewModel.SelectedMachineCalendar = viewModel.WorkingCalendars.Single();
        await viewModel.SaveMachineAsync();
        await viewModel.BeginNewResourceAsync();
        viewModel.ResourceEmployeeNumber = "E-41";
        viewModel.ResourceFirstName = "Noa";
        viewModel.ResourceLastName = "Levi";
        viewModel.ResourceRole = "qa_worker";
        viewModel.SelectedResourceCalendar = viewModel.ResourceCalendars.Single();
        var machineSkill = Assert.Single(viewModel.ResourceMachineSkills);
        Assert.Contains("M41", machineSkill.DisplayName);
        machineSkill.IsSelected = true;
        viewModel.ResourcePhotoPath = "C:\\photos\\noa.jpg";
        viewModel.ResourceNotes = "Night shift backup";
        viewModel.ResourceIsActive = false;

        await viewModel.SaveResourceAsync();

        Assert.NotNull(api.LastResourceCreate);
        Assert.Equal("qa_worker", api.LastResourceCreate!.Role);
        Assert.Equal("calendar-day", api.LastResourceCreate.AssignedCalendarId);
        Assert.Equal(["machine-1"], api.LastResourceCreate.Skills);
        var resource = Assert.Single(viewModel.Resources);
        Assert.False(resource.IsActive);
        Assert.Equal("Noa", resource.FirstName);
        Assert.Equal("Night shift backup", resource.Notes);

        viewModel.SelectedResource = resource;
        Assert.Equal("Edit employee / resource", viewModel.ResourceFormHeading);
        Assert.Equal("Noa", viewModel.ResourceFirstName);
        Assert.Equal("qa_worker", viewModel.ResourceRole);
        Assert.Equal("calendar-day", viewModel.SelectedResourceCalendar?.WorkingCalendarId);
        Assert.True(Assert.Single(viewModel.ResourceMachineSkills).IsSelected);

        viewModel.ResourceFirstName = "Changed only in the form";
        Assert.True(viewModel.EditSelectedResourceCommand.CanExecute(null));
        await viewModel.EditSelectedResourceAsync();
        Assert.Equal("Noa", viewModel.ResourceFirstName);

        viewModel.ResourceLastName = "Katz";
        viewModel.ResourceEmail = "noa.katz@example.test";
        viewModel.ResourceIsActive = true;
        await viewModel.SaveResourceAsync();

        Assert.NotNull(api.LastResourceUpdate);
        Assert.Equal("Katz", api.LastResourceUpdate!.LastName);
        Assert.Equal("noa.katz@example.test", api.LastResourceUpdate.Email);
        Assert.Equal($"\"resource:{resource.ResourceId}:v1\"", api.LastResourceEntityTag);
        Assert.Equal("Noa Katz", viewModel.SelectedResource!.Name);
        Assert.True(viewModel.SelectedResource.IsActive);
        Assert.Equal(2, viewModel.SelectedResource.Version);
    }

    [Fact]
    public async Task Editor_can_create_partial_and_full_day_employee_exceptions()
    {
        var api = new FakeApiClient();
        var viewModel = CreateViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(12));
        await viewModel.EnsureLoadedAsync();
        await viewModel.BeginNewResourceAsync();
        viewModel.ResourceEmployeeNumber = "E-42";
        viewModel.ResourceFirstName = "Dan";
        viewModel.ResourceLastName = "Cohen";
        viewModel.SelectedResourceCalendar = viewModel.ResourceCalendars.Single();
        await viewModel.SaveResourceAsync();

        await viewModel.BeginNewResourceExceptionAsync();
        viewModel.ResourceExceptionDate = "2026-08-18";
        viewModel.ResourceExceptionType = "unavailable";
        viewModel.ResourceExceptionIsFullDay = false;
        viewModel.ResourceExceptionStartsAt = "10:00";
        viewModel.ResourceExceptionEndsAt = "12:00";
        viewModel.ResourceExceptionNote = "Appointment";
        await viewModel.SaveResourceExceptionAsync();

        Assert.NotNull(api.LastResourceExceptionCreate);
        Assert.False(api.LastResourceExceptionCreate!.IsFullDay);
        Assert.Equal("10:00", api.LastResourceExceptionCreate.StartsAtLocal);
        Assert.Single(viewModel.ResourceExceptions);

        await viewModel.BeginNewResourceExceptionAsync();
        viewModel.ResourceExceptionDate = "2026-08-19";
        viewModel.ResourceExceptionType = "vacation";
        viewModel.ResourceExceptionIsFullDay = true;
        await viewModel.SaveResourceExceptionAsync();

        Assert.True(api.LastResourceExceptionCreate!.IsFullDay);
        Assert.Null(api.LastResourceExceptionCreate.StartsAtLocal);
        Assert.Equal(2, viewModel.ResourceExceptions.Count);
    }

    [Fact]
    public async Task Editor_can_create_update_and_delete_machine_type()
    {
        var api = new FakeApiClient();
        var viewModel = CreateViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(13));
        await viewModel.EnsureLoadedAsync();
        await viewModel.BeginNewMachineTypeAsync();
        viewModel.MachineTypeName = "Turning";
        viewModel.MachineTypeCapabilitiesText = "lathe, automated, lathe";

        await viewModel.SaveMachineTypeAsync();

        Assert.Equal(["lathe", "automated"], api.LastMachineTypeCreate?.Capabilities);
        var created = viewModel.MachineTypes.Single(value => value.Name == "Turning");
        viewModel.SelectedMachineType = created;
        viewModel.MachineTypeName = "Automated turning";
        await viewModel.SaveMachineTypeAsync();

        Assert.Equal("Automated turning", api.LastMachineTypeUpdate?.Name);
        Assert.Equal($"\"machine-type:{created.MachineTypeId}:v1\"", api.LastMachineTypeEntityTag);
        viewModel.SelectedMachineType = viewModel.MachineTypes.Single(value =>
            value.MachineTypeId == created.MachineTypeId);
        await viewModel.DeleteSelectedMachineTypeAsync();

        Assert.DoesNotContain(viewModel.MachineTypes, value => value.MachineTypeId == created.MachineTypeId);
    }

    [Fact]
    public async Task Editor_can_create_update_and_delete_postprocessor()
    {
        var api = new FakeApiClient();
        var viewModel = CreateViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(13));
        await viewModel.EnsureLoadedAsync();
        await viewModel.BeginNewPostprocessorAsync();
        viewModel.PostprocessorName = "Haas UMC";
        viewModel.PostprocessorDescription = "Released UMC programs";

        await viewModel.SavePostprocessorAsync();

        Assert.Equal("Haas UMC", api.LastPostprocessorCreate?.Name);
        var created = viewModel.Postprocessors.Single(value => value.Name == "Haas UMC");
        viewModel.SelectedPostprocessor = created;
        viewModel.PostprocessorDescription = "Updated description";
        await viewModel.SavePostprocessorAsync();

        Assert.Equal("Updated description", api.LastPostprocessorUpdate?.Description);
        Assert.Equal($"\"postprocessor:{created.PostprocessorId}:v1\"", api.LastPostprocessorEntityTag);
        viewModel.SelectedPostprocessor = viewModel.Postprocessors.Single(value =>
            value.PostprocessorId == created.PostprocessorId);
        await viewModel.DeleteSelectedPostprocessorAsync();

        Assert.DoesNotContain(viewModel.Postprocessors, value => value.PostprocessorId == created.PostprocessorId);
    }

    [Fact]
    public async Task Viewer_can_refresh_but_all_setup_mutations_are_disabled()
    {
        var api = new FakeApiClient();
        var viewModel = CreateViewModel();
        viewModel.AttachSession(api, "windows-1", EditorStatus(4) with
        {
            State = ClientEditState.Viewer,
            Holder = null
        });
        await viewModel.EnsureLoadedAsync();

        Assert.True(viewModel.RefreshMasterDataCommand.CanExecute(null));
        Assert.False(viewModel.NewCalendarCommand.CanExecute(null));
        Assert.False(viewModel.SaveCalendarCommand.CanExecute(null));
        Assert.False(viewModel.DeleteCalendarCommand.CanExecute(null));
        Assert.False(viewModel.SetSetupCalendarCommand.CanExecute(null));
        Assert.False(viewModel.NewMachineCommand.CanExecute(null));
        Assert.False(viewModel.SaveMachineCommand.CanExecute(null));
        Assert.False(viewModel.DeactivateMachineCommand.CanExecute(null));
        Assert.False(viewModel.DeleteMachineCommand.CanExecute(null));
        Assert.False(viewModel.NewPlannedMaintenanceCommand.CanExecute(null));
        Assert.False(viewModel.ReportBreakdownCommand.CanExecute(null));
        Assert.False(viewModel.SaveDowntimeCommand.CanExecute(null));
        Assert.False(viewModel.RestoreBreakdownCommand.CanExecute(null));
        Assert.False(viewModel.NewMachineTypeCommand.CanExecute(null));
        Assert.False(viewModel.SaveMachineTypeCommand.CanExecute(null));
        Assert.False(viewModel.DeleteMachineTypeCommand.CanExecute(null));
        Assert.False(viewModel.NewPostprocessorCommand.CanExecute(null));
        Assert.False(viewModel.SavePostprocessorCommand.CanExecute(null));
        Assert.False(viewModel.DeletePostprocessorCommand.CanExecute(null));
        Assert.False(viewModel.NewResourceCommand.CanExecute(null));
        Assert.False(viewModel.EditSelectedResourceCommand.CanExecute(null));
        Assert.False(viewModel.SaveResourceCommand.CanExecute(null));
        Assert.False(viewModel.DeleteResourceCommand.CanExecute(null));
    }

    private static SetupViewModel CreateViewModel(
        Func<Task>? connect = null,
        Func<Task>? save = null,
        Func<Task>? refresh = null) => new(
        connect ?? (() => Task.CompletedTask),
        save ?? (() => Task.CompletedTask),
        refresh ?? (() => Task.CompletedTask),
        () => true,
        () => true);

    private static EditModeStatus EditorStatus(long generation) => new(
        ClientEditState.Editor,
        generation,
        new EditModeHolder("windows-1", "planner", generation, DateTimeOffset.UtcNow),
        null,
        DateTimeOffset.UtcNow,
        30);

    private sealed class FakeApiClient : IPlannerApiClient
    {
        private readonly List<WorkingCalendar> calendars =
        [
            Calendar("calendar-day", "Day shift")
        ];
        private readonly List<PlannerMachine> machines = [];
        private readonly List<MachineDowntime> downtimes = [];
        private readonly List<PlannerMachineType> machineTypes =
        [
            MachineType("type-mill", "5-axis milling", ["mill", "5-axis"])
        ];
        private readonly List<PlannerPostprocessor> postprocessors =
        [
            Postprocessor("post-default", "Default CNC")
        ];
        private readonly List<PlannerResource> resources = [];
        private readonly List<EmployeeCalendarException> resourceExceptions = [];

        internal WorkingCalendarCreate? LastCalendarCreate { get; private set; }
        internal WorkingCalendarUpdate? LastCalendarUpdate { get; private set; }
        internal string? LastCalendarEntityTag { get; private set; }
        internal string? SetupCalendarId { get; private set; }
        internal MachineCreate? LastMachineCreate { get; private set; }
        internal MachineCreate? LastMachineUpdate { get; private set; }
        internal string? LastMachineEntityTag { get; private set; }
        internal MachineDowntimeCreate? LastDowntimeCreate { get; private set; }
        internal BreakdownRestore? LastBreakdownRestore { get; private set; }
        internal MachineTypeCreate? LastMachineTypeCreate { get; private set; }
        internal MachineTypeUpdate? LastMachineTypeUpdate { get; private set; }
        internal string? LastMachineTypeEntityTag { get; private set; }
        internal PostprocessorCreate? LastPostprocessorCreate { get; private set; }
        internal PostprocessorUpdate? LastPostprocessorUpdate { get; private set; }
        internal string? LastPostprocessorEntityTag { get; private set; }
        internal ResourceCreate? LastResourceCreate { get; private set; }
        internal ResourceUpdate? LastResourceUpdate { get; private set; }
        internal string? LastResourceEntityTag { get; private set; }
        internal EmployeeCalendarExceptionCreate? LastResourceExceptionCreate { get; private set; }

        public Task<IReadOnlyList<WorkingCalendar>> ListWorkingCalendarsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkingCalendar>>(calendars.ToArray());

        public Task<WorkingCalendar> CreateWorkingCalendarAsync(
            WorkingCalendarCreate create,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastCalendarCreate = create;
            var value = new WorkingCalendar(
                $"calendar-{calendars.Count + 1}", create.Name, create.TimeZoneId,
                create.Workdays, create.ShiftStartsAtLocal, create.ShiftEndsAtLocal,
                "weekly", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                create.Windows, create.BreakWindows, create.Exceptions, create.Usages, create.UseIsraeliHolidays);
            calendars.Add(value);
            return Task.FromResult(value);
        }

        public Task<WorkingCalendarResource> UpdateWorkingCalendarAsync(
            string workingCalendarId,
            WorkingCalendarUpdate update,
            string entityTag,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastCalendarUpdate = update;
            LastCalendarEntityTag = entityTag;
            var index = calendars.FindIndex(value => value.WorkingCalendarId == workingCalendarId);
            var value = calendars[index] with
            {
                Name = update.Name,
                TimeZoneId = update.TimeZoneId,
                Workdays = update.Workdays,
                ShiftStartsAtLocal = update.ShiftStartsAtLocal,
                ShiftEndsAtLocal = update.ShiftEndsAtLocal,
                Windows = update.Windows,
                BreakWindows = update.BreakWindows,
                Exceptions = update.Exceptions,
                Usages = update.Usages,
                UseIsraeliHolidays = update.UseIsraeliHolidays,
                Version = calendars[index].Version + 1
            };
            calendars[index] = value;
            return Task.FromResult(new WorkingCalendarResource(
                value, $"\"working-calendar:{workingCalendarId}:v{value.Version}\""));
        }

        public Task DeleteWorkingCalendarAsync(
            string workingCalendarId,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            calendars.RemoveAll(value => value.WorkingCalendarId == workingCalendarId);
            return Task.CompletedTask;
        }

        public Task<SetupCalendarSelection> GetSetupCalendarAsync(
            CancellationToken cancellationToken = default)
        {
            var calendar = calendars.FirstOrDefault(value => value.WorkingCalendarId == SetupCalendarId);
            return Task.FromResult(new SetupCalendarSelection(SetupCalendarId, calendar));
        }

        public Task<SetupCalendarSelection> SetSetupCalendarAsync(
            string workingCalendarId,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            SetupCalendarId = workingCalendarId;
            return GetSetupCalendarAsync(cancellationToken);
        }

        public Task ClearSetupCalendarAsync(
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            SetupCalendarId = null;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlannerMachine>> ListMachinesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerMachine>>(machines.ToArray());

        public Task<PlannerMachine> CreateMachineAsync(
            MachineCreate create,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastMachineCreate = create;
            var value = ToMachine("machine-1", create, 1);
            machines.Add(value);
            return Task.FromResult(value);
        }

        public Task<MachineResource> UpdateMachineAsync(
            string machineId,
            MachineCreate update,
            string entityTag,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastMachineUpdate = update;
            LastMachineEntityTag = entityTag;
            var index = machines.FindIndex(value => value.MachineId == machineId);
            var value = ToMachine(machineId, update, machines[index].Version + 1);
            machines[index] = value;
            return Task.FromResult(new MachineResource(
                value, $"\"machine:{machineId}:v{value.Version}\""));
        }

        public Task DeleteMachineAsync(
            string machineId,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            machines.RemoveAll(value => value.MachineId == machineId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MachineDowntime>> ListDowntimesAsync(string? machineId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MachineDowntime>>(downtimes.Where(value => machineId is null || value.MachineId == machineId).ToArray());

        public Task<MachineDowntime> CreateDowntimeAsync(MachineDowntimeCreate create, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            LastDowntimeCreate = create;
            var now = DateTimeOffset.UtcNow;
            var value = new MachineDowntime($"down-{downtimes.Count + 1}", create.MachineId,
                create.DowntimeType, create.StartsAt, create.EndsAt, create.Reason,
                create.PlannedBy, null, create.ReportedBy,
                create.DowntimeType == "breakdown" ? "active" : "planned", 1, now, now);
            downtimes.Add(value);
            return Task.FromResult(value);
        }

        public Task<MachineDowntimeResource> UpdatePlannedMaintenanceAsync(string downtimeId, PlannedMaintenanceUpdate update, string entityTag, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            var index = downtimes.FindIndex(value => value.DowntimeId == downtimeId);
            var value = downtimes[index] with { MachineId = update.MachineId, StartsAt = update.StartsAt,
                EndsAt = update.EndsAt, Reason = update.Reason, PlannedBy = update.PlannedBy,
                Version = downtimes[index].Version + 1 };
            downtimes[index] = value;
            return Task.FromResult(new MachineDowntimeResource(value, $"\"downtime:{downtimeId}:v{value.Version}\""));
        }

        public Task<MachineDowntimeResource> RestoreBreakdownAsync(string downtimeId, BreakdownRestore restore, string entityTag, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            LastBreakdownRestore = restore;
            var index = downtimes.FindIndex(value => value.DowntimeId == downtimeId);
            var value = downtimes[index] with { EndsAt = restore.RestoredAt, RepairNote = restore.RepairNote,
                Status = "restored", Version = downtimes[index].Version + 1 };
            downtimes[index] = value;
            return Task.FromResult(new MachineDowntimeResource(value, $"\"downtime:{downtimeId}:v{value.Version}\""));
        }

        public Task<IReadOnlyList<PlannerMachineType>> ListMachineTypesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerMachineType>>(machineTypes.ToArray());

        public Task<PlannerMachineType> CreateMachineTypeAsync(
            MachineTypeCreate create,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastMachineTypeCreate = create;
            var value = MachineType($"type-{machineTypes.Count + 1}", create.Name, create.Capabilities);
            machineTypes.Add(value);
            return Task.FromResult(value);
        }

        public Task<MachineTypeResource> UpdateMachineTypeAsync(
            string machineTypeId,
            MachineTypeUpdate update,
            string entityTag,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastMachineTypeUpdate = update;
            LastMachineTypeEntityTag = entityTag;
            var index = machineTypes.FindIndex(value => value.MachineTypeId == machineTypeId);
            var value = machineTypes[index] with
            {
                Name = update.Name,
                Capabilities = update.Capabilities,
                Version = machineTypes[index].Version + 1
            };
            machineTypes[index] = value;
            return Task.FromResult(new MachineTypeResource(
                value, $"\"machine-type:{machineTypeId}:v{value.Version}\""));
        }

        public Task DeleteMachineTypeAsync(
            string machineTypeId,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            machineTypes.RemoveAll(value => value.MachineTypeId == machineTypeId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlannerPostprocessor>> ListPostprocessorsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerPostprocessor>>(postprocessors.ToArray());

        public Task<PlannerPostprocessor> CreatePostprocessorAsync(
            PostprocessorCreate create,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastPostprocessorCreate = create;
            var now = DateTimeOffset.UtcNow;
            var value = new PlannerPostprocessor(
                $"post-{postprocessors.Count + 1}", create.Name, create.Description,
                create.IsActive, 1, now, now);
            postprocessors.Add(value);
            return Task.FromResult(value);
        }

        public Task<PostprocessorResource> UpdatePostprocessorAsync(
            string postprocessorId,
            PostprocessorUpdate update,
            string entityTag,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            LastPostprocessorUpdate = update;
            LastPostprocessorEntityTag = entityTag;
            var index = postprocessors.FindIndex(value => value.PostprocessorId == postprocessorId);
            var value = postprocessors[index] with
            {
                Name = update.Name,
                Description = update.Description,
                IsActive = update.IsActive,
                Version = postprocessors[index].Version + 1
            };
            postprocessors[index] = value;
            return Task.FromResult(new PostprocessorResource(
                value, $"\"postprocessor:{postprocessorId}:v{value.Version}\""));
        }

        public Task DeletePostprocessorAsync(
            string postprocessorId,
            string clientId,
            long editGeneration,
            CancellationToken cancellationToken = default)
        {
            postprocessors.RemoveAll(value => value.PostprocessorId == postprocessorId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlannerResource>> ListResourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlannerResource>>(resources.ToArray());

        public Task<PlannerResource> CreateResourceAsync(ResourceCreate create, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            LastResourceCreate = create;
            var value = new PlannerResource($"resource-{resources.Count + 1}", create.EmployeeNumber,
                $"{create.FirstName} {create.LastName}", create.FirstName, create.LastName, create.Role,
                create.Skills, create.AssignedCalendarId, create.PhotoPath, create.Notes, create.Email,
                create.IsActive, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            resources.Add(value);
            return Task.FromResult(value);
        }

        public Task<ResourceResource> UpdateResourceAsync(string resourceId, ResourceUpdate update, string entityTag, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            LastResourceUpdate = update;
            LastResourceEntityTag = entityTag;
            var index = resources.FindIndex(value => value.ResourceId == resourceId);
            var value = resources[index] with { EmployeeNumber = update.EmployeeNumber, Name = $"{update.FirstName} {update.LastName}", FirstName = update.FirstName, LastName = update.LastName, Role = update.Role, Skills = update.Skills, AssignedCalendarId = update.AssignedCalendarId, PhotoPath = update.PhotoPath, Notes = update.Notes, Email = update.Email, IsActive = update.IsActive, Version = resources[index].Version + 1 };
            resources[index] = value;
            return Task.FromResult(new ResourceResource(value, $"\"resource:{resourceId}:v{value.Version}\""));
        }

        public Task DeleteResourceAsync(string resourceId, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            resources.RemoveAll(value => value.ResourceId == resourceId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EmployeeCalendarException>> ListEmployeeExceptionsAsync(string resourceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EmployeeCalendarException>>(resourceExceptions.Where(value => value.ResourceId == resourceId).ToArray());

        public Task<EmployeeCalendarException> CreateEmployeeExceptionAsync(string resourceId, EmployeeCalendarExceptionCreate create, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            LastResourceExceptionCreate = create;
            var value = new EmployeeCalendarException($"exception-{resourceExceptions.Count + 1}", resourceId,
                create.Date, create.ExceptionType, create.IsFullDay, create.StartsAtLocal, create.EndsAtLocal,
                create.Note, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            resourceExceptions.Add(value);
            return Task.FromResult(value);
        }

        public Task<EmployeeCalendarExceptionResource> UpdateEmployeeExceptionAsync(string resourceId, string exceptionId, EmployeeCalendarExceptionUpdate update, string entityTag, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            var index = resourceExceptions.FindIndex(value => value.ExceptionId == exceptionId && value.ResourceId == resourceId);
            var value = resourceExceptions[index] with { Date = update.Date, ExceptionType = update.ExceptionType,
                IsFullDay = update.IsFullDay, StartsAtLocal = update.StartsAtLocal, EndsAtLocal = update.EndsAtLocal,
                Note = update.Note, Version = resourceExceptions[index].Version + 1 };
            resourceExceptions[index] = value;
            return Task.FromResult(new EmployeeCalendarExceptionResource(value, $"\"employee-exception:{exceptionId}:v{value.Version}\""));
        }

        public Task DeleteEmployeeExceptionAsync(string resourceId, string exceptionId, string clientId, long editGeneration, CancellationToken cancellationToken = default)
        {
            resourceExceptions.RemoveAll(value => value.ResourceId == resourceId && value.ExceptionId == exceptionId);
            return Task.CompletedTask;
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

        public void Dispose()
        {
        }

        private static WorkingCalendar Calendar(string id, string name) => new(
            id, name, "Asia/Jerusalem",
            ["sunday", "monday", "tuesday", "wednesday", "thursday"],
            "06:00", "18:00", "weekly", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        private static PlannerMachineType MachineType(
            string id,
            string name,
            IReadOnlyList<string> capabilities) => new(
            id, name, capabilities, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        private static PlannerPostprocessor Postprocessor(string id, string name) => new(
            id, name, null, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        private static PlannerMachine ToMachine(string id, MachineCreate value, int version) => new(
            id, value.Number, value.Name, value.ProcessType, value.AxisType,
            value.Capabilities, value.WorkingCalendarId, value.IsActive,
            value.DisplayEnabled, value.PicturePath, null, 0, version,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, value.MachineTypeId,
            value.RespectMasterCalendar, value.ExecutionMode,
            value.SupportedPostprocessorIds, value.UsableToolPositions,
            value.RapidRateMillimetersPerMinute, value.ToolChangeTimeSeconds,
            value.MachineTimeFactor);
    }
}
