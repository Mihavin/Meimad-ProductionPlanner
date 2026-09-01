using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class SetupViewModel : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<string> DayTokens =
    [
        "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"
    ];

    private readonly Func<Task> connect;
    private readonly Func<Task> saveConnection;
    private readonly Func<Task> refreshConnection;
    private readonly Func<bool> canConnect;
    private readonly Func<bool> canRefreshConnection;
    private IPlannerApiClient? apiClient;
    private string clientId = string.Empty;
    private long editGeneration;
    private bool isEditor;
    private bool isBusy;
    private bool hasLoaded;
    private string serverAddress = string.Empty;
    private string localUserName = string.Empty;
    private string connectionHeadline = "Not connected";
    private string connectionDetail = "Save a Server address and connect.";
    private string statusMessage = "Connect to manage factory setup.";
    private WorkingCalendar? selectedCalendar;
    private WorkingCalendar? selectedSetupCalendar;
    private WorkingCalendar? selectedMasterCalendar;
    private string? editingCalendarId;
    private string calendarName = string.Empty;
    private string calendarTimeZoneId = "Asia/Jerusalem";
    private string calendarShiftStartsAt = "06:00";
    private string calendarShiftEndsAt = "18:00";
    private string calendarWindowsText = "06:00-18:00";
    private string calendarBreakWindowsText = string.Empty;
    private string calendarExceptionsText = string.Empty;
    private bool calendarUsageMachine = true;
    private bool calendarUsageSetupWorker = true;
    private bool calendarUsageRegularWorker = true;
    private bool calendarUsageQaWorker = true;
    private bool calendarUseIsraeliHolidays;
    private bool worksSunday = true;
    private bool worksMonday = true;
    private bool worksTuesday = true;
    private bool worksWednesday = true;
    private bool worksThursday = true;
    private bool worksFriday;
    private bool worksSaturday;
    private PlannerMachine? selectedMachine;
    private string? editingMachineId;
    private string machineNumber = string.Empty;
    private string machineName = string.Empty;
    private string machineProcessType = string.Empty;
    private string machineAxisType = string.Empty;
    private string machineCapabilitiesText = string.Empty;
    private WorkingCalendar? selectedMachineCalendar;
    private PlannerMachineType? selectedMachineTypeForMachine;
    private string machinePicturePath = string.Empty;
    private bool machineIsActive = true;
    private bool machineDisplayEnabled = true;
    private bool machineRespectMasterCalendar = true;
    private string machineExecutionMode = "MANUAL";
    private string machineUsableToolPositions = string.Empty;
    private string machineRapidRateMillimetersPerMinute = string.Empty;
    private string machineToolChangeTimeSeconds = string.Empty;
    private string machineTimeFactor = "1";
    private string haasHost = string.Empty;
    private string haasMacAddress = string.Empty;
    private CncAdapterDefinition? selectedCncAdapter;
    private string haasMdcPort = "5051";
    private string haasMtConnectPort = "8082";
    private string haasDprntPort = "8080";
    private string haasTelemetryProvider = "MTCONNECT";
    private bool haasLocalNetShareEnabled;
    private string haasLocalNetSharePath = string.Empty;
    private string haasCredentialsReference = string.Empty;
    private string haasPartCounterSource = "Q500";
    private string haasPollingIntervalMs = "2000";
    private string haasConnectionTimeoutMs = "3000";
    private bool haasEnabled;
    private int haasSettingsVersion;
    private string haasDiagnostics = "Load Haas configuration to view connection status.";
    private string haasTimeline = "No Haas Bench events loaded.";
    private string verificationDprintPort = "8080";
    private string verificationChallengeProgram = "9001";
    private string verificationVerifyProgram = "9002";
    private string verificationCustomGcodeAlias = string.Empty;
    private string verificationNonceVariable = "10501";
    private string verificationResponseVariable = "10500";
    private string verificationStateVariable = "10502";
    private string verificationReleaseTokenVariable = "10503";
    private string verificationFinalizeProgram = "9003";
    private string verificationEventSequenceVariable = "10504";
    private string verificationMacroVersion = "10";
    private string verificationCodeDigits = "6";
    private string verificationTimeoutSeconds = "300";
    private bool verificationEnabled;
    private int verificationSettingsVersion;
    private string verificationRecoveryRunId = string.Empty;
    private string verificationRecoveryNcReleaseId = string.Empty;
    private string verificationRecoveryToolTableReleaseId = string.Empty;
    private string verificationRecoveryReason = string.Empty;
    private MachineDowntime? selectedDowntime;
    private string? editingDowntimeId;
    private PlannerMachine? selectedDowntimeMachine;
    private string downtimeType = "planned_maintenance";
    private string downtimeStartsAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private string downtimeEndsAt = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private string downtimeReason = string.Empty;
    private string downtimeActor = string.Empty;
    private string downtimeRepairNote = string.Empty;
    private string downtimeRestoredAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private PlannerMachineType? selectedMachineType;
    private string? editingMachineTypeId;
    private string machineTypeName = string.Empty;
    private string machineTypeCapabilitiesText = string.Empty;
    private PlannerPostprocessor? selectedPostprocessor;
    private string? editingPostprocessorId;
    private string postprocessorName = string.Empty;
    private string postprocessorDescription = string.Empty;
    private bool postprocessorIsActive = true;
    private PlannerResource? selectedResource;
    private string? editingResourceId;
    private string resourceEmployeeNumber = string.Empty;
    private string resourceFirstName = string.Empty;
    private string resourceLastName = string.Empty;
    private string resourceRole = "regular_worker";
    private WorkingCalendar? selectedResourceCalendar;
    private string resourcePhotoPath = string.Empty;
    private string resourceNotes = string.Empty;
    private string resourceEmail = string.Empty;
    private bool resourceIsActive = true;
    private bool resourceRespectMasterCalendar = true;
    private string resourceToolLoadSecondsPerTool = "60";
    private string resourceFixtureAssemblySeconds = string.Empty;
    private string resourceFirstPartRunningSpeedPercent = "66.6667";
    private EmployeeCalendarException? selectedResourceException;
    private string? editingResourceExceptionId;
    private string resourceExceptionDate = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
    private string resourceExceptionType = "unavailable";
    private bool resourceExceptionIsFullDay = true;
    private string resourceExceptionStartsAt = "09:00";
    private string resourceExceptionEndsAt = "12:00";
    private string resourceExceptionNote = string.Empty;
    private IsraeliHoliday? selectedIsraeliHoliday;
    private string? editingIsraeliHolidayId;
    private string israeliHolidayDate = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
    private string israeliHolidayName = string.Empty;
    private string israeliHolidayStatus = "non_working";
    private string israeliHolidayStartsAt = "08:00";
    private string israeliHolidayEndsAt = "13:00";
    private string holidaySyncFromYear = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
    private string holidaySyncToYear = (DateTime.Today.Year + 1).ToString(CultureInfo.InvariantCulture);
    private string holidaySyncStatus = "Cached holidays are available offline. Refresh when internet access is available.";
    private string reportSenderAddress = string.Empty;
    private string reportRecipientsText = string.Empty;
    private string reportSmtpHost = string.Empty;
    private string reportSmtpPort = string.Empty;
    private bool reportUseSsl = true;
    private bool dailyReportEnabled;
    private string dailyReportTimeLocal = "07:00";
    private string reportTimeZoneId = "Asia/Jerusalem";
    private bool weeklyMaterialReportEnabled;
    private string weeklyMaterialReportSendDay = "thursday";
    private string weeklyMaterialReportTimeLocal = "08:00";
    private bool weeklyEmployeeEfficiencyEnabled;
    private string weeklyEmployeeEfficiencySendDay = "sunday";
    private string weeklyEmployeeEfficiencyTimeLocal = "08:00";
    private string? reportEmailSettingsEntityTag;

    internal SetupViewModel(
        Func<Task> connect,
        Func<Task> saveConnection,
        Func<Task> refreshConnection,
        Func<bool> canConnect,
        Func<bool> canRefreshConnection)
    {
        this.connect = connect;
        this.saveConnection = saveConnection;
        this.refreshConnection = refreshConnection;
        this.canConnect = canConnect;
        this.canRefreshConnection = canRefreshConnection;

        ConnectCommand = new AsyncCommand(connect, canConnect);
        SaveConnectionCommand = new AsyncCommand(saveConnection, canConnect);
        RefreshConnectionCommand = new AsyncCommand(refreshConnection, canRefreshConnection);
        RefreshMasterDataCommand = new AsyncCommand(RefreshAsync, CanRead);

        NewCalendarCommand = new AsyncCommand(BeginNewCalendarAsync, CanManage);
        SaveCalendarCommand = new AsyncCommand(SaveCalendarAsync, CanSaveCalendar);
        DeleteCalendarCommand = new AsyncCommand(DeleteSelectedCalendarAsync, CanDeleteCalendar);
        SetSetupCalendarCommand = new AsyncCommand(SetSetupCalendarAsync, CanSetSetupCalendar);
        ClearSetupCalendarCommand = new AsyncCommand(ClearSetupCalendarAsync, CanClearSetupCalendar);
        SetMasterCalendarCommand = new AsyncCommand(SetMasterCalendarAsync, CanSetMasterCalendar);
        ClearMasterCalendarCommand = new AsyncCommand(ClearMasterCalendarAsync, CanClearMasterCalendar);

        NewMachineCommand = new AsyncCommand(BeginNewMachineAsync, CanManage);
        SaveMachineCommand = new AsyncCommand(SaveMachineAsync, CanSaveMachine);
        DeactivateMachineCommand = new AsyncCommand(DeactivateSelectedMachineAsync, CanDeactivateMachine);
        DeleteMachineCommand = new AsyncCommand(DeleteSelectedMachineAsync, CanDeleteMachine);
        LoadHaasConfigurationCommand = new AsyncCommand(LoadHaasConfigurationAsync, CanReadHaas);
        SaveHaasConfigurationCommand = new AsyncCommand(SaveHaasConfigurationAsync, CanManageHaas);
        TestHaasConnectionCommand = new AsyncCommand(TestHaasConnectionAsync, CanReadHaas);
        TestHaasMtConnectCommand = new AsyncCommand(TestHaasMtConnectAsync, CanReadHaas);
        TestHaasMdcCommand = new AsyncCommand(TestHaasMdcAsync, CanReadHaas);
        TestHaasNetShareCommand = new AsyncCommand(TestHaasNetShareAsync, CanReadHaas);
        RefreshHaasMonitorCommand = new AsyncCommand(RefreshHaasMonitorAsync, CanReadHaas);
        ReconnectCncCommand = new AsyncCommand(ReconnectCncAsync, CanManageHaas);
        LoadVerificationConfigurationCommand = new AsyncCommand(LoadVerificationConfigurationAsync, CanReadHaas);
        SaveVerificationConfigurationCommand = new AsyncCommand(SaveVerificationConfigurationAsync, CanManageHaas);
        GenerateOffsetLoaderReleaseCommand = new AsyncCommand(GenerateOffsetLoaderReleaseAsync, CanManageHaas);
        InvalidateVerificationCommand = new AsyncCommand(InvalidateVerificationAsync, CanManageHaas);
        RevokeCurrentOffsetLoaderCommand = new AsyncCommand(RevokeCurrentOffsetLoaderAsync, CanManageHaas);
        NewPlannedMaintenanceCommand = new AsyncCommand(BeginNewPlannedMaintenanceAsync, CanManage);
        ReportBreakdownCommand = new AsyncCommand(BeginNewBreakdownAsync, CanManage);
        SaveDowntimeCommand = new AsyncCommand(SaveDowntimeAsync, CanSaveDowntime);
        RestoreBreakdownCommand = new AsyncCommand(RestoreSelectedBreakdownAsync, CanRestoreBreakdown);

        NewMachineTypeCommand = new AsyncCommand(BeginNewMachineTypeAsync, CanManage);
        SaveMachineTypeCommand = new AsyncCommand(SaveMachineTypeAsync, CanSaveMachineType);
        DeleteMachineTypeCommand = new AsyncCommand(DeleteSelectedMachineTypeAsync, CanDeleteMachineType);

        NewPostprocessorCommand = new AsyncCommand(BeginNewPostprocessorAsync, CanManage);
        SavePostprocessorCommand = new AsyncCommand(SavePostprocessorAsync, CanSavePostprocessor);
        DeletePostprocessorCommand = new AsyncCommand(DeleteSelectedPostprocessorAsync, CanDeletePostprocessor);

        NewResourceCommand = new AsyncCommand(BeginNewResourceAsync, CanManage);
        EditSelectedResourceCommand = new AsyncCommand(EditSelectedResourceAsync, CanEditSelectedResource);
        SaveResourceCommand = new AsyncCommand(SaveResourceAsync, CanSaveResource);
        DeleteResourceCommand = new AsyncCommand(DeleteSelectedResourceAsync, CanDeleteResource);
        RefreshResourceExceptionsCommand = new AsyncCommand(RefreshResourceExceptionsAsync, CanReadResourceExceptions);
        NewResourceExceptionCommand = new AsyncCommand(BeginNewResourceExceptionAsync, CanManageResourceExceptions);
        SaveResourceExceptionCommand = new AsyncCommand(SaveResourceExceptionAsync, CanManageResourceExceptions);
        DeleteResourceExceptionCommand = new AsyncCommand(DeleteSelectedResourceExceptionAsync, CanDeleteResourceException);
        NewIsraeliHolidayCommand = new AsyncCommand(BeginNewIsraeliHolidayAsync, CanManage);
        SaveIsraeliHolidayCommand = new AsyncCommand(SaveIsraeliHolidayAsync, CanSaveIsraeliHoliday);
        DeleteIsraeliHolidayCommand = new AsyncCommand(DeleteSelectedIsraeliHolidayAsync, CanDeleteIsraeliHoliday);
        SynchronizeIsraeliHolidaysCommand = new AsyncCommand(SynchronizeIsraeliHolidaysAsync, CanManage);
        SaveReportEmailSettingsCommand = new AsyncCommand(SaveReportEmailSettingsAsync, CanSaveReportEmailSettings);
        SendWeeklyMaterialReportNowCommand = new AsyncCommand(SendWeeklyMaterialReportNowAsync, CanSaveReportEmailSettings);
        SendWeeklyEmployeeEfficiencyReportNowCommand = new AsyncCommand(SendWeeklyEmployeeEfficiencyReportNowAsync, CanSaveReportEmailSettings);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? ConfigurationChanged;

    public LegacyExcelImportViewModel LegacyImport { get; } = new();

    public ServerMaintenanceViewModel ServerMaintenance { get; } = new();

    public ResourceMasterDataViewModel ResourceMasterData { get; } = new();

    public ObservableCollection<WorkingCalendar> WorkingCalendars { get; } = [];

    public IReadOnlyList<WorkingCalendar> MachineWorkingCalendars => WorkingCalendars
        .Where(value => value.Usages is null || value.Usages.Contains("machine", StringComparer.OrdinalIgnoreCase))
        .ToArray();

    public IReadOnlyList<WorkingCalendar> SetupWorkerCalendars => WorkingCalendars
        .Where(value => value.Usages is null || value.Usages.Contains("setup_worker", StringComparer.OrdinalIgnoreCase))
        .ToArray();

    public IReadOnlyList<WorkingCalendar> RegularWorkerCalendars => WorkingCalendars
        .Where(value => value.Usages is null || value.Usages.Contains("regular_worker", StringComparer.OrdinalIgnoreCase))
        .ToArray();

    public IReadOnlyList<WorkingCalendar> QaWorkerCalendars => WorkingCalendars
        .Where(value => value.Usages is null || value.Usages.Contains("qa_worker", StringComparer.OrdinalIgnoreCase))
        .ToArray();

    public ObservableCollection<PlannerMachine> Machines { get; } = [];
    public ObservableCollection<MachineDowntime> Downtimes { get; } = [];

    public ObservableCollection<PlannerMachineType> MachineTypes { get; } = [];
    public ObservableCollection<PlannerPostprocessor> Postprocessors { get; } = [];
    public ObservableCollection<MachinePostprocessorOption> MachinePostprocessors { get; } = [];

    public ObservableCollection<PlannerResource> Resources { get; } = [];
    public ObservableCollection<ResourceMachineSkillOption> ResourceMachineSkills { get; } = [];
    public ObservableCollection<EmployeeCalendarException> ResourceExceptions { get; } = [];

    public ObservableCollection<IsraeliHoliday> IsraeliHolidays { get; } = [];

    public IReadOnlyList<string> CalendarTimeZones { get; } = ["Asia/Jerusalem", "UTC"];
    public IReadOnlyList<string> MachineExecutionModes { get; } = ["MANUAL", "CNC_GCODE"];

    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand SaveConnectionCommand { get; }
    public AsyncCommand RefreshConnectionCommand { get; }
    public AsyncCommand RefreshMasterDataCommand { get; }
    public AsyncCommand NewCalendarCommand { get; }
    public AsyncCommand SaveCalendarCommand { get; }
    public AsyncCommand DeleteCalendarCommand { get; }
    public AsyncCommand SetSetupCalendarCommand { get; }
    public AsyncCommand ClearSetupCalendarCommand { get; }
    public AsyncCommand SetMasterCalendarCommand { get; }
    public AsyncCommand ClearMasterCalendarCommand { get; }
    public AsyncCommand NewMachineCommand { get; }
    public AsyncCommand SaveMachineCommand { get; }
    public AsyncCommand DeactivateMachineCommand { get; }
    public AsyncCommand DeleteMachineCommand { get; }
    public AsyncCommand LoadHaasConfigurationCommand { get; }
    public AsyncCommand SaveHaasConfigurationCommand { get; }
    public AsyncCommand TestHaasConnectionCommand { get; }
    public AsyncCommand TestHaasMtConnectCommand { get; }
    public AsyncCommand TestHaasMdcCommand { get; }
    public AsyncCommand TestHaasNetShareCommand { get; }
    public AsyncCommand RefreshHaasMonitorCommand { get; }
    public AsyncCommand ReconnectCncCommand { get; }
    public AsyncCommand LoadVerificationConfigurationCommand { get; }
    public AsyncCommand SaveVerificationConfigurationCommand { get; }
    public AsyncCommand GenerateOffsetLoaderReleaseCommand { get; }
    public AsyncCommand InvalidateVerificationCommand { get; }
    public AsyncCommand RevokeCurrentOffsetLoaderCommand { get; }
    public AsyncCommand NewPlannedMaintenanceCommand { get; }
    public AsyncCommand ReportBreakdownCommand { get; }
    public AsyncCommand SaveDowntimeCommand { get; }
    public AsyncCommand RestoreBreakdownCommand { get; }
    public AsyncCommand NewMachineTypeCommand { get; }
    public AsyncCommand SaveMachineTypeCommand { get; }
    public AsyncCommand DeleteMachineTypeCommand { get; }
    public AsyncCommand NewPostprocessorCommand { get; }
    public AsyncCommand SavePostprocessorCommand { get; }
    public AsyncCommand DeletePostprocessorCommand { get; }
    public AsyncCommand NewResourceCommand { get; }
    public AsyncCommand EditSelectedResourceCommand { get; }
    public AsyncCommand SaveResourceCommand { get; }
    public AsyncCommand DeleteResourceCommand { get; }
    public AsyncCommand RefreshResourceExceptionsCommand { get; }
    public AsyncCommand NewResourceExceptionCommand { get; }
    public AsyncCommand SaveResourceExceptionCommand { get; }
    public AsyncCommand DeleteResourceExceptionCommand { get; }
    public AsyncCommand NewIsraeliHolidayCommand { get; }
    public AsyncCommand SaveIsraeliHolidayCommand { get; }
    public AsyncCommand DeleteIsraeliHolidayCommand { get; }
    public AsyncCommand SynchronizeIsraeliHolidaysCommand { get; }
    public AsyncCommand SaveReportEmailSettingsCommand { get; }
    public AsyncCommand SendWeeklyMaterialReportNowCommand { get; }
    public AsyncCommand SendWeeklyEmployeeEfficiencyReportNowCommand { get; }

    public string ServerAddress
    {
        get => serverAddress;
        set => SetField(ref serverAddress, value);
    }

    public string LocalUserName
    {
        get => localUserName;
        set => SetField(ref localUserName, value);
    }

    public string ConnectionHeadline
    {
        get => connectionHeadline;
        private set => SetField(ref connectionHeadline, value);
    }

    public string ConnectionDetail
    {
        get => connectionDetail;
        private set => SetField(ref connectionDetail, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetField(ref statusMessage, value);
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

    public bool IsEditor => isEditor;

    public string AuthorityText => isEditor
        ? "Edit Mode is held: setup changes are enabled."
        : "View Mode: setup data is read-only.";

    public WorkingCalendar? SelectedCalendar
    {
        get => selectedCalendar;
        set
        {
            if (SetField(ref selectedCalendar, value) && value is not null)
            {
                PopulateCalendarForm(value);
            }

            RaiseCommandStates();
        }
    }

    public WorkingCalendar? SelectedSetupCalendar
    {
        get => selectedSetupCalendar;
        set
        {
            if (SetField(ref selectedSetupCalendar, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public WorkingCalendar? SelectedMasterCalendar
    {
        get => selectedMasterCalendar;
        set { if (SetField(ref selectedMasterCalendar, value)) RaiseCommandStates(); }
    }

    public string CalendarName { get => calendarName; set => SetField(ref calendarName, value); }
    public string CalendarTimeZoneId { get => calendarTimeZoneId; set => SetField(ref calendarTimeZoneId, value); }
    public string CalendarShiftStartsAt { get => calendarShiftStartsAt; set => SetField(ref calendarShiftStartsAt, value); }
    public string CalendarShiftEndsAt { get => calendarShiftEndsAt; set => SetField(ref calendarShiftEndsAt, value); }
    public string CalendarWindowsText { get => calendarWindowsText; set => SetField(ref calendarWindowsText, value); }
    public string CalendarBreakWindowsText { get => calendarBreakWindowsText; set => SetField(ref calendarBreakWindowsText, value); }
    public string CalendarExceptionsText { get => calendarExceptionsText; set => SetField(ref calendarExceptionsText, value); }
    public bool CalendarUsageMachine { get => calendarUsageMachine; set => SetField(ref calendarUsageMachine, value); }
    public bool CalendarUsageSetupWorker { get => calendarUsageSetupWorker; set => SetField(ref calendarUsageSetupWorker, value); }
    public bool CalendarUsageRegularWorker { get => calendarUsageRegularWorker; set => SetField(ref calendarUsageRegularWorker, value); }
    public bool CalendarUsageQaWorker { get => calendarUsageQaWorker; set => SetField(ref calendarUsageQaWorker, value); }
    public bool CalendarUseIsraeliHolidays { get => calendarUseIsraeliHolidays; set => SetField(ref calendarUseIsraeliHolidays, value); }
    public bool WorksSunday { get => worksSunday; set => SetField(ref worksSunday, value); }
    public bool WorksMonday { get => worksMonday; set => SetField(ref worksMonday, value); }
    public bool WorksTuesday { get => worksTuesday; set => SetField(ref worksTuesday, value); }
    public bool WorksWednesday { get => worksWednesday; set => SetField(ref worksWednesday, value); }
    public bool WorksThursday { get => worksThursday; set => SetField(ref worksThursday, value); }
    public bool WorksFriday { get => worksFriday; set => SetField(ref worksFriday, value); }
    public bool WorksSaturday { get => worksSaturday; set => SetField(ref worksSaturday, value); }
    public string CalendarFormHeading => editingCalendarId is null ? "New calendar" : "Edit calendar";
    public bool IsCalendarEditable => editingCalendarId is null || SelectedCalendar?.ScheduleKind == "weekly";

    public PlannerMachine? SelectedMachine
    {
        get => selectedMachine;
        set
        {
            if (SetField(ref selectedMachine, value) && value is not null)
            {
                PopulateMachineForm(value);
            }

            RaiseCommandStates();
        }
    }

    public string MachineNumber { get => machineNumber; set => SetField(ref machineNumber, value); }
    public string MachineName { get => machineName; set => SetField(ref machineName, value); }
    public string MachineProcessType { get => machineProcessType; set => SetField(ref machineProcessType, value); }
    public string MachineAxisType { get => machineAxisType; set => SetField(ref machineAxisType, value); }
    public string MachineCapabilitiesText { get => machineCapabilitiesText; set => SetField(ref machineCapabilitiesText, value); }
    public WorkingCalendar? SelectedMachineCalendar { get => selectedMachineCalendar; set { if (SetField(ref selectedMachineCalendar, value)) RaiseCommandStates(); } }
    public PlannerMachineType? SelectedMachineTypeForMachine
    {
        get => selectedMachineTypeForMachine;
        set
        {
            if (SetField(ref selectedMachineTypeForMachine, value) && value is not null)
            {
                MachineProcessType = value.Name;
            }

            RaiseCommandStates();
        }
    }
    public string MachinePicturePath { get => machinePicturePath; set => SetField(ref machinePicturePath, value); }
    public bool MachineIsActive { get => machineIsActive; set => SetField(ref machineIsActive, value); }
    public bool MachineRespectMasterCalendar { get => machineRespectMasterCalendar; set => SetField(ref machineRespectMasterCalendar, value); }
    public bool MachineDisplayEnabled { get => machineDisplayEnabled; set => SetField(ref machineDisplayEnabled, value); }
    public string MachineExecutionMode { get => machineExecutionMode; set => SetField(ref machineExecutionMode, value); }
    public string MachineUsableToolPositions { get => machineUsableToolPositions; set => SetField(ref machineUsableToolPositions, value); }
    public string MachineRapidRateMillimetersPerMinute { get => machineRapidRateMillimetersPerMinute; set => SetField(ref machineRapidRateMillimetersPerMinute, value); }
    public string MachineToolChangeTimeSeconds { get => machineToolChangeTimeSeconds; set => SetField(ref machineToolChangeTimeSeconds, value); }
    public string MachineTimeFactor { get => machineTimeFactor; set => SetField(ref machineTimeFactor, value); }
    public string MachineFormHeading => editingMachineId is null ? "New machine" : "Edit machine";
    public ObservableCollection<CncAdapterDefinition> CncAdapters { get; } = [];
    public CncAdapterDefinition? SelectedCncAdapter
    {
        get => selectedCncAdapter;
        set
        {
            if (SetField(ref selectedCncAdapter, value))
                OnPropertyChanged(nameof(CncAdapterAvailability));
        }
    }
    public string CncAdapterAvailability => SelectedCncAdapter is null
        ? "Load configuration to read the Server adapter registry."
        : SelectedCncAdapter.Implemented
            ? $"{SelectedCncAdapter.DisplayName} is implemented."
            : $"{SelectedCncAdapter.DisplayName} is registered but unsupported.";
    public string HaasHost { get => haasHost; set => SetField(ref haasHost, value); }
    public string HaasMacAddress { get => haasMacAddress; set => SetField(ref haasMacAddress, value); }
    public string HaasMdcPort { get => haasMdcPort; set => SetField(ref haasMdcPort, value); }
    public string HaasMtConnectPort { get => haasMtConnectPort; set => SetField(ref haasMtConnectPort, value); }
    public string HaasDprntPort { get => haasDprntPort; set => SetField(ref haasDprntPort, value); }
    public string HaasTelemetryProvider { get => haasTelemetryProvider; set => SetField(ref haasTelemetryProvider, value); }
    public IReadOnlyList<string> HaasTelemetryProviders { get; } = ["MTCONNECT", "MDC"];
    public bool HaasLocalNetShareEnabled { get => haasLocalNetShareEnabled; set => SetField(ref haasLocalNetShareEnabled, value); }
    public string HaasLocalNetSharePath { get => haasLocalNetSharePath; set => SetField(ref haasLocalNetSharePath, value); }
    public string HaasCredentialsReference { get => haasCredentialsReference; set => SetField(ref haasCredentialsReference, value); }
    public string HaasPartCounterSource { get => haasPartCounterSource; set => SetField(ref haasPartCounterSource, value); }
    public IReadOnlyList<string> HaasPartCounterSources { get; } = ["Q500", "M30_COUNTER_1", "M30_COUNTER_2"];
    public string HaasPollingIntervalMs { get => haasPollingIntervalMs; set => SetField(ref haasPollingIntervalMs, value); }
    public string HaasConnectionTimeoutMs { get => haasConnectionTimeoutMs; set => SetField(ref haasConnectionTimeoutMs, value); }
    public bool HaasEnabled { get => haasEnabled; set => SetField(ref haasEnabled, value); }
    public string HaasDiagnostics { get => haasDiagnostics; private set => SetField(ref haasDiagnostics, value); }
    public string HaasTimeline { get => haasTimeline; private set => SetField(ref haasTimeline, value); }
    public string VerificationDprintPort { get => verificationDprintPort; set => SetField(ref verificationDprintPort, value); }
    public string VerificationChallengeProgram { get => verificationChallengeProgram; set => SetField(ref verificationChallengeProgram, value); }
    public string VerificationVerifyProgram { get => verificationVerifyProgram; set => SetField(ref verificationVerifyProgram, value); }
    public string VerificationCustomGcodeAlias { get => verificationCustomGcodeAlias; set => SetField(ref verificationCustomGcodeAlias, value); }
    public string VerificationNonceVariable { get => verificationNonceVariable; set => SetField(ref verificationNonceVariable, value); }
    public string VerificationResponseVariable { get => verificationResponseVariable; set => SetField(ref verificationResponseVariable, value); }
    public string VerificationStateVariable { get => verificationStateVariable; set => SetField(ref verificationStateVariable, value); }
    public string VerificationReleaseTokenVariable { get => verificationReleaseTokenVariable; set => SetField(ref verificationReleaseTokenVariable, value); }
    public string VerificationFinalizeProgram { get => verificationFinalizeProgram; set => SetField(ref verificationFinalizeProgram, value); }
    public string VerificationEventSequenceVariable { get => verificationEventSequenceVariable; set => SetField(ref verificationEventSequenceVariable, value); }
    public string VerificationMacroVersion { get => verificationMacroVersion; set => SetField(ref verificationMacroVersion, value); }
    public string VerificationCodeDigits { get => verificationCodeDigits; set => SetField(ref verificationCodeDigits, value); }
    public string VerificationTimeoutSeconds { get => verificationTimeoutSeconds; set => SetField(ref verificationTimeoutSeconds, value); }
    public bool VerificationEnabled { get => verificationEnabled; set => SetField(ref verificationEnabled, value); }
    public string VerificationRecoveryRunId { get => verificationRecoveryRunId; set => SetField(ref verificationRecoveryRunId, value); }
    public string VerificationRecoveryNcReleaseId { get => verificationRecoveryNcReleaseId; set => SetField(ref verificationRecoveryNcReleaseId, value); }
    public string VerificationRecoveryToolTableReleaseId { get => verificationRecoveryToolTableReleaseId; set => SetField(ref verificationRecoveryToolTableReleaseId, value); }
    public string VerificationRecoveryReason { get => verificationRecoveryReason; set => SetField(ref verificationRecoveryReason, value); }
    public MachineDowntime? SelectedDowntime
    {
        get => selectedDowntime;
        set
        {
            if (SetField(ref selectedDowntime, value) && value is not null) PopulateDowntimeForm(value);
            RaiseCommandStates();
        }
    }
    public PlannerMachine? SelectedDowntimeMachine { get => selectedDowntimeMachine; set { if (SetField(ref selectedDowntimeMachine, value)) RaiseCommandStates(); } }
    public string DowntimeType { get => downtimeType; private set { if (SetField(ref downtimeType, value)) { OnPropertyChanged(nameof(IsPlannedMaintenance)); OnPropertyChanged(nameof(IsBreakdown)); OnPropertyChanged(nameof(DowntimeFormHeading)); RaiseCommandStates(); } } }
    public bool IsPlannedMaintenance => DowntimeType == "planned_maintenance";
    public bool IsBreakdown => DowntimeType == "breakdown";
    public string DowntimeStartsAt { get => downtimeStartsAt; set => SetField(ref downtimeStartsAt, value); }
    public string DowntimeEndsAt { get => downtimeEndsAt; set => SetField(ref downtimeEndsAt, value); }
    public string DowntimeReason { get => downtimeReason; set => SetField(ref downtimeReason, value); }
    public string DowntimeActor { get => downtimeActor; set => SetField(ref downtimeActor, value); }
    public string DowntimeRepairNote { get => downtimeRepairNote; set => SetField(ref downtimeRepairNote, value); }
    public string DowntimeRestoredAt { get => downtimeRestoredAt; set => SetField(ref downtimeRestoredAt, value); }
    public string DowntimeFormHeading => editingDowntimeId is null
        ? IsBreakdown ? "Report machine breakdown" : "New planned maintenance"
        : IsBreakdown ? "Breakdown details" : "Edit planned maintenance";

    public PlannerMachineType? SelectedMachineType
    {
        get => selectedMachineType;
        set
        {
            if (SetField(ref selectedMachineType, value) && value is not null)
            {
                PopulateMachineTypeForm(value);
            }

            RaiseCommandStates();
        }
    }

    public string MachineTypeName { get => machineTypeName; set => SetField(ref machineTypeName, value); }
    public string MachineTypeCapabilitiesText { get => machineTypeCapabilitiesText; set => SetField(ref machineTypeCapabilitiesText, value); }
    public string MachineTypeFormHeading => editingMachineTypeId is null ? "New machine type" : "Edit machine type";

    public PlannerPostprocessor? SelectedPostprocessor
    {
        get => selectedPostprocessor;
        set
        {
            if (SetField(ref selectedPostprocessor, value) && value is not null)
            {
                PopulatePostprocessorForm(value);
            }

            RaiseCommandStates();
        }
    }

    public string PostprocessorName { get => postprocessorName; set => SetField(ref postprocessorName, value); }
    public string PostprocessorDescription { get => postprocessorDescription; set => SetField(ref postprocessorDescription, value); }
    public bool PostprocessorIsActive { get => postprocessorIsActive; set => SetField(ref postprocessorIsActive, value); }
    public string PostprocessorFormHeading => editingPostprocessorId is null ? "New postprocessor" : "Edit postprocessor";

    public PlannerResource? SelectedResource
    {
        get => selectedResource;
        set
        {
            if (SetField(ref selectedResource, value))
            {
                editingResourceExceptionId = null;
                OnPropertyChanged(nameof(ResourceExceptionFormHeading));
                ResourceExceptions.Clear();
                SelectedResourceException = null;
                if (value is not null)
                {
                    PopulateResourceForm(value);
                    _ = LoadResourceExceptionsAsync(value.ResourceId);
                }
            }
            RaiseCommandStates();
        }
    }
    public string ResourceEmployeeNumber { get => resourceEmployeeNumber; set => SetField(ref resourceEmployeeNumber, value); }
    public string ResourceFirstName { get => resourceFirstName; set => SetField(ref resourceFirstName, value); }
    public string ResourceLastName { get => resourceLastName; set => SetField(ref resourceLastName, value); }
    public string ResourceRole { get => resourceRole; set { if (SetField(ref resourceRole, value)) { OnPropertyChanged(nameof(ResourceCalendars)); SelectedResourceCalendar = ResourceCalendars.FirstOrDefault(value => value.WorkingCalendarId == SelectedResourceCalendar?.WorkingCalendarId); } } }
    public IReadOnlyList<string> ResourceRoles { get; } = ["setup_worker", "regular_worker", "qa_worker"];
    public IReadOnlyList<WorkingCalendar> ResourceCalendars => ResourceRole switch
    {
        "setup_worker" => SetupWorkerCalendars,
        "qa_worker" => QaWorkerCalendars,
        _ => RegularWorkerCalendars
    };
    public WorkingCalendar? SelectedResourceCalendar { get => selectedResourceCalendar; set => SetField(ref selectedResourceCalendar, value); }
    public string ResourcePhotoPath { get => resourcePhotoPath; set => SetField(ref resourcePhotoPath, value); }
    public string ResourceNotes { get => resourceNotes; set => SetField(ref resourceNotes, value); }
    public string ResourceEmail { get => resourceEmail; set => SetField(ref resourceEmail, value); }
    public bool ResourceIsActive { get => resourceIsActive; set => SetField(ref resourceIsActive, value); }
    public bool ResourceRespectMasterCalendar { get => resourceRespectMasterCalendar; set => SetField(ref resourceRespectMasterCalendar, value); }
    public string ResourceToolLoadSecondsPerTool { get => resourceToolLoadSecondsPerTool; set => SetField(ref resourceToolLoadSecondsPerTool, value); }
    public string ResourceFixtureAssemblySeconds { get => resourceFixtureAssemblySeconds; set => SetField(ref resourceFixtureAssemblySeconds, value); }
    public string ResourceFirstPartRunningSpeedPercent { get => resourceFirstPartRunningSpeedPercent; set => SetField(ref resourceFirstPartRunningSpeedPercent, value); }
    public string ResourceFormHeading => editingResourceId is null ? "New employee / resource" : "Edit employee / resource";
    public string ResourceSaveActionText => editingResourceId is null ? "Save employee / resource" : "Save employee / resource changes";
    public EmployeeCalendarException? SelectedResourceException
    {
        get => selectedResourceException;
        set { if (SetField(ref selectedResourceException, value) && value is not null) PopulateResourceExceptionForm(value); RaiseCommandStates(); }
    }
    public IReadOnlyList<string> ResourceExceptionTypes { get; } = ["vacation", "sick_day", "personal_day", "unavailable", "custom_note"];
    public string ResourceExceptionDate { get => resourceExceptionDate; set => SetField(ref resourceExceptionDate, value); }
    public string ResourceExceptionType { get => resourceExceptionType; set => SetField(ref resourceExceptionType, value); }
    public bool ResourceExceptionIsFullDay { get => resourceExceptionIsFullDay; set { if (SetField(ref resourceExceptionIsFullDay, value)) OnPropertyChanged(nameof(IsResourceExceptionPartialDay)); } }
    public bool IsResourceExceptionPartialDay => !ResourceExceptionIsFullDay;
    public string ResourceExceptionStartsAt { get => resourceExceptionStartsAt; set => SetField(ref resourceExceptionStartsAt, value); }
    public string ResourceExceptionEndsAt { get => resourceExceptionEndsAt; set => SetField(ref resourceExceptionEndsAt, value); }
    public string ResourceExceptionNote { get => resourceExceptionNote; set => SetField(ref resourceExceptionNote, value); }
    public string ResourceExceptionFormHeading => editingResourceExceptionId is null ? "New availability exception" : "Edit availability exception";

    public IsraeliHoliday? SelectedIsraeliHoliday
    {
        get => selectedIsraeliHoliday;
        set { if (SetField(ref selectedIsraeliHoliday, value) && value is not null) PopulateIsraeliHolidayForm(value); RaiseCommandStates(); }
    }
    public string IsraeliHolidayDate { get => israeliHolidayDate; set => SetField(ref israeliHolidayDate, value); }
    public string IsraeliHolidayName { get => israeliHolidayName; set => SetField(ref israeliHolidayName, value); }
    public IReadOnlyList<string> IsraeliHolidayStatuses { get; } = ["non_working", "working", "partial_working"];
    public string IsraeliHolidayStatus { get => israeliHolidayStatus; set { if(SetField(ref israeliHolidayStatus,value))OnPropertyChanged(nameof(IsIsraeliHolidayPartialWorking)); } }
    public bool IsIsraeliHolidayPartialWorking => IsraeliHolidayStatus == "partial_working";
    public string IsraeliHolidayStartsAt { get => israeliHolidayStartsAt; set => SetField(ref israeliHolidayStartsAt,value); }
    public string IsraeliHolidayEndsAt { get => israeliHolidayEndsAt; set => SetField(ref israeliHolidayEndsAt,value); }
    public string HolidaySyncFromYear { get => holidaySyncFromYear; set => SetField(ref holidaySyncFromYear,value); }
    public string HolidaySyncToYear { get => holidaySyncToYear; set => SetField(ref holidaySyncToYear,value); }
    public string HolidaySyncStatus { get => holidaySyncStatus; private set => SetField(ref holidaySyncStatus,value); }
    public string IsraeliHolidayFormHeading => editingIsraeliHolidayId is null ? "New Israeli holiday" : "Edit Israeli holiday";

    public string ReportSenderAddress { get => reportSenderAddress; set => SetField(ref reportSenderAddress, value); }
    public string ReportRecipientsText { get => reportRecipientsText; set => SetField(ref reportRecipientsText, value); }
    public string ReportSmtpHost { get => reportSmtpHost; set => SetField(ref reportSmtpHost, value); }
    public string ReportSmtpPort { get => reportSmtpPort; set => SetField(ref reportSmtpPort, value); }
    public bool ReportUseSsl { get => reportUseSsl; set => SetField(ref reportUseSsl, value); }
    public bool DailyReportEnabled { get => dailyReportEnabled; set => SetField(ref dailyReportEnabled, value); }
    public string DailyReportTimeLocal { get => dailyReportTimeLocal; set => SetField(ref dailyReportTimeLocal, value); }
    public string ReportTimeZoneId { get => reportTimeZoneId; set => SetField(ref reportTimeZoneId, value); }
    public bool WeeklyMaterialReportEnabled { get => weeklyMaterialReportEnabled; set => SetField(ref weeklyMaterialReportEnabled, value); }
    public string WeeklyMaterialReportSendDay { get => weeklyMaterialReportSendDay; set => SetField(ref weeklyMaterialReportSendDay, value); }
    public string WeeklyMaterialReportTimeLocal { get => weeklyMaterialReportTimeLocal; set => SetField(ref weeklyMaterialReportTimeLocal, value); }
    public bool WeeklyEmployeeEfficiencyEnabled { get => weeklyEmployeeEfficiencyEnabled; set => SetField(ref weeklyEmployeeEfficiencyEnabled, value); }
    public string WeeklyEmployeeEfficiencySendDay { get => weeklyEmployeeEfficiencySendDay; set => SetField(ref weeklyEmployeeEfficiencySendDay, value); }
    public string WeeklyEmployeeEfficiencyTimeLocal { get => weeklyEmployeeEfficiencyTimeLocal; set => SetField(ref weeklyEmployeeEfficiencyTimeLocal, value); }
    public IReadOnlyList<string> Weekdays { get; } = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    internal void ApplyConnectionSettings(string address, string userName)
    {
        ServerAddress = address;
        LocalUserName = userName;
        ServerMaintenance.UpdateConnectionContext(address, userName);
    }

    internal void ApplyConnectionStatus(string headline, string detail)
    {
        ConnectionHeadline = headline;
        ConnectionDetail = detail;
    }

    internal void UpdateConnectionCommandStates()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        SaveConnectionCommand.RaiseCanExecuteChanged();
        RefreshConnectionCommand.RaiseCanExecuteChanged();
    }

    internal void AttachSession(
        IPlannerApiClient? newApiClient,
        string newClientId,
        EditModeStatus? editStatus)
    {
        var apiChanged = !ReferenceEquals(apiClient, newApiClient);
        var nextIsEditor = editStatus?.State == ClientEditState.Editor;
        var nextGeneration = editStatus?.Generation ?? 0;
        ServerMaintenance.AttachSession(
            newApiClient, newClientId, LocalUserName, nextGeneration, nextIsEditor, ServerAddress);
        ResourceMasterData.AttachSession(newApiClient, newClientId, nextGeneration, nextIsEditor);
        if (!apiChanged
            && string.Equals(clientId, newClientId, StringComparison.Ordinal)
            && isEditor == nextIsEditor
            && editGeneration == nextGeneration)
        {
            return;
        }

        if (apiChanged)
        {
            apiClient = newApiClient;
            hasLoaded = false;
            ClearCollections();
        }

        clientId = newClientId;
        isEditor = nextIsEditor;
        editGeneration = nextGeneration;
        LegacyImport.AttachSession(apiClient, clientId, editStatus);
        OnPropertyChanged(nameof(IsEditor));
        OnPropertyChanged(nameof(AuthorityText));
        RaiseCommandStates();
    }

    internal async Task EnsureLoadedAsync()
    {
        if (!hasLoaded && apiClient is not null)
        {
            await RefreshAsync();
        }
    }

    internal async Task RefreshAsync()
    {
        if (!CanRead())
        {
            return;
        }

        var calendarId = SelectedCalendar?.WorkingCalendarId;
        var machineId = SelectedMachine?.MachineId;
        var downtimeId = SelectedDowntime?.DowntimeId;
        var machineTypeId = SelectedMachineType?.MachineTypeId;
        var postprocessorId = SelectedPostprocessor?.PostprocessorId;
        var resourceId = SelectedResource?.ResourceId;
        var israeliHolidayId = SelectedIsraeliHoliday?.IsraeliHolidayId;
        IsBusy = true;
        try
        {
            var calendarsTask = apiClient!.ListWorkingCalendarsAsync();
            var machinesTask = apiClient.ListMachinesAsync();
            var downtimesTask = apiClient.ListDowntimesAsync();
            var machineTypesTask = apiClient.ListMachineTypesAsync();
            var postprocessorsTask = apiClient.ListPostprocessorsAsync();
            var setupCalendarTask = apiClient.GetSetupCalendarAsync();
            var masterCalendarTask = apiClient.GetMasterCalendarAsync();
            var resourcesTask = apiClient.ListResourcesAsync();
            var holidaysTask = apiClient.ListIsraeliHolidaysAsync();
            var reportSettingsTask = apiClient.GetReportEmailSettingsAsync();
            var resourceMasterDataTask = ResourceMasterData.RefreshAsync();
            await Task.WhenAll(calendarsTask, machinesTask, downtimesTask, machineTypesTask, postprocessorsTask, setupCalendarTask, masterCalendarTask,
                resourcesTask, holidaysTask, reportSettingsTask, resourceMasterDataTask);

            Replace(WorkingCalendars, await calendarsTask);
            OnPropertyChanged(nameof(MachineWorkingCalendars));
            OnPropertyChanged(nameof(SetupWorkerCalendars));
            OnPropertyChanged(nameof(RegularWorkerCalendars));
            OnPropertyChanged(nameof(QaWorkerCalendars));
            OnPropertyChanged(nameof(ResourceCalendars));
            Replace(Machines, await machinesTask);
            RebuildResourceMachineSkills([]);
            Replace(Downtimes, await downtimesTask);
            Replace(MachineTypes, await machineTypesTask);
            Replace(Postprocessors, await postprocessorsTask);
            Replace(Resources, await resourcesTask);
            Replace(IsraeliHolidays, await holidaysTask);
            var setup = await setupCalendarTask;

            SelectedCalendar = FindCalendar(calendarId) ?? WorkingCalendars.FirstOrDefault();
            SelectedSetupCalendar = FindCalendar(setup.WorkingCalendarId);
            SelectedMasterCalendar = FindCalendar((await masterCalendarTask).WorkingCalendarId);
            SelectedMachine = Machines.FirstOrDefault(value => value.MachineId == machineId)
                ?? Machines.FirstOrDefault();
            SelectedDowntime = Downtimes.FirstOrDefault(value => value.DowntimeId == downtimeId)
                ?? Downtimes.FirstOrDefault();
            SelectedMachineType = MachineTypes.FirstOrDefault(value => value.MachineTypeId == machineTypeId)
                ?? MachineTypes.FirstOrDefault();
            SelectedPostprocessor = Postprocessors.FirstOrDefault(value => value.PostprocessorId == postprocessorId)
                ?? Postprocessors.FirstOrDefault();
            SelectedResource = Resources.FirstOrDefault(value => value.ResourceId == resourceId)
                ?? Resources.FirstOrDefault();
            SelectedIsraeliHoliday = IsraeliHolidays.FirstOrDefault(value => value.IsraeliHolidayId == israeliHolidayId)
                ?? IsraeliHolidays.FirstOrDefault();
            PopulateReportEmailSettings(await reportSettingsTask);
            RefreshMachineFormLookups();
            hasLoaded = true;
            StatusMessage = $"Setup data refreshed at {DateTime.Now:HH:mm:ss}.";
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

    internal Task BeginNewCalendarAsync()
    {
        editingCalendarId = null;
        selectedCalendar = null;
        OnPropertyChanged(nameof(SelectedCalendar));
        CalendarName = string.Empty;
        CalendarTimeZoneId = CalendarTimeZones[0];
        CalendarShiftStartsAt = "06:00";
        CalendarShiftEndsAt = "18:00";
        CalendarWindowsText = "06:00-18:00";
        CalendarBreakWindowsText = string.Empty;
        CalendarExceptionsText = string.Empty;
        CalendarUsageMachine = true;
        CalendarUsageSetupWorker = true;
        CalendarUsageRegularWorker = true;
        CalendarUsageQaWorker = true;
        CalendarUseIsraeliHolidays = false;
        SetWorkdays(["sunday", "monday", "tuesday", "wednesday", "thursday"]);
        OnPropertyChanged(nameof(CalendarFormHeading));
        OnPropertyChanged(nameof(IsCalendarEditable));
        StatusMessage = "Enter the recurring weekly calendar.";
        RaiseCommandStates();
        return Task.CompletedTask;
    }

    internal async Task SaveCalendarAsync()
    {
        if (!CanSaveCalendar()) return;
        var workdays = SelectedWorkdays();
        if (string.IsNullOrWhiteSpace(CalendarName) || workdays.Count == 0)
        {
            StatusMessage = "Calendar name and at least one working day are required.";
            return;
        }

        IReadOnlyList<WorkingCalendarWindow> windows;
        IReadOnlyList<WorkingCalendarWindow> breakWindows;
        IReadOnlyList<WorkingCalendarException> exceptions;
        try
        {
            windows = ParseCalendarWindows(CalendarWindowsText, required: true);
            breakWindows = ParseCalendarWindows(CalendarBreakWindowsText, required: false);
            exceptions = ParseCalendarExceptions();
        }
        catch (FormatException)
        {
            StatusMessage = "Use HH:mm-HH:mm windows and yyyy-MM-dd | closed or yyyy-MM-dd | windows | breaks | name exceptions.";
            return;
        }
        var usages = SelectedCalendarUsages();
        if (usages.Count == 0)
        {
            StatusMessage = "Select at least one Calendar usage.";
            return;
        }
        var savedId = editingCalendarId;
        var succeeded = false;
        IsBusy = true;
        try
        {
            if (savedId is null)
            {
                var created = await apiClient!.CreateWorkingCalendarAsync(
                    new WorkingCalendarCreate(
                        CalendarName, CalendarTimeZoneId, workdays,
                        null, null, windows, breakWindows, exceptions, usages, CalendarUseIsraeliHolidays),
                    clientId, editGeneration);
                savedId = created.WorkingCalendarId;
            }
            else
            {
                var selected = SelectedCalendar!;
                await apiClient!.UpdateWorkingCalendarAsync(
                    savedId,
                    new WorkingCalendarUpdate(
                        CalendarName, CalendarTimeZoneId, workdays,
                        null, null, windows, breakWindows, exceptions, usages, CalendarUseIsraeliHolidays),
                    CalendarEntityTag(selected), clientId, editGeneration);
            }

            succeeded = true;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }

        if (succeeded)
        {
            SelectedCalendar = null;
            await RefreshAsync();
            SelectedCalendar = FindCalendar(savedId);
            StatusMessage = "Calendar saved by the Server.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task DeleteSelectedCalendarAsync()
    {
        if (!CanDeleteCalendar()) return;
        var deleting = SelectedCalendar!;
        if (await TryDeleteAsync(() => apiClient!.DeleteWorkingCalendarAsync(
                deleting.WorkingCalendarId, clientId, editGeneration)))
        {
            await RefreshAsync();
            StatusMessage = $"Calendar {deleting.Name} deleted.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task SetSetupCalendarAsync()
    {
        if (!CanSetSetupCalendar()) return;
        var selected = SelectedSetupCalendar!;
        if (await TryMutationAsync(() => apiClient!.SetSetupCalendarAsync(
                selected.WorkingCalendarId, clientId, editGeneration)))
        {
            StatusMessage = $"{selected.Name} is the dedicated Setup Calendar.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task ClearSetupCalendarAsync()
    {
        if (!CanClearSetupCalendar()) return;
        if (await TryMutationAsync(() => apiClient!.ClearSetupCalendarAsync(clientId, editGeneration)))
        {
            SelectedSetupCalendar = null;
            StatusMessage = "Dedicated Setup Calendar cleared; the Server fallback will apply.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task SetMasterCalendarAsync()
    {
        if (!CanSetMasterCalendar()) return;
        var selected = SelectedMasterCalendar!;
        if (await TryMutationAsync(() => apiClient!.SetMasterCalendarAsync(selected.WorkingCalendarId, clientId, editGeneration)))
        {
            StatusMessage = $"{selected.Name} is the Israel Master Calendar.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task ClearMasterCalendarAsync()
    {
        if (!CanClearMasterCalendar()) return;
        if (await TryMutationAsync(() => apiClient!.ClearMasterCalendarAsync(clientId, editGeneration)))
        {
            SelectedMasterCalendar = null;
            StatusMessage = "Israel Master Calendar cleared.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal Task BeginNewMachineAsync()
    {
        editingMachineId = null;
        selectedMachine = null;
        OnPropertyChanged(nameof(SelectedMachine));
        MachineNumber = string.Empty;
        MachineName = string.Empty;
        MachineProcessType = string.Empty;
        MachineAxisType = string.Empty;
        MachineCapabilitiesText = string.Empty;
        SelectedMachineTypeForMachine = MachineTypes.FirstOrDefault();
        SelectedMachineCalendar = MachineWorkingCalendars.FirstOrDefault();
        MachinePicturePath = string.Empty;
        MachineIsActive = true;
        MachineDisplayEnabled = true;
        MachineRespectMasterCalendar = true;
        MachineExecutionMode = "MANUAL";
        MachineUsableToolPositions = string.Empty;
        MachineRapidRateMillimetersPerMinute = string.Empty;
        MachineToolChangeTimeSeconds = string.Empty;
        MachineTimeFactor = "1";
        ResetHaasForm();
        RebuildMachinePostprocessors([]);
        OnPropertyChanged(nameof(MachineFormHeading));
        StatusMessage = "Enter the Machine master data.";
        RaiseCommandStates();
        return Task.CompletedTask;
    }

    internal async Task SaveMachineAsync()
    {
        if (!CanSaveMachine()) return;
        if (string.IsNullOrWhiteSpace(MachineNumber)
            || string.IsNullOrWhiteSpace(MachineName)
            || (SelectedMachineTypeForMachine is null && string.IsNullOrWhiteSpace(MachineProcessType)))
        {
            StatusMessage = "Machine number, name, and type are required.";
            return;
        }

        if (!TryCreateMachineValues(MachineIsActive, out var values))
        {
            StatusMessage = "Use positive capacity and rapid-rate values, non-negative tool-change seconds, and a Machine time factor greater than zero.";
            return;
        }
        var savedId = editingMachineId;
        var succeeded = false;
        IsBusy = true;
        try
        {
            if (savedId is null)
            {
                savedId = (await apiClient!.CreateMachineAsync(values!, clientId, editGeneration)).MachineId;
            }
            else
            {
                await apiClient!.UpdateMachineAsync(
                    savedId, values!, MachineEntityTag(SelectedMachine!), clientId, editGeneration);
            }

            succeeded = true;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }

        if (succeeded)
        {
            SelectedMachine = null;
            await RefreshAsync();
            SelectedMachine = Machines.FirstOrDefault(value => value.MachineId == savedId);
            StatusMessage = "Machine saved by the Server.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task DeactivateSelectedMachineAsync()
    {
        if (!CanDeactivateMachine()) return;
        var machine = SelectedMachine!;
        PopulateMachineForm(machine);
        if (!TryCreateMachineValues(false, out var values)) return;
        if (await TryMutationAsync(async () =>
            {
                await apiClient!.UpdateMachineAsync(
                    machine.MachineId, values!, MachineEntityTag(machine),
                    clientId, editGeneration);
            }))
        {
            await RefreshAsync();
            SelectedMachine = Machines.FirstOrDefault(value => value.MachineId == machine.MachineId);
            StatusMessage = $"Machine {machine.Number} deactivated.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task LoadHaasConfigurationAsync()
    {
        if (!CanReadHaas()) return;
        await RunHaasReadAsync(async () =>
        {
            var adapters = await apiClient!.ListCncAdaptersAsync();
            CncAdapters.Clear();
            foreach (var adapter in adapters) CncAdapters.Add(adapter);
            SelectedCncAdapter = CncAdapters.FirstOrDefault(value => value.Id == "HAAS_NGC");
            var value = await apiClient!.GetHaasConnectionAsync(SelectedMachine!.MachineId);
            PopulateHaasConfiguration(value);
            HaasDiagnostics = value.Version == 0
                ? "Haas NGC is not configured for this Machine."
                : "Configuration loaded. Workflow state is derived from Server events.";
        });
    }

    internal async Task SaveHaasConfigurationAsync()
    {
        if (!CanManageHaas()) return;
        if (SelectedCncAdapter is { Implemented: false } || SelectedCncAdapter?.Id is not (null or "HAAS_NGC"))
        {
            HaasDiagnostics = "The selected adapter is registered for future use and cannot be enabled or saved.";
            return;
        }
        if (!int.TryParse(HaasMdcPort, out var mdcPort)
            || !int.TryParse(HaasMtConnectPort, out var mtPort)
            || !int.TryParse(HaasDprntPort, out var dprntPort)
            || !int.TryParse(HaasPollingIntervalMs, out var polling)
            || !int.TryParse(HaasConnectionTimeoutMs, out var timeout))
        {
            HaasDiagnostics = "Haas ports, polling, and timeout must be numeric.";
            return;
        }
        await RunHaasReadAsync(async () =>
        {
            var value = await apiClient!.UpdateHaasConnectionAsync(SelectedMachine!.MachineId,
                new HaasConnectionUpdate(HaasHost, HaasMacAddress, mdcPort, mtPort, dprntPort, HaasLocalNetShareEnabled,
                    NullIfBlank(HaasLocalNetSharePath), NullIfBlank(HaasCredentialsReference),
                    HaasPartCounterSource, polling, timeout, 2, 50, 32768,
                    [@"\bPART(?:\s+NAME)?\s*[:=]\s*([^()\r\n]+)"], HaasEnabled, haasSettingsVersion,
                    HaasTelemetryProvider),
                clientId, editGeneration);
            PopulateHaasConfiguration(value);
            HaasDiagnostics = "Haas NGC configuration saved by the Server.";
        });
    }

    internal async Task LoadVerificationConfigurationAsync()
    {
        if (!CanReadHaas()) return;
        await RunHaasReadAsync(async () =>
        {
            try
            {
                var value = await apiClient!.GetCncVerificationSettingsAsync(SelectedMachine!.MachineId);
                PopulateVerificationConfiguration(value);
                HaasDiagnostics = "Verification configuration loaded.";
            }
            catch (PlannerApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                verificationSettingsVersion = 0;
                HaasDiagnostics = "Verification is not configured. Review all controller-specific values before saving.";
            }
        });
    }

    internal async Task SaveVerificationConfigurationAsync()
    {
        if (!CanManageHaas()) return;
        if (!int.TryParse(VerificationDprintPort, out var dprintPort)
            || !int.TryParse(VerificationChallengeProgram, out var challengeProgram)
            || !int.TryParse(VerificationVerifyProgram, out var verifyProgram)
            || !TryOptionalInt(VerificationCustomGcodeAlias, out var alias)
            || !int.TryParse(VerificationNonceVariable, out var nonceVariable)
            || !int.TryParse(VerificationResponseVariable, out var responseVariable)
            || !int.TryParse(VerificationStateVariable, out var stateVariable)
            || !int.TryParse(VerificationReleaseTokenVariable, out var releaseTokenVariable)
            || !int.TryParse(VerificationFinalizeProgram, out var finalizeProgram)
            || !int.TryParse(VerificationEventSequenceVariable, out var eventSequenceVariable)
            || !int.TryParse(VerificationMacroVersion, out var macroVersion)
            || !int.TryParse(VerificationCodeDigits, out var digits)
            || !int.TryParse(VerificationTimeoutSeconds, out var timeout))
        {
            HaasDiagnostics = "Verification ports, program numbers, variables, version, digits, and timeout must be numeric.";
            return;
        }
        await RunHaasReadAsync(async () =>
        {
            var value = await apiClient!.UpdateCncVerificationSettingsAsync(
                SelectedMachine!.MachineId, new("HAAS_DPRNT_TCP", dprintPort,
                    challengeProgram, verifyProgram, alias, nonceVariable, responseVariable,
                    stateVariable, releaseTokenVariable, finalizeProgram, eventSequenceVariable,
                    macroVersion, digits, timeout, VerificationEnabled,
                    verificationSettingsVersion), clientId, editGeneration);
            PopulateVerificationConfiguration(value);
            HaasDiagnostics = "Verification configuration saved. Enable only after Machine commissioning.";
        });
    }

    private void PopulateVerificationConfiguration(CncVerificationSettings value)
    {
        VerificationDprintPort = value.DprintPort.ToString(CultureInfo.InvariantCulture);
        VerificationChallengeProgram = value.ChallengeProgramNumber.ToString(CultureInfo.InvariantCulture);
        VerificationVerifyProgram = value.VerifyProgramNumber.ToString(CultureInfo.InvariantCulture);
        VerificationCustomGcodeAlias = value.CustomGcodeAlias?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        VerificationNonceVariable = value.NonceVariable.ToString(CultureInfo.InvariantCulture);
        VerificationResponseVariable = value.ResponseVariable.ToString(CultureInfo.InvariantCulture);
        VerificationStateVariable = value.VerificationStateVariable.ToString(CultureInfo.InvariantCulture);
        VerificationReleaseTokenVariable = value.ReleaseTokenVariable.ToString(CultureInfo.InvariantCulture);
        VerificationFinalizeProgram = value.FinalizeProgramNumber?.ToString(CultureInfo.InvariantCulture) ?? "9003";
        VerificationEventSequenceVariable = value.EventSequenceVariable?.ToString(CultureInfo.InvariantCulture) ?? "10504";
        VerificationMacroVersion = value.ExpectedMacroVersion.ToString(CultureInfo.InvariantCulture);
        VerificationCodeDigits = value.ResponseCodeDigits.ToString(CultureInfo.InvariantCulture);
        VerificationTimeoutSeconds = value.VerificationTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        VerificationEnabled = value.Enabled;
        verificationSettingsVersion = value.Version;
    }

    internal async Task GenerateOffsetLoaderReleaseAsync()
    {
        if (!CanManageHaas()) return;
        var runId = VerificationRecoveryRunId.Trim();
        var ncReleaseId = VerificationRecoveryNcReleaseId.Trim();
        var toolTableReleaseId = VerificationRecoveryToolTableReleaseId.Trim();
        if (runId.Length == 0 || ncReleaseId.Length == 0 || toolTableReleaseId.Length == 0)
        {
            HaasDiagnostics = "Production Run, approved NC release, and tool-table release are required to generate an Offset Loader release.";
            return;
        }
        await RunHaasReadAsync(async () =>
        {
            var value = await apiClient!.CreateOffsetLoaderReleaseAsync(
                runId, new(SelectedMachine!.MachineId, ncReleaseId, toolTableReleaseId),
                clientId, editGeneration);
            HaasDiagnostics = $"New current Offset Loader {value.OffsetLoaderReleaseId} created; verification token {value.VerificationReleaseToken:D6}. Prior releases remain immutable history.";
        });
    }

    internal Task InvalidateVerificationAsync() => RunVerificationRecoveryAsync(
        "invalidate the current verification session",
        (runId, request) => apiClient!.InvalidateCncVerificationAsync(
            runId, request, clientId, editGeneration),
        "Current verification session invalidated. Run the current Offset Loader again before verification.");

    internal Task RevokeCurrentOffsetLoaderAsync() => RunVerificationRecoveryAsync(
        "revoke the current Offset Loader",
        (runId, request) => apiClient!.RevokeCurrentOffsetLoaderAsync(
            runId, request, clientId, editGeneration),
        "Current Offset Loader revoked. Generate and execute a valid replacement; immutable release history was preserved.");

    private async Task RunVerificationRecoveryAsync(
        string action,
        Func<string, CncRecoveryRequest, Task<CncRecoveryResult>> submit,
        string successMessage)
    {
        if (!CanManageHaas()) return;
        var runId = VerificationRecoveryRunId.Trim();
        var reason = VerificationRecoveryReason.Trim();
        if (runId.Length == 0 || reason.Length == 0)
        {
            HaasDiagnostics = $"Production Run and a recovery reason are required to {action}.";
            return;
        }
        await RunHaasReadAsync(async () =>
        {
            await submit(runId, new(SelectedMachine!.MachineId, reason));
            HaasDiagnostics = successMessage;
        });
    }

    internal Task TestHaasMdcAsync() => RunHaasReadAsync(async () =>
    {
        var result = await apiClient!.TestHaasMdcAsync(SelectedMachine!.MachineId);
        HaasDiagnostics = result.Succeeded
            ? $"MDC: Connected | Program: {result.ProgramNumber ?? "none"} | Status: {result.MachineStatus} | Parts: {result.Parts}"
            : $"MDC: {result.Message}";
    });

    internal Task TestHaasConnectionAsync() =>
        string.Equals(HaasTelemetryProvider, "MTCONNECT", StringComparison.OrdinalIgnoreCase)
            ? TestHaasMtConnectAsync()
            : TestHaasMdcAsync();

    internal Task TestHaasMtConnectAsync() => RunHaasReadAsync(async () =>
    {
        var result = await apiClient!.TestHaasMtConnectAsync(SelectedMachine!.MachineId);
        var program = result.ProgramNumber ?? "none";
        var machineStatus = result.MachineStatus ?? "unknown";
        var parts = result.Parts?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
        HaasDiagnostics = result.Succeeded
            ? $"MTConnect: {result.Message} | Program: {program} | Status: {machineStatus} | Parts: {parts}"
            : $"MTConnect: {result.Message}";
    });

    internal Task TestHaasNetShareAsync() => RunHaasReadAsync(async () =>
    {
        var result = await apiClient!.TestHaasNetShareAsync(SelectedMachine!.MachineId);
        HaasDiagnostics = result.Succeeded
            ? $"Net Share: Connected | Program: {result.ProgramNumber} | Part: {result.Header?.PartName}"
            : $"Net Share: {result.Message}";
    });

    internal Task RefreshHaasMonitorAsync() => RunHaasReadAsync(async () =>
    {
        var value = await apiClient!.GetHaasMonitorAsync(SelectedMachine!.MachineId);
        var snapshot = value.Snapshot;
        HaasDiagnostics = snapshot is null
            ? "No Haas telemetry snapshot has been received."
            : $"{snapshot.ConnectivityState} | Part: {snapshot.MachineHeaderPartName ?? "unverified"} | Program: {snapshot.ProgramNumber ?? "none"} | File: {Path.GetFileName(snapshot.MachineHeaderSourcePath) ?? "unavailable"} | Bench: {value.ActiveBench?.BatchOperationId ?? "none"} | Parts counter: {snapshot.PartCounter?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} | Last Poll: {snapshot.Timestamp.ToLocalTime():HH:mm:ss}";
        HaasTimeline = value.RecentEvents.Count == 0
            ? "No Haas Bench events recorded."
            : string.Join(Environment.NewLine, value.RecentEvents.Reverse().Select(item =>
                $"{item.Timestamp.ToLocalTime():HH:mm:ss}  {item.EventType}"));
        if (value.ActiveBench is not null)
            HaasTimeline += $"{Environment.NewLine}Actual Setup: {TimeSpan.FromSeconds(value.ActualSetupSeconds):g} | Actual Production: {TimeSpan.FromSeconds(value.ActualProductionSeconds):g}";
    });

    internal Task ReconnectCncAsync() => RunHaasReadAsync(async () =>
    {
        await apiClient!.ReconnectCncAsync(SelectedMachine!.MachineId, clientId, editGeneration);
        HaasDiagnostics = "Server-side CNC reconnect requested. Browser/client connectivity is unchanged.";
    });

    internal async Task DeleteSelectedMachineAsync()
    {
        if (!CanDeleteMachine()) return;
        var deleting = SelectedMachine!;
        if (await TryDeleteAsync(() => apiClient!.DeleteMachineAsync(
                deleting.MachineId, clientId, editGeneration)))
        {
            await RefreshAsync();
            StatusMessage = $"Machine {deleting.Number} deleted.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal Task BeginNewPlannedMaintenanceAsync()
    {
        editingDowntimeId = null;
        selectedDowntime = null;
        OnPropertyChanged(nameof(SelectedDowntime));
        DowntimeType = "planned_maintenance";
        SelectedDowntimeMachine = SelectedMachine ?? Machines.FirstOrDefault();
        DowntimeStartsAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        DowntimeEndsAt = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        DowntimeReason = string.Empty;
        DowntimeActor = LocalUserName;
        DowntimeRepairNote = string.Empty;
        StatusMessage = "Enter the planned maintenance window.";
        RaiseCommandStates();
        return Task.CompletedTask;
    }

    internal Task BeginNewBreakdownAsync()
    {
        editingDowntimeId = null;
        selectedDowntime = null;
        OnPropertyChanged(nameof(SelectedDowntime));
        DowntimeType = "breakdown";
        SelectedDowntimeMachine = SelectedMachine ?? Machines.FirstOrDefault();
        DowntimeStartsAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        DowntimeEndsAt = string.Empty;
        DowntimeReason = string.Empty;
        DowntimeActor = LocalUserName;
        DowntimeRepairNote = string.Empty;
        StatusMessage = "Report the breakdown; it blocks the Machine until Restore is recorded.";
        RaiseCommandStates();
        return Task.CompletedTask;
    }

    internal async Task SaveDowntimeAsync()
    {
        if (!CanSaveDowntime()) return;
        if (!TryParseLocalInstant(DowntimeStartsAt, out var startsAt)
            || (IsPlannedMaintenance && !TryParseLocalInstant(DowntimeEndsAt, out _)))
        {
            StatusMessage = "Use yyyy-MM-dd HH:mm for downtime dates and times.";
            return;
        }
        if (string.IsNullOrWhiteSpace(DowntimeReason) || string.IsNullOrWhiteSpace(DowntimeActor))
        {
            StatusMessage = "Reason and the responsible/reported-by person are required.";
            return;
        }
        var savedId = editingDowntimeId;
        var succeeded = false;
        IsBusy = true;
        try
        {
            if (savedId is null)
            {
                DateTimeOffset? endsAt = null;
                if (IsPlannedMaintenance)
                {
                    TryParseLocalInstant(DowntimeEndsAt, out var parsedEnd);
                    endsAt = parsedEnd;
                }
                var created = await apiClient!.CreateDowntimeAsync(new MachineDowntimeCreate(
                    DowntimeType, SelectedDowntimeMachine!.MachineId, startsAt, endsAt,
                    DowntimeReason, IsPlannedMaintenance ? DowntimeActor : null,
                    IsBreakdown ? DowntimeActor : null), clientId, editGeneration);
                savedId = created.DowntimeId;
            }
            else
            {
                TryParseLocalInstant(DowntimeEndsAt, out var endsAt);
                await apiClient!.UpdatePlannedMaintenanceAsync(savedId,
                    new PlannedMaintenanceUpdate(SelectedDowntimeMachine!.MachineId, startsAt,
                        endsAt, DowntimeReason, DowntimeActor),
                    DowntimeEntityTag(SelectedDowntime!), clientId, editGeneration);
            }
            succeeded = true;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }
        if (succeeded)
        {
            SelectedDowntime = null;
            await RefreshAsync();
            SelectedDowntime = Downtimes.FirstOrDefault(value => value.DowntimeId == savedId);
            StatusMessage = IsBreakdown ? "Machine marked broken." : "Planned maintenance saved.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task RestoreSelectedBreakdownAsync()
    {
        if (!CanRestoreBreakdown()) return;
        if (!TryParseLocalInstant(DowntimeRestoredAt, out var restoredAt))
        {
            StatusMessage = "Use yyyy-MM-dd HH:mm for the restored time.";
            return;
        }
        var downtime = SelectedDowntime!;
        if (await TryMutationAsync(async () =>
            {
                await apiClient!.RestoreBreakdownAsync(downtime.DowntimeId,
                    new BreakdownRestore(restoredAt, DowntimeRepairNote),
                    DowntimeEntityTag(downtime), clientId, editGeneration);
            }))
        {
            await RefreshAsync();
            SelectedDowntime = Downtimes.FirstOrDefault(value => value.DowntimeId == downtime.DowntimeId);
            StatusMessage = "Machine marked restored; Timeline availability was recalculated.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal Task BeginNewMachineTypeAsync()
    {
        editingMachineTypeId = null;
        selectedMachineType = null;
        OnPropertyChanged(nameof(SelectedMachineType));
        MachineTypeName = string.Empty;
        MachineTypeCapabilitiesText = string.Empty;
        OnPropertyChanged(nameof(MachineTypeFormHeading));
        StatusMessage = "Enter a reusable Machine Type.";
        RaiseCommandStates();
        return Task.CompletedTask;
    }

    internal async Task SaveMachineTypeAsync()
    {
        if (!CanSaveMachineType()) return;
        if (string.IsNullOrWhiteSpace(MachineTypeName))
        {
            StatusMessage = "Machine Type name is required.";
            return;
        }

        var values = ParseTokens(MachineTypeCapabilitiesText);
        var savedId = editingMachineTypeId;
        var succeeded = false;
        IsBusy = true;
        try
        {
            if (savedId is null)
            {
                savedId = (await apiClient!.CreateMachineTypeAsync(
                    new MachineTypeCreate(MachineTypeName, values), clientId, editGeneration)).MachineTypeId;
            }
            else
            {
                await apiClient!.UpdateMachineTypeAsync(
                    savedId, new MachineTypeUpdate(MachineTypeName, values),
                    MachineTypeEntityTag(SelectedMachineType!), clientId, editGeneration);
            }

            succeeded = true;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }

        if (succeeded)
        {
            SelectedMachineType = null;
            await RefreshAsync();
            SelectedMachineType = MachineTypes.FirstOrDefault(value => value.MachineTypeId == savedId);
            StatusMessage = "Machine Type saved by the Server.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task DeleteSelectedMachineTypeAsync()
    {
        if (!CanDeleteMachineType()) return;
        var deleting = SelectedMachineType!;
        if (await TryDeleteAsync(() => apiClient!.DeleteMachineTypeAsync(
                deleting.MachineTypeId, clientId, editGeneration)))
        {
            await RefreshAsync();
            StatusMessage = $"Machine Type {deleting.Name} deleted.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal Task BeginNewPostprocessorAsync()
    {
        editingPostprocessorId = null;
        selectedPostprocessor = null;
        OnPropertyChanged(nameof(SelectedPostprocessor));
        PostprocessorName = string.Empty;
        PostprocessorDescription = string.Empty;
        PostprocessorIsActive = true;
        OnPropertyChanged(nameof(PostprocessorFormHeading));
        StatusMessage = "Enter the postprocessor configuration.";
        RaiseCommandStates();
        return Task.CompletedTask;
    }

    internal async Task SavePostprocessorAsync()
    {
        if (!CanSavePostprocessor()) return;
        if (string.IsNullOrWhiteSpace(PostprocessorName))
        {
            StatusMessage = "Postprocessor name is required.";
            return;
        }

        var savedId = editingPostprocessorId;
        var succeeded = false;
        IsBusy = true;
        try
        {
            if (savedId is null)
            {
                savedId = (await apiClient!.CreatePostprocessorAsync(
                    new PostprocessorCreate(PostprocessorName.Trim(), NullIfBlank(PostprocessorDescription), PostprocessorIsActive),
                    clientId, editGeneration)).PostprocessorId;
            }
            else
            {
                await apiClient!.UpdatePostprocessorAsync(
                    savedId,
                    new PostprocessorUpdate(PostprocessorName.Trim(), NullIfBlank(PostprocessorDescription), PostprocessorIsActive),
                    PostprocessorEntityTag(SelectedPostprocessor!), clientId, editGeneration);
            }

            succeeded = true;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }

        if (succeeded)
        {
            SelectedPostprocessor = null;
            await RefreshAsync();
            SelectedPostprocessor = Postprocessors.FirstOrDefault(value => value.PostprocessorId == savedId);
            StatusMessage = "Postprocessor saved by the Server.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task DeleteSelectedPostprocessorAsync()
    {
        if (!CanDeletePostprocessor()) return;
        var deleting = SelectedPostprocessor!;
        if (await TryDeleteAsync(() => apiClient!.DeletePostprocessorAsync(
                deleting.PostprocessorId, clientId, editGeneration)))
        {
            await RefreshAsync();
            StatusMessage = $"Postprocessor {deleting.Name} deleted.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal Task BeginNewResourceAsync()
    {
        editingResourceId = null;
        selectedResource = null;
        OnPropertyChanged(nameof(SelectedResource));
        ResourceEmployeeNumber = string.Empty;
        ResourceFirstName = string.Empty;
        ResourceLastName = string.Empty;
        ResourceRole = "regular_worker";
        RebuildResourceMachineSkills([]);
        SelectedResourceCalendar = null;
        ResourcePhotoPath = string.Empty;
        ResourceNotes = string.Empty;
        ResourceEmail = string.Empty;
        ResourceToolLoadSecondsPerTool = "60";
        ResourceFixtureAssemblySeconds = string.Empty;
        ResourceFirstPartRunningSpeedPercent = "66.6667";
        ResourceIsActive = true;
        ResourceRespectMasterCalendar = true;
        OnPropertyChanged(nameof(ResourceFormHeading));
        OnPropertyChanged(nameof(ResourceSaveActionText));
        StatusMessage = "Enter the employee or resource master data.";
        RaiseCommandStates();
        return Task.CompletedTask;
    }

    internal Task EditSelectedResourceAsync()
    {
        if (SelectedResource is null) return Task.CompletedTask;

        PopulateResourceForm(SelectedResource);
        StatusMessage = $"Editing employee / resource {SelectedResource.Name}.";
        RaiseCommandStates();
        return Task.CompletedTask;
    }

    internal async Task SaveResourceAsync()
    {
        if (!CanSaveResource()) return;
        if (string.IsNullOrWhiteSpace(ResourceEmployeeNumber)
            || string.IsNullOrWhiteSpace(ResourceFirstName)
            || string.IsNullOrWhiteSpace(ResourceLastName)
            || SelectedResourceCalendar is null)
        {
            StatusMessage = "Employee number, first name, last name, role, and calendar are required.";
            return;
        }

        var savedId = editingResourceId;
        var update = new ResourceUpdate(ResourceEmployeeNumber.Trim(), ResourceFirstName.Trim(), ResourceLastName.Trim(), ResourceRole,
            ResourceMachineSkills.Where(value => value.IsSelected).Select(value => value.MachineId).ToArray(),
            SelectedResourceCalendar.WorkingCalendarId, NullIfBlank(ResourcePhotoPath),
            NullIfBlank(ResourceNotes), NullIfBlank(ResourceEmail), ResourceIsActive, ResourceRespectMasterCalendar,
            double.Parse(ResourceToolLoadSecondsPerTool, CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(ResourceFixtureAssemblySeconds) ? null : double.Parse(ResourceFixtureAssemblySeconds, CultureInfo.InvariantCulture),
            double.Parse(ResourceFirstPartRunningSpeedPercent, CultureInfo.InvariantCulture));
        var succeeded = false;
        IsBusy = true;
        try
        {
            if (savedId is null)
            {
                savedId = (await apiClient!.CreateResourceAsync(
                    new ResourceCreate(update.EmployeeNumber, update.FirstName, update.LastName, update.Role, update.Skills, update.AssignedCalendarId, update.PhotoPath, update.Notes, update.Email, update.IsActive, update.RespectMasterCalendar),
                    clientId, editGeneration)).ResourceId;
            }
            else
            {
                await apiClient!.UpdateResourceAsync(savedId, update, ResourceEntityTag(SelectedResource!), clientId, editGeneration);
            }
            succeeded = true;
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = FriendlyMessage(exception); }
        finally { IsBusy = false; }

        if (succeeded)
        {
            SelectedResource = null;
            await RefreshAsync();
            SelectedResource = Resources.FirstOrDefault(value => value.ResourceId == savedId);
            StatusMessage = "Employee / resource saved by the Server.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task DeleteSelectedResourceAsync()
    {
        if (!CanDeleteResource()) return;
        var deleting = SelectedResource!;
        if (await TryDeleteAsync(() => apiClient!.DeleteResourceAsync(deleting.ResourceId, clientId, editGeneration)))
        {
            await RefreshAsync();
            StatusMessage = $"Employee / resource {deleting.Name} deleted.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task RefreshResourceExceptionsAsync()
    {
        if (SelectedResource is null) return;
        await LoadResourceExceptionsAsync(SelectedResource.ResourceId);
    }

    private async Task LoadResourceExceptionsAsync(string resourceId)
    {
        if (apiClient is null) return;
        try
        {
            var values = await apiClient.ListEmployeeExceptionsAsync(resourceId);
            if (SelectedResource?.ResourceId != resourceId) return;
            Replace(ResourceExceptions, values);
            SelectedResourceException = ResourceExceptions.FirstOrDefault(value =>
                value.ExceptionId == editingResourceExceptionId) ?? ResourceExceptions.FirstOrDefault();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
        }
    }

    internal Task BeginNewResourceExceptionAsync()
    {
        editingResourceExceptionId = null;
        selectedResourceException = null;
        OnPropertyChanged(nameof(SelectedResourceException));
        ResourceExceptionDate = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
        ResourceExceptionType = "unavailable";
        ResourceExceptionIsFullDay = true;
        ResourceExceptionStartsAt = "09:00";
        ResourceExceptionEndsAt = "12:00";
        ResourceExceptionNote = string.Empty;
        OnPropertyChanged(nameof(ResourceExceptionFormHeading));
        RaiseCommandStates();
        return Task.CompletedTask;
    }

    internal async Task SaveResourceExceptionAsync()
    {
        if (!CanManageResourceExceptions() || SelectedResource is null) return;
        if (!DateOnly.TryParseExact(ResourceExceptionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            StatusMessage = "Exception date must use yyyy-MM-dd.";
            return;
        }
        if (!ResourceExceptionIsFullDay
            && (string.IsNullOrWhiteSpace(ResourceExceptionStartsAt) || string.IsNullOrWhiteSpace(ResourceExceptionEndsAt)))
        {
            StatusMessage = "Partial-day exceptions require start and end times.";
            return;
        }

        var resourceId = SelectedResource.ResourceId;
        var savedId = editingResourceExceptionId;
        var update = new EmployeeCalendarExceptionUpdate(
            ResourceExceptionDate.Trim(), ResourceExceptionType, ResourceExceptionIsFullDay,
            ResourceExceptionIsFullDay ? null : ResourceExceptionStartsAt.Trim(),
            ResourceExceptionIsFullDay ? null : ResourceExceptionEndsAt.Trim(),
            NullIfBlank(ResourceExceptionNote));
        var succeeded = await TryMutationAsync(async () =>
        {
            if (savedId is null)
            {
                savedId = (await apiClient!.CreateEmployeeExceptionAsync(resourceId,
                    new(update.Date, update.ExceptionType, update.IsFullDay, update.StartsAtLocal, update.EndsAtLocal, update.Note),
                    clientId, editGeneration)).ExceptionId;
            }
            else
            {
                await apiClient!.UpdateEmployeeExceptionAsync(resourceId, savedId, update,
                    ResourceExceptionEntityTag(SelectedResourceException!), clientId, editGeneration);
            }
        });
        if (!succeeded) return;
        editingResourceExceptionId = savedId;
        await LoadResourceExceptionsAsync(resourceId);
        SelectedResourceException = ResourceExceptions.FirstOrDefault(value => value.ExceptionId == savedId);
        StatusMessage = "Employee availability exception saved by the Server.";
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async Task DeleteSelectedResourceExceptionAsync()
    {
        if (!CanDeleteResourceException()) return;
        var resourceId = SelectedResource!.ResourceId;
        var deleting = SelectedResourceException!;
        if (!await TryDeleteAsync(() => apiClient!.DeleteEmployeeExceptionAsync(
                resourceId, deleting.ExceptionId, clientId, editGeneration))) return;
        editingResourceExceptionId = null;
        await LoadResourceExceptionsAsync(resourceId);
        StatusMessage = $"Employee availability exception {deleting.DisplayName} deleted.";
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    internal Task BeginNewIsraeliHolidayAsync()
    {
        editingIsraeliHolidayId = null;
        selectedIsraeliHoliday = null;
        OnPropertyChanged(nameof(SelectedIsraeliHoliday));
        IsraeliHolidayDate = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
        IsraeliHolidayName = string.Empty;
        IsraeliHolidayStatus = "non_working";
        IsraeliHolidayStartsAt = "08:00";
        IsraeliHolidayEndsAt = "13:00";
        OnPropertyChanged(nameof(IsraeliHolidayFormHeading));
        StatusMessage = "Enter an Israeli holiday exception.";
        RaiseCommandStates();
        return Task.CompletedTask;
    }

    internal async Task SaveIsraeliHolidayAsync()
    {
        if (!CanSaveIsraeliHoliday()) return;
        if (!DateOnly.TryParseExact(IsraeliHolidayDate, "yyyy-MM-dd", out _)
            || string.IsNullOrWhiteSpace(IsraeliHolidayName))
        {
            StatusMessage = "Holiday date (yyyy-MM-dd) and name are required.";
            return;
        }

        var savedId = editingIsraeliHolidayId;
        var update = new IsraeliHolidayUpdate(IsraeliHolidayDate, IsraeliHolidayName.Trim(), IsraeliHolidayStatus,
            IsIsraeliHolidayPartialWorking ? IsraeliHolidayStartsAt : null,
            IsIsraeliHolidayPartialWorking ? IsraeliHolidayEndsAt : null);
        var succeeded = false;
        IsBusy = true;
        try
        {
            if (savedId is null)
            {
                savedId = (await apiClient!.CreateIsraeliHolidayAsync(
                    new IsraeliHolidayCreate(update.Date, update.Name, update.Status, update.StartsAtLocal, update.EndsAtLocal), clientId, editGeneration)).IsraeliHolidayId;
            }
            else
            {
                await apiClient!.UpdateIsraeliHolidayAsync(
                    savedId, update, IsraeliHolidayEntityTag(SelectedIsraeliHoliday!), clientId, editGeneration);
            }
            succeeded = true;
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = FriendlyMessage(exception); }
        finally { IsBusy = false; }

        if (succeeded)
        {
            SelectedIsraeliHoliday = null;
            await RefreshAsync();
            SelectedIsraeliHoliday = IsraeliHolidays.FirstOrDefault(value => value.IsraeliHolidayId == savedId);
            StatusMessage = "Israeli holiday saved by the Server.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task SynchronizeIsraeliHolidaysAsync()
    {
        if (!CanManage()) return;
        if(!int.TryParse(HolidaySyncFromYear,NumberStyles.None,CultureInfo.InvariantCulture,out var fromYear)
           || !int.TryParse(HolidaySyncToYear,NumberStyles.None,CultureInfo.InvariantCulture,out var toYear))
        { HolidaySyncStatus="Enter valid Gregorian years."; return; }
        IsBusy=true;
        try
        {
            var result=await apiClient!.SynchronizeIsraeliHolidaysAsync(new(fromYear,toYear),clientId,editGeneration);
            HolidaySyncStatus=result.Succeeded
                ? $"Online refresh completed: {result.Created} added, {result.Updated} updated, {result.PreservedManual} manual corrections preserved. Cache timestamp: {result.LastSuccessAt:g}."
                : $"Offline/cache mode: {result.Error} Existing cached holidays remain available.";
            await RefreshAsync();
            if(result.Succeeded)ConfigurationChanged?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception exception) when(IsExpected(exception)) { HolidaySyncStatus=FriendlyMessage(exception)+" Existing cached holidays remain available."; }
        finally { IsBusy=false; }
    }

    internal async Task DeleteSelectedIsraeliHolidayAsync()
    {
        if (!CanDeleteIsraeliHoliday()) return;
        var deleting = SelectedIsraeliHoliday!;
        if (await TryDeleteAsync(() => apiClient!.DeleteIsraeliHolidayAsync(deleting.IsraeliHolidayId, clientId, editGeneration)))
        {
            await RefreshAsync();
            StatusMessage = $"Israeli holiday {deleting.Name} deleted.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task SaveReportEmailSettingsAsync()
    {
        if (!CanSaveReportEmailSettings()) return;
        int? smtpPort = null;
        if (!string.IsNullOrWhiteSpace(ReportSmtpPort)
            && (!int.TryParse(ReportSmtpPort, out var parsedPort) || parsedPort < 1 || parsedPort > 65535))
        {
            StatusMessage = "SMTP port must be a number from 1 through 65535.";
            return;
        }
        else if (!string.IsNullOrWhiteSpace(ReportSmtpPort)) smtpPort = int.Parse(ReportSmtpPort);
        if (DailyReportEnabled && !TimeOnly.TryParseExact(DailyReportTimeLocal, "HH:mm", out _))
        {
            StatusMessage = "Daily report time must be HH:mm.";
            return;
        }
        if (!TimeOnly.TryParseExact(WeeklyMaterialReportTimeLocal, "HH:mm", out _))
        {
            StatusMessage = "Weekly material report time must be HH:mm.";
            return;
        }
        if (!TimeOnly.TryParseExact(WeeklyEmployeeEfficiencyTimeLocal, "HH:mm", out _))
        {
            StatusMessage = "Weekly employee efficiency report time must be HH:mm.";
            return;
        }

        var saved = false;
        IsBusy = true;
        try
        {
            var resource = await apiClient!.UpdateReportEmailSettingsAsync(
                new ReportEmailSettingsUpdate(NullIfBlank(ReportSenderAddress), ParseTokens(ReportRecipientsText),
                    NullIfBlank(ReportSmtpHost), smtpPort, ReportUseSsl, DailyReportEnabled,
                    DailyReportEnabled ? DailyReportTimeLocal : null, NullIfBlank(ReportTimeZoneId),
                    WeeklyMaterialReportEnabled, WeeklyMaterialReportSendDay, WeeklyMaterialReportTimeLocal,
                    WeeklyEmployeeEfficiencyEnabled, WeeklyEmployeeEfficiencySendDay, WeeklyEmployeeEfficiencyTimeLocal),
                reportEmailSettingsEntityTag ?? "\"report-email-settings:1:v0\"", clientId, editGeneration);
            PopulateReportEmailSettings(resource);
            saved = true;
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = FriendlyMessage(exception); }
        finally { IsBusy = false; }
        if (saved)
        {
            StatusMessage = "Report and email settings saved by the Server.";
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task SendWeeklyMaterialReportNowAsync()
    {
        if (!CanSaveReportEmailSettings()) return;
        IsBusy = true;
        try
        {
            var report = await apiClient!.SendWeeklyMaterialReportAsync(clientId, editGeneration);
            StatusMessage = $"Weekly material report sent ({report.Items.Count} Case/Part rows).";
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = FriendlyMessage(exception); }
        finally { IsBusy = false; }
    }

    internal async Task SendWeeklyEmployeeEfficiencyReportNowAsync()
    {
        if (!CanSaveReportEmailSettings()) return;
        IsBusy = true;
        try
        {
            var report = await apiClient!.SendWeeklyEmployeeEfficiencyReportAsync(clientId, editGeneration);
            StatusMessage = $"Weekly employee efficiency report sent ({report.Employees.Count} employees).";
        }
        catch (Exception exception) when (IsExpected(exception)) { StatusMessage = FriendlyMessage(exception); }
        finally { IsBusy = false; }
    }

    private void PopulateCalendarForm(WorkingCalendar value)
    {
        editingCalendarId = value.WorkingCalendarId;
        CalendarName = value.Name;
        CalendarTimeZoneId = value.TimeZoneId;
        CalendarShiftStartsAt = value.ShiftStartsAtLocal ?? string.Empty;
        CalendarShiftEndsAt = value.ShiftEndsAtLocal ?? string.Empty;
        var windows = value.Windows ?? (value.ShiftStartsAtLocal is not null && value.ShiftEndsAtLocal is not null
            ? [new WorkingCalendarWindow(value.ShiftStartsAtLocal, value.ShiftEndsAtLocal)]
            : []);
        CalendarWindowsText = string.Join(Environment.NewLine, windows.Select(window => $"{window.StartsAtLocal}-{window.EndsAtLocal}"));
        CalendarBreakWindowsText = string.Join(Environment.NewLine,
            (value.BreakWindows ?? []).Select(window => $"{window.StartsAtLocal}-{window.EndsAtLocal}"));
        CalendarExceptionsText = string.Join(Environment.NewLine,
            (value.Exceptions ?? []).Select(FormatCalendarException));
        var usages = (value.Usages ?? ["machine", "setup_worker", "regular_worker", "qa_worker"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        CalendarUsageMachine = usages.Contains("machine");
        CalendarUsageSetupWorker = usages.Contains("setup_worker");
        CalendarUsageRegularWorker = usages.Contains("regular_worker");
        CalendarUsageQaWorker = usages.Contains("qa_worker");
        CalendarUseIsraeliHolidays = value.UseIsraeliHolidays;
        SetWorkdays(value.Workdays);
        OnPropertyChanged(nameof(CalendarFormHeading));
        OnPropertyChanged(nameof(IsCalendarEditable));
    }

    private void PopulateMachineForm(PlannerMachine value)
    {
        editingMachineId = value.MachineId;
        MachineNumber = value.Number;
        MachineName = value.Name;
        MachineProcessType = value.ProcessType;
        MachineAxisType = value.AxisType ?? string.Empty;
        MachineCapabilitiesText = string.Join(", ", value.Capabilities);
        selectedMachineTypeForMachine = MachineTypes.FirstOrDefault(type =>
            type.MachineTypeId == value.MachineTypeId)
            ?? MachineTypes.FirstOrDefault(type =>
                string.Equals(type.Name, value.ProcessType, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(SelectedMachineTypeForMachine));
        SelectedMachineCalendar = FindCalendar(value.WorkingCalendarId);
        MachinePicturePath = value.PicturePath ?? string.Empty;
        MachineIsActive = value.IsActive;
        MachineDisplayEnabled = value.DisplayEnabled;
        MachineRespectMasterCalendar = value.RespectMasterCalendar;
        MachineExecutionMode = value.ExecutionMode;
        MachineUsableToolPositions = value.UsableToolPositions?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        MachineRapidRateMillimetersPerMinute = value.RapidRateMillimetersPerMinute?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        MachineToolChangeTimeSeconds = value.ToolChangeTimeSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        MachineTimeFactor = value.MachineTimeFactor.ToString(CultureInfo.InvariantCulture);
        RebuildMachinePostprocessors(value.SupportedPostprocessorIds ?? []);
        ResetHaasForm();
        OnPropertyChanged(nameof(MachineFormHeading));
    }

    private void PopulateHaasConfiguration(HaasConnectionSettings value)
    {
        HaasHost = value.Host;
        HaasMacAddress = value.MacAddress;
        HaasMdcPort = value.MdcPort.ToString(CultureInfo.InvariantCulture);
        HaasMtConnectPort = value.MtConnectPort.ToString(CultureInfo.InvariantCulture);
        HaasDprntPort = value.DprntPort.ToString(CultureInfo.InvariantCulture);
        HaasTelemetryProvider = value.TelemetryProvider;
        HaasLocalNetShareEnabled = value.LocalNetShareEnabled;
        HaasLocalNetSharePath = value.LocalNetSharePath ?? string.Empty;
        HaasCredentialsReference = value.CredentialsReference ?? string.Empty;
        HaasPartCounterSource = value.PartCounterSource;
        HaasPollingIntervalMs = value.PollingIntervalMs.ToString(CultureInfo.InvariantCulture);
        HaasConnectionTimeoutMs = value.ConnectionTimeoutMs.ToString(CultureInfo.InvariantCulture);
        HaasEnabled = value.Enabled;
        haasSettingsVersion = value.Version;
    }

    private void ResetHaasForm()
    {
        HaasHost = string.Empty;
        HaasMacAddress = string.Empty;
        HaasMdcPort = "5051";
        HaasMtConnectPort = "8082";
        HaasDprntPort = "8080";
        HaasTelemetryProvider = "MTCONNECT";
        HaasLocalNetShareEnabled = false;
        HaasLocalNetSharePath = string.Empty;
        HaasCredentialsReference = string.Empty;
        HaasPartCounterSource = "Q500";
        HaasPollingIntervalMs = "2000";
        HaasConnectionTimeoutMs = "3000";
        HaasEnabled = false;
        haasSettingsVersion = 0;
        HaasDiagnostics = editingMachineId is null
            ? "Save the Machine before configuring Haas NGC."
            : "Load Haas configuration to view connection status.";
        HaasTimeline = "No Haas Bench events loaded.";
    }

    private void PopulateDowntimeForm(MachineDowntime value)
    {
        editingDowntimeId = value.DowntimeId;
        DowntimeType = value.DowntimeType;
        SelectedDowntimeMachine = Machines.FirstOrDefault(machine => machine.MachineId == value.MachineId);
        DowntimeStartsAt = value.StartsAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        DowntimeEndsAt = value.EndsAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
        DowntimeReason = value.Reason;
        DowntimeActor = value.DowntimeType == "breakdown" ? value.ReportedBy ?? string.Empty : value.PlannedBy ?? string.Empty;
        DowntimeRepairNote = value.RepairNote ?? string.Empty;
        DowntimeRestoredAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(DowntimeFormHeading));
    }

    private void PopulateMachineTypeForm(PlannerMachineType value)
    {
        editingMachineTypeId = value.MachineTypeId;
        MachineTypeName = value.Name;
        MachineTypeCapabilitiesText = string.Join(", ", value.Capabilities);
        OnPropertyChanged(nameof(MachineTypeFormHeading));
    }

    private void PopulatePostprocessorForm(PlannerPostprocessor value)
    {
        editingPostprocessorId = value.PostprocessorId;
        PostprocessorName = value.Name;
        PostprocessorDescription = value.Description ?? string.Empty;
        PostprocessorIsActive = value.IsActive;
        OnPropertyChanged(nameof(PostprocessorFormHeading));
        RaiseCommandStates();
    }

    private void PopulateResourceForm(PlannerResource value)
    {
        editingResourceId = value.ResourceId;
        ResourceEmployeeNumber = value.EmployeeNumber;
        ResourceFirstName = value.FirstName;
        ResourceLastName = value.LastName;
        ResourceRole = value.Role;
        RebuildResourceMachineSkills(value.Skills);
        SelectedResourceCalendar = FindCalendar(value.AssignedCalendarId);
        ResourcePhotoPath = value.PhotoPath ?? string.Empty;
        ResourceNotes = value.Notes ?? string.Empty;
        ResourceEmail = value.Email ?? string.Empty;
        ResourceToolLoadSecondsPerTool = value.ToolLoadSecondsPerTool.ToString(CultureInfo.InvariantCulture);
        ResourceFixtureAssemblySeconds = value.FixtureAssemblySeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ResourceFirstPartRunningSpeedPercent = value.FirstPartRunningSpeedPercent.ToString(CultureInfo.InvariantCulture);
        ResourceIsActive = value.IsActive;
        ResourceRespectMasterCalendar = value.RespectMasterCalendar;
        OnPropertyChanged(nameof(ResourceFormHeading));
        OnPropertyChanged(nameof(ResourceSaveActionText));
    }

    private void PopulateResourceExceptionForm(EmployeeCalendarException value)
    {
        editingResourceExceptionId = value.ExceptionId;
        ResourceExceptionDate = value.Date;
        ResourceExceptionType = value.ExceptionType;
        ResourceExceptionIsFullDay = value.IsFullDay;
        ResourceExceptionStartsAt = value.StartsAtLocal ?? "09:00";
        ResourceExceptionEndsAt = value.EndsAtLocal ?? "12:00";
        ResourceExceptionNote = value.Note ?? string.Empty;
        OnPropertyChanged(nameof(ResourceExceptionFormHeading));
    }

    private void PopulateIsraeliHolidayForm(IsraeliHoliday value)
    {
        editingIsraeliHolidayId = value.IsraeliHolidayId;
        IsraeliHolidayDate = value.Date;
        IsraeliHolidayName = value.Name;
        IsraeliHolidayStatus = value.Status;
        IsraeliHolidayStartsAt = value.StartsAtLocal ?? "08:00";
        IsraeliHolidayEndsAt = value.EndsAtLocal ?? "13:00";
        OnPropertyChanged(nameof(IsraeliHolidayFormHeading));
    }

    private void PopulateReportEmailSettings(ReportEmailSettingsResource resource)
    {
        var value = resource.Value;
        ReportSenderAddress = value.SenderAddress ?? string.Empty;
        ReportRecipientsText = string.Join(", ", value.Recipients);
        ReportSmtpHost = value.SmtpHost ?? string.Empty;
        ReportSmtpPort = value.SmtpPort?.ToString() ?? string.Empty;
        ReportUseSsl = value.UseSsl;
        DailyReportEnabled = value.DailyReportEnabled;
        DailyReportTimeLocal = value.DailyReportTimeLocal ?? "07:00";
        ReportTimeZoneId = value.TimeZoneId ?? "Asia/Jerusalem";
        WeeklyMaterialReportEnabled = value.WeeklyMaterialReportEnabled;
        WeeklyMaterialReportSendDay = value.WeeklyMaterialReportSendDay;
        WeeklyMaterialReportTimeLocal = value.WeeklyMaterialReportTimeLocal;
        WeeklyEmployeeEfficiencyEnabled = value.WeeklyEmployeeEfficiencyEnabled;
        WeeklyEmployeeEfficiencySendDay = value.WeeklyEmployeeEfficiencySendDay;
        WeeklyEmployeeEfficiencyTimeLocal = value.WeeklyEmployeeEfficiencyTimeLocal;
        reportEmailSettingsEntityTag = resource.EntityTag;
    }

    private void RefreshMachineFormLookups()
    {
        if (SelectedMachine is not null)
        {
            PopulateMachineForm(SelectedMachine);
        }
    }

    private bool TryCreateMachineValues(bool active, out MachineCreate? value)
    {
        value = null;
        if (!TryParseOptionalPositiveInt(MachineUsableToolPositions, out var usableTools)
            || !TryParseOptionalPositiveDouble(
                MachineRapidRateMillimetersPerMinute,
                allowZero: false,
                out var rapidRate)
            || !TryParseOptionalPositiveDouble(
                MachineToolChangeTimeSeconds,
                allowZero: true,
                out var toolChangeSeconds)
            || !double.TryParse(
                MachineTimeFactor,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var timeFactor)
            || !double.IsFinite(timeFactor)
            || timeFactor <= 0)
        {
            return false;
        }

        value = new MachineCreate(
            MachineNumber,
            MachineName,
            SelectedMachineTypeForMachine?.Name ?? MachineProcessType,
            NullIfBlank(MachineAxisType),
            ParseTokens(MachineCapabilitiesText),
            SelectedMachineCalendar!.WorkingCalendarId,
            active,
            MachineDisplayEnabled,
            NullIfBlank(MachinePicturePath),
            SelectedMachineTypeForMachine?.MachineTypeId,
            MachineRespectMasterCalendar,
            MachineExecutionMode,
            MachinePostprocessors.Where(option => option.IsSelected)
                .Select(option => option.PostprocessorId)
                .ToArray(),
            usableTools,
            rapidRate,
            toolChangeSeconds,
            timeFactor);
        return true;
    }

    private IReadOnlyList<string> SelectedWorkdays()
    {
        var selected = new List<string>();
        if (WorksSunday) selected.Add("sunday");
        if (WorksMonday) selected.Add("monday");
        if (WorksTuesday) selected.Add("tuesday");
        if (WorksWednesday) selected.Add("wednesday");
        if (WorksThursday) selected.Add("thursday");
        if (WorksFriday) selected.Add("friday");
        if (WorksSaturday) selected.Add("saturday");
        return selected;
    }

    private static IReadOnlyList<WorkingCalendarWindow> ParseCalendarWindows(string text, bool required)
    {
        var windows = new List<WorkingCalendarWindow>();
        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            windows.Add(ParseCalendarWindow(line));
        }
        if (required && windows.Count == 0) throw new FormatException();
        return windows;
    }

    private IReadOnlyList<WorkingCalendarException> ParseCalendarExceptions()
    {
        var exceptions = new List<WorkingCalendarException>();
        foreach (var line in CalendarExceptionsText.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts.Length > 4
                || !DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", out _))
                throw new FormatException();
            if (string.Equals(parts[1], "closed", StringComparison.OrdinalIgnoreCase))
            {
                exceptions.Add(new WorkingCalendarException(
                    parts[0], [], [], parts.Length >= 3 ? NullIfBlank(parts[2]) : null));
                continue;
            }

            var windows = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseCalendarWindow).ToArray();
            if (windows.Length == 0) throw new FormatException();
            var breaks = parts.Length >= 3 && parts[2] != "-"
                ? parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ParseCalendarWindow).ToArray()
                : [];
            exceptions.Add(new WorkingCalendarException(
                parts[0], windows, breaks, parts.Length == 4 ? NullIfBlank(parts[3]) : null));
        }
        return exceptions;
    }

    private static WorkingCalendarWindow ParseCalendarWindow(string value)
    {
        var parts = value.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TimeOnly.TryParseExact(parts[0], "HH:mm", out _)
            || (parts[1] != "24:00" && !TimeOnly.TryParseExact(parts[1], "HH:mm", out _)))
            throw new FormatException();
        return new WorkingCalendarWindow(parts[0], parts[1]);
    }

    private static string FormatCalendarException(WorkingCalendarException value)
    {
        if (value.Windows.Count == 0)
            return $"{value.Date} | closed{(value.Name is null ? string.Empty : $" | {value.Name}")}";
        var windows = string.Join(',', value.Windows.Select(window => $"{window.StartsAtLocal}-{window.EndsAtLocal}"));
        var breaks = value.BreakWindows.Count == 0
            ? "-"
            : string.Join(',', value.BreakWindows.Select(window => $"{window.StartsAtLocal}-{window.EndsAtLocal}"));
        return $"{value.Date} | {windows} | {breaks}{(value.Name is null ? string.Empty : $" | {value.Name}")}";
    }

    private IReadOnlyList<string> SelectedCalendarUsages()
    {
        var result = new List<string>();
        if (CalendarUsageMachine) result.Add("machine");
        if (CalendarUsageSetupWorker) result.Add("setup_worker");
        if (CalendarUsageRegularWorker) result.Add("regular_worker");
        if (CalendarUsageQaWorker) result.Add("qa_worker");
        return result;
    }

    private void SetWorkdays(IReadOnlyList<string> workdays)
    {
        var selected = workdays.ToHashSet(StringComparer.OrdinalIgnoreCase);
        WorksSunday = selected.Contains(DayTokens[0]);
        WorksMonday = selected.Contains(DayTokens[1]);
        WorksTuesday = selected.Contains(DayTokens[2]);
        WorksWednesday = selected.Contains(DayTokens[3]);
        WorksThursday = selected.Contains(DayTokens[4]);
        WorksFriday = selected.Contains(DayTokens[5]);
        WorksSaturday = selected.Contains(DayTokens[6]);
    }

    private async Task<bool> TryMutationAsync(Func<Task> mutation)
    {
        IsBusy = true;
        try
        {
            await mutation();
            return true;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            StatusMessage = FriendlyMessage(exception);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task<bool> TryDeleteAsync(Func<Task> delete) => TryMutationAsync(delete);

    private async Task RunHaasReadAsync(Func<Task> action)
    {
        if (apiClient is null || SelectedMachine is null) return;
        IsBusy = true;
        try { await action(); }
        catch (Exception exception) when (IsExpected(exception))
        {
            HaasDiagnostics = FriendlyMessage(exception);
        }
        finally { IsBusy = false; }
    }

    private bool CanRead() => apiClient is not null && !IsBusy;
    private bool CanManage() => apiClient is not null && isEditor && !IsBusy;
    private bool CanSaveCalendar() => CanManage() && IsCalendarEditable;
    private bool CanDeleteCalendar() => CanManage() && SelectedCalendar is not null;
    private bool CanSetSetupCalendar() => CanManage() && SelectedSetupCalendar is not null;
    private bool CanClearSetupCalendar() => CanManage() && SelectedSetupCalendar is not null;
    private bool CanSetMasterCalendar() => CanManage() && SelectedMasterCalendar is not null;
    private bool CanClearMasterCalendar() => CanManage() && SelectedMasterCalendar is not null;
    private bool CanSaveMachine() => CanManage() && SelectedMachineCalendar is not null;
    private bool CanDeactivateMachine() => CanManage() && SelectedMachine is { IsActive: true };
    private bool CanDeleteMachine() => CanManage() && SelectedMachine is not null;
    private bool CanReadHaas() => CanRead() && SelectedMachine is not null;
    private bool CanManageHaas() => CanManage() && SelectedMachine is not null;
    private bool CanSaveDowntime() => CanManage() && SelectedDowntimeMachine is not null
        && (editingDowntimeId is null || IsPlannedMaintenance);
    private bool CanRestoreBreakdown() => CanManage()
        && SelectedDowntime is { DowntimeType: "breakdown", Status: "active" };
    private bool CanSaveMachineType() => CanManage();
    private bool CanDeleteMachineType() => CanManage() && SelectedMachineType is not null;
    private bool CanSavePostprocessor() => CanManage();
    private bool CanDeletePostprocessor() => CanManage() && SelectedPostprocessor is not null;
    private bool CanEditSelectedResource() => CanManage() && SelectedResource is not null;
    private bool CanSaveResource() => CanManage();
    private bool CanDeleteResource() => CanManage() && SelectedResource is not null;
    private bool CanReadResourceExceptions() => CanRead() && SelectedResource is not null;
    private bool CanManageResourceExceptions() => CanManage() && SelectedResource is not null;
    private bool CanDeleteResourceException() => CanManageResourceExceptions() && SelectedResourceException is not null;
    private bool CanSaveIsraeliHoliday() => CanManage();
    private bool CanDeleteIsraeliHoliday() => CanManage() && SelectedIsraeliHoliday is not null;
    private bool CanSaveReportEmailSettings() => CanManage() && reportEmailSettingsEntityTag is not null;

    private void RaiseCommandStates()
    {
        UpdateConnectionCommandStates();
        RefreshMasterDataCommand.RaiseCanExecuteChanged();
        NewCalendarCommand.RaiseCanExecuteChanged();
        SaveCalendarCommand.RaiseCanExecuteChanged();
        DeleteCalendarCommand.RaiseCanExecuteChanged();
        SetSetupCalendarCommand.RaiseCanExecuteChanged();
        ClearSetupCalendarCommand.RaiseCanExecuteChanged();
        SetMasterCalendarCommand.RaiseCanExecuteChanged();
        ClearMasterCalendarCommand.RaiseCanExecuteChanged();
        NewMachineCommand.RaiseCanExecuteChanged();
        SaveMachineCommand.RaiseCanExecuteChanged();
        DeactivateMachineCommand.RaiseCanExecuteChanged();
        DeleteMachineCommand.RaiseCanExecuteChanged();
        LoadHaasConfigurationCommand.RaiseCanExecuteChanged();
        SaveHaasConfigurationCommand.RaiseCanExecuteChanged();
        LoadVerificationConfigurationCommand.RaiseCanExecuteChanged();
        SaveVerificationConfigurationCommand.RaiseCanExecuteChanged();
        GenerateOffsetLoaderReleaseCommand.RaiseCanExecuteChanged();
        InvalidateVerificationCommand.RaiseCanExecuteChanged();
        RevokeCurrentOffsetLoaderCommand.RaiseCanExecuteChanged();
        TestHaasConnectionCommand.RaiseCanExecuteChanged();
        TestHaasMtConnectCommand.RaiseCanExecuteChanged();
        TestHaasMdcCommand.RaiseCanExecuteChanged();
        TestHaasNetShareCommand.RaiseCanExecuteChanged();
        RefreshHaasMonitorCommand.RaiseCanExecuteChanged();
        ReconnectCncCommand.RaiseCanExecuteChanged();
        NewPlannedMaintenanceCommand.RaiseCanExecuteChanged();
        ReportBreakdownCommand.RaiseCanExecuteChanged();
        SaveDowntimeCommand.RaiseCanExecuteChanged();
        RestoreBreakdownCommand.RaiseCanExecuteChanged();
        NewMachineTypeCommand.RaiseCanExecuteChanged();
        SaveMachineTypeCommand.RaiseCanExecuteChanged();
        DeleteMachineTypeCommand.RaiseCanExecuteChanged();
        NewPostprocessorCommand.RaiseCanExecuteChanged();
        SavePostprocessorCommand.RaiseCanExecuteChanged();
        DeletePostprocessorCommand.RaiseCanExecuteChanged();
        NewResourceCommand.RaiseCanExecuteChanged();
        EditSelectedResourceCommand.RaiseCanExecuteChanged();
        SaveResourceCommand.RaiseCanExecuteChanged();
        DeleteResourceCommand.RaiseCanExecuteChanged();
        RefreshResourceExceptionsCommand.RaiseCanExecuteChanged();
        NewResourceExceptionCommand.RaiseCanExecuteChanged();
        SaveResourceExceptionCommand.RaiseCanExecuteChanged();
        DeleteResourceExceptionCommand.RaiseCanExecuteChanged();
        NewIsraeliHolidayCommand.RaiseCanExecuteChanged();
        SaveIsraeliHolidayCommand.RaiseCanExecuteChanged();
        DeleteIsraeliHolidayCommand.RaiseCanExecuteChanged();
        SynchronizeIsraeliHolidaysCommand.RaiseCanExecuteChanged();
        SaveReportEmailSettingsCommand.RaiseCanExecuteChanged();
        SendWeeklyMaterialReportNowCommand.RaiseCanExecuteChanged();
        SendWeeklyEmployeeEfficiencyReportNowCommand.RaiseCanExecuteChanged();
    }

    private void ClearCollections()
    {
        WorkingCalendars.Clear();
        Machines.Clear();
        ResourceMachineSkills.Clear();
        Downtimes.Clear();
        MachineTypes.Clear();
        Postprocessors.Clear();
        MachinePostprocessors.Clear();
        Resources.Clear();
        ResourceExceptions.Clear();
        IsraeliHolidays.Clear();
        selectedCalendar = null;
        selectedSetupCalendar = null;
        selectedMasterCalendar = null;
        selectedMachine = null;
        selectedDowntime = null;
        selectedMachineType = null;
        selectedPostprocessor = null;
        selectedResource = null;
        selectedResourceException = null;
        selectedIsraeliHoliday = null;
        editingCalendarId = null;
        editingMachineId = null;
        editingDowntimeId = null;
        editingMachineTypeId = null;
        editingPostprocessorId = null;
        editingResourceId = null;
        editingResourceExceptionId = null;
        editingIsraeliHolidayId = null;
        reportEmailSettingsEntityTag = null;
        OnPropertyChanged(nameof(SelectedCalendar));
        OnPropertyChanged(nameof(SelectedSetupCalendar));
        OnPropertyChanged(nameof(SelectedMasterCalendar));
        OnPropertyChanged(nameof(SelectedMachine));
        OnPropertyChanged(nameof(SelectedDowntime));
        OnPropertyChanged(nameof(SelectedMachineType));
        OnPropertyChanged(nameof(SelectedPostprocessor));
        OnPropertyChanged(nameof(SelectedResource));
        OnPropertyChanged(nameof(SelectedResourceException));
        OnPropertyChanged(nameof(SelectedIsraeliHoliday));
    }

    private WorkingCalendar? FindCalendar(string? id) => id is null
        ? null
        : WorkingCalendars.FirstOrDefault(value => value.WorkingCalendarId == id);

    private static string CalendarEntityTag(WorkingCalendar value) =>
        $"\"working-calendar:{value.WorkingCalendarId}:v{value.Version}\"";

    private static string MachineEntityTag(PlannerMachine value) =>
        $"\"machine:{value.MachineId}:v{value.Version}\"";

    private static bool TryOptionalInt(string value, out int? parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = null;
            return true;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            parsed = number;
            return true;
        }

        parsed = null;
        return false;
    }

    private static string DowntimeEntityTag(MachineDowntime value) =>
        $"\"downtime:{value.DowntimeId}:v{value.Version}\"";

    private static string MachineTypeEntityTag(PlannerMachineType value) =>
        $"\"machine-type:{value.MachineTypeId}:v{value.Version}\"";

    private static string PostprocessorEntityTag(PlannerPostprocessor value) =>
        $"\"postprocessor:{value.PostprocessorId}:v{value.Version}\"";

    private void RebuildMachinePostprocessors(IReadOnlyList<string> selectedIds)
    {
        var selected = selectedIds.ToHashSet(StringComparer.Ordinal);
        MachinePostprocessors.Clear();
        foreach (var postprocessor in Postprocessors.Where(value => value.IsActive))
        {
            MachinePostprocessors.Add(new MachinePostprocessorOption(
                postprocessor.PostprocessorId,
                postprocessor.Name,
                selected.Contains(postprocessor.PostprocessorId)));
        }
    }

    private static string ResourceEntityTag(PlannerResource value) =>
        $"\"resource:{value.ResourceId}:v{value.Version}\"";

    private void RebuildResourceMachineSkills(IReadOnlyList<string> selectedSkills)
    {
        var selected = selectedSkills.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectsEveryMachine = selected.Contains("*");
        ResourceMachineSkills.Clear();
        foreach (var machine in Machines)
        {
            var legacyTokens = new[] { machine.Number, machine.Name, machine.ProcessType }
                .Concat(string.IsNullOrWhiteSpace(machine.AxisType) ? [] : [machine.AxisType])
                .Concat(machine.Capabilities);
            var isSelected = selectsEveryMachine
                || selected.Contains(machine.MachineId)
                || legacyTokens.Any(selected.Contains);
            ResourceMachineSkills.Add(new ResourceMachineSkillOption(
                machine.MachineId,
                machine.IsActive ? machine.DisplayName : $"{machine.DisplayName} (inactive)",
                isSelected));
        }
    }

    private static string ResourceExceptionEntityTag(EmployeeCalendarException value) =>
        $"\"employee-exception:{value.ExceptionId}:v{value.Version}\"";

    private static string IsraeliHolidayEntityTag(IsraeliHoliday value) =>
        $"\"israeli-holiday:{value.IsraeliHolidayId}:v{value.Version}\"";

    private static IReadOnlyList<string> ParseTokens(string value) => value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool TryParseOptionalPositiveInt(string value, out int? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            return false;
        }

        result = parsed;
        return true;
    }

    private static bool TryParseOptionalPositiveDouble(
        string value,
        bool allowZero,
        out double? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed)
            || parsed < 0
            || (!allowZero && parsed == 0))
        {
            return false;
        }

        result = parsed;
        return true;
    }

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryParseLocalInstant(string value, out DateTimeOffset result)
    {
        if (!DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var local))
        {
            result = default;
            return false;
        }
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        result = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        return true;
    }

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> values)
    {
        destination.Clear();
        foreach (var value in values)
        {
            destination.Add(value);
        }
    }

    private static bool IsExpected(Exception exception) => exception is
        PlannerApiException or PlannerProtocolException or HttpRequestException or TaskCanceledException;

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        TaskCanceledException => "The Server did not respond before the client timeout.",
        HttpRequestException => "The configured Server could not be reached.",
        PlannerApiException api => $"{api.Message} ({api.Code})",
        _ => exception.Message
    };

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

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class ResourceMachineSkillOption : INotifyPropertyChanged
{
    private bool isSelected;

    internal ResourceMachineSkillOption(string machineId, string displayName, bool isSelected)
    {
        MachineId = machineId;
        DisplayName = displayName;
        this.isSelected = isSelected;
    }

    public string MachineId { get; }
    public string DisplayName { get; }
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class MachinePostprocessorOption : INotifyPropertyChanged
{
    private bool isSelected;

    internal MachinePostprocessorOption(string postprocessorId, string displayName, bool isSelected)
    {
        PostprocessorId = postprocessorId;
        DisplayName = displayName;
        this.isSelected = isSelected;
    }

    public string PostprocessorId { get; }
    public string DisplayName { get; }
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
