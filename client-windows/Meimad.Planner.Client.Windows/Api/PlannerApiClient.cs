using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meimad.Planner.Client.Windows.Api;

internal interface IPlannerApiClient : IDisposable
{
    Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken = default);

    Task<ServerMaintenanceCatalog> GetServerMaintenanceAsync(
        string clientId,
        string userId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<CollectedDataPreview> PreviewCollectedDataAsync(
        CollectedDataPreviewRequest preview,
        string clientId,
        string userId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<CollectedDataPurgeResult> PurgeCollectedDataAsync(
        CollectedDataPurgeRequest purge,
        string clientId,
        string userId,
        long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<DatabaseBackupDownload> DownloadDatabaseBackupAsync(
        string destinationFolder,
        string clientId,
        string userId,
        long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<EditModeStatus> GetEditModeAsync(
        string clientId,
        CancellationToken cancellationToken = default);

    Task<EditModeStatus> RequestEditAsync(
        string clientId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<EditModeStatus> ReleaseEditAsync(
        string clientId,
        long generation,
        CancellationToken cancellationToken = default);

    Task<EditModeStatus> DecideTransferAsync(
        string clientId,
        long generation,
        string requestId,
        bool release,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlannerCase>> ListCasesAsync(
        CaseQuery query,
        CancellationToken cancellationToken = default);

    Task<CaseResource> GetCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<CaseResource> CreateCaseAsync(
        CaseUpdate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<CaseResource> UpdateCaseAsync(
        string caseId,
        CaseUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CaseOperation>> ListCaseOperationsAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<CaseOperation> CreateCaseOperationAsync(
        string caseId,
        CaseOperationCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<CaseOperation> UpdateCaseOperationAsync(
        string caseId,
        string operationId,
        CaseOperationUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<PlannerOrder>> ListOrdersAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<PlannerOrder> CreateOrderAsync(
        OrderCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<PlannerGCodeCatalog> GetOperationGCodeAsync(
        string caseId,
        string operationId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<PlannerGCodeRelease> ReleaseGCodeAsync(
        string caseId,
        string operationId,
        GCodeReleaseCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<PlannerOrder> UpdateOrderAsync(
        string orderId,
        OrderUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<ProductionBatch>> ListBatchesAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<ProductionBatch> CreateBatchAsync(
        ProductionBatchCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ProductionBatch> UpdateBatchAsync(
        string batchId,
        ProductionBatchUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<BatchMaterialReconciliation> GetBatchMaterialAsync(
        string batchId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<VerifiedMaterialReceipt> CreateVerifiedMaterialReceiptAsync(
        VerifiedMaterialReceiptCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<BatchMaterialReconciliation> ReplaceBatchMaterialReservationsAsync(
        string batchId,
        MaterialReservationsReplace update,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<byte[]?> GetCasePreviewAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<PlannerMachine> CreateMachineAsync(
        MachineCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<PlannerMachine>> ListMachinesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlannerMachine>>([]);

    Task<IReadOnlyList<UserTerminal>> ListUserTerminalsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserTerminal>>([]);

    Task<UserTerminal> CreateUserTerminalAsync(
        CreateUserTerminalRequest request, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<UserTerminal> UpdateUserTerminalAsync(
        string deviceId, UpdateUserTerminalRequest request, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task DeleteUserTerminalAsync(string deviceId, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<IReadOnlyList<QcQueueItem>> ListQcQueueAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<QcQueueItem>>([]);

    Task<IReadOnlyList<PreparationQueueItem>> ListPreparationQueueAsync(
        string stage,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PreparationQueueItem>>([]);

    Task<ProductionPackageInfo> CreateProductionPackageAsync(
        string batchOperationId, string clientId, string userId,
        string toolOffsetMode = "MEASURED",
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<ProductionPackageInfo?> GetCurrentProductionPackageAsync(
        string batchOperationId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<byte[]> ReadProductionPackageArtifactAsync(
        string batchOperationId, string artifactId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<string> ReadGCodeFileTextAsync(
        string caseId, string caseOperationId, string releaseId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<byte[]> ReadToolTableFileAsync(
        string caseId, string caseOperationId, string releaseId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<QcDecisionResult> DecideQcAsync(
        string productionRunId, QcDecisionRequest request,
        string clientId, string userId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<MachineResource> GetMachineAsync(string machineId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<MachineResource> UpdateMachineAsync(
        string machineId, MachineCreate update, string entityTag,
        string clientId, long editGeneration, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task DeleteCaseAsync(string caseId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task DeleteCaseOperationAsync(string caseId, string operationId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task DeleteOrderAsync(string orderId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task DeleteBatchAsync(string batchId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task DeleteMachineAsync(string machineId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<IReadOnlyList<MachineDowntime>> ListDowntimesAsync(
        string? machineId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MachineDowntime>>([]);
    Task<MachineDowntime> CreateDowntimeAsync(
        MachineDowntimeCreate create, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<MachineDowntimeResource> UpdatePlannedMaintenanceAsync(
        string downtimeId, PlannedMaintenanceUpdate update, string entityTag,
        string clientId, long editGeneration, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<HaasConnectionSettings> GetHaasConnectionAsync(
        string machineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<HaasConnectionSettings> UpdateHaasConnectionAsync(
        string machineId, HaasConnectionUpdate update, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<HaasConnectionTest> TestHaasMtConnectAsync(
        string machineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<HaasConnectionTest> TestHaasMdcAsync(
        string machineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<HaasConnectionTest> TestHaasNetShareAsync(
        string machineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<HaasMachineMonitor> GetHaasMonitorAsync(
        string machineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<CncVerificationSettings> GetCncVerificationSettingsAsync(
        string machineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<CncVerificationSettings> UpdateCncVerificationSettingsAsync(
        string machineId, CncVerificationSettingsUpdate update, string clientId,
        long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<OffsetLoaderRelease> CreateOffsetLoaderReleaseAsync(
        string productionRunId, CreateOffsetLoaderReleaseRequest request,
        string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<CncRecoveryResult> InvalidateCncVerificationAsync(
        string productionRunId, CncRecoveryRequest request,
        string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<CncRecoveryResult> RevokeCurrentOffsetLoaderAsync(
        string productionRunId, CncRecoveryRequest request,
        string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<IReadOnlyList<CncAdapterDefinition>> ListCncAdaptersAsync(
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CncAdapterDefinition>>([]);
    Task ReconnectCncAsync(
        string machineId, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<MachineDowntimeResource> RestoreBreakdownAsync(
        string downtimeId, BreakdownRestore restore, string entityTag,
        string clientId, long editGeneration, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<WorkingCalendar>> ListWorkingCalendarsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkingCalendar>>([]);

    Task<WorkingCalendar> CreateWorkingCalendarAsync(
        WorkingCalendarCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<WorkingCalendarResource> GetWorkingCalendarAsync(
        string workingCalendarId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<WorkingCalendarResource> UpdateWorkingCalendarAsync(
        string workingCalendarId,
        WorkingCalendarUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task DeleteWorkingCalendarAsync(
        string workingCalendarId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<SetupCalendarSelection> GetSetupCalendarAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SetupCalendarSelection(null, null));

    Task<SetupCalendarSelection> SetSetupCalendarAsync(
        string workingCalendarId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task ClearSetupCalendarAsync(
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<SetupCalendarSelection> GetMasterCalendarAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SetupCalendarSelection(null, null));

    Task<SetupCalendarSelection> SetMasterCalendarAsync(
        string workingCalendarId, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task ClearMasterCalendarAsync(string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<IReadOnlyList<PlannerMachineType>> ListMachineTypesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlannerMachineType>>([]);

    Task<MachineTypeResource> GetMachineTypeAsync(
        string machineTypeId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<PlannerMachineType> CreateMachineTypeAsync(
        MachineTypeCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<MachineTypeResource> UpdateMachineTypeAsync(
        string machineTypeId,
        MachineTypeUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task DeleteMachineTypeAsync(
        string machineTypeId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<PlannerPostprocessor>> ListPostprocessorsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlannerPostprocessor>>([]);

    Task<PostprocessorResource> GetPostprocessorAsync(
        string postprocessorId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<PlannerPostprocessor> CreatePostprocessorAsync(
        PostprocessorCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<PostprocessorResource> UpdatePostprocessorAsync(
        string postprocessorId,
        PostprocessorUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task DeletePostprocessorAsync(
        string postprocessorId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<PlannerResource>> ListResourcesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlannerResource>>([]);

    Task<IReadOnlyList<PlannerSkill>> ListSkillsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlannerSkill>>([]);
    Task<PlannerSkill> CreateSkillAsync(SkillCreate create, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<PlannerSkill> UpdateSkillAsync(string id,SkillUpdate update,string clientId,long editGeneration,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    Task DeleteSkillAsync(string id,int version,string clientId,long editGeneration,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    Task<IReadOnlyList<PlannerWorkstationType>> ListWorkstationTypesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlannerWorkstationType>>([]);
    Task<PlannerWorkstationType> CreateWorkstationTypeAsync(WorkstationTypeCreate create, string clientId,
        long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<PlannerWorkstationType> UpdateWorkstationTypeAsync(string id,WorkstationTypeUpdate update,string clientId,long editGeneration,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    Task DeleteWorkstationTypeAsync(string id,int version,string clientId,long editGeneration,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    Task<IReadOnlyList<PlannerWorkstation>> ListWorkstationsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlannerWorkstation>>([]);
    Task<PlannerWorkstation> CreateWorkstationAsync(WorkstationCreate create, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<PlannerWorkstation> UpdateWorkstationAsync(string id,WorkstationUpdate update,string clientId,long editGeneration,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    Task DeleteWorkstationAsync(string id,int version,string clientId,long editGeneration,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    Task<IReadOnlyList<PlannerExternalResource>> ListExternalResourcesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlannerExternalResource>>([]);
    Task<PlannerExternalResource> CreateExternalResourceAsync(ExternalResourceCreate create, string clientId,
        long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<PlannerExternalResource> UpdateExternalResourceAsync(string id,ExternalResourceUpdate update,string clientId,long editGeneration,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    Task DeleteExternalResourceAsync(string id,int version,string clientId,long editGeneration,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    Task<PlannerEmployeeSkills> GetEmployeeSkillsAsync(string employeeId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PlannerEmployeeSkills(employeeId, []));
    Task<PlannerEmployeeSkills> SetEmployeeSkillsAsync(string employeeId, EmployeeSkillsUpdate update,
        string clientId, long editGeneration, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<PlannerResource> CreateResourceAsync(
        ResourceCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ResourceResource> GetResourceAsync(
        string resourceId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ResourceResource> UpdateResourceAsync(
        string resourceId,
        ResourceUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task DeleteResourceAsync(
        string resourceId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<EmployeeCalendarException>> ListEmployeeExceptionsAsync(
        string resourceId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EmployeeCalendarException>>([]);

    Task<EmployeeCalendarException> CreateEmployeeExceptionAsync(
        string resourceId, EmployeeCalendarExceptionCreate create,
        string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<EmployeeCalendarExceptionResource> UpdateEmployeeExceptionAsync(
        string resourceId, string exceptionId, EmployeeCalendarExceptionUpdate update,
        string entityTag, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task DeleteEmployeeExceptionAsync(
        string resourceId, string exceptionId, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<EmployeeAvailability> GetEmployeeAvailabilityAsync(
        string resourceId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<IReadOnlyList<IsraeliHoliday>> ListIsraeliHolidaysAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IsraeliHoliday>>([]);

    Task<IsraeliHoliday> CreateIsraeliHolidayAsync(
        IsraeliHolidayCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IsraeliHolidayResource> GetIsraeliHolidayAsync(
        string israeliHolidayId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IsraeliHolidayResource> UpdateIsraeliHolidayAsync(
        string israeliHolidayId,
        IsraeliHolidayUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task DeleteIsraeliHolidayAsync(
        string israeliHolidayId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IsraeliHolidaySyncResult> SynchronizeIsraeliHolidaysAsync(
        IsraeliHolidaySyncRequest request, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<ReportEmailSettingsResource> GetReportEmailSettingsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ReportEmailSettingsResource(
            new ReportEmailSettings(null, [], null, null, true, false, null, "Asia/Jerusalem", 0, DateTimeOffset.MinValue),
            "\"report-email-settings:1:v0\""));

    Task<ReportEmailSettingsResource> UpdateReportEmailSettingsAsync(
        ReportEmailSettingsUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<WeeklyMaterialReport> SendWeeklyMaterialReportAsync(
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<WeeklyEmployeeEfficiencyReport> SendWeeklyEmployeeEfficiencyReportAsync(
        string clientId, long editGeneration, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<byte[]?> GetMachinePictureAsync(
        string machineId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);

    Task<PlanningBoardSnapshot> GetPlanningBoardAsync(
        CancellationToken cancellationToken = default);

    Task<ProductionRunResource> CreateProductionRunAsync(
        ProductionRunCreate value, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<PlannerProductionReadiness> GetProductionReadinessAsync(
        string batchOperationId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<PlannerProductionReadiness> UpdateProductionReadinessInputsAsync(
        string batchOperationId,
        ProductionReadinessInputUpdate update,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<TimelineSnapshot> GetTimelineAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CaseComponent>> ListCaseComponentsAsync(
        string caseId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CaseComponent>>([]);

    Task<IReadOnlyList<CaseComponent>> ListCaseWhereUsedAsync(
        string caseId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CaseComponent>>([]);

    Task<CaseComponent> CreateCaseComponentAsync(
        string caseId, CaseComponentCreate create, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<CaseComponent> UpdateCaseComponentAsync(
        string caseId, string componentId, CaseComponentUpdate update, string entityTag,
        string clientId, long editGeneration, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task DeactivateCaseComponentAsync(
        string caseId, string componentId, string entityTag, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<ComponentDemandPreview> PreviewCaseComponentDemandAsync(
        string caseId, double quantity, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<DerivedCaseOrder>> ListDerivedCaseOrdersAsync(
        string caseId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DerivedCaseOrder>>([]);

    /// <summary>
    /// Allows deterministic Timeline requests in diagnostics and tests. Normal
    /// interactive refreshes omit <paramref name="asOf"/> and use Server time.
    /// </summary>
    Task<TimelineSnapshot> GetTimelineAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset? asOf,
        CancellationToken cancellationToken = default) =>
        GetTimelineAsync(from, to, cancellationToken);

    Task AssignOrMoveOperationAsync(
        string batchOperationId,
        string machineId,
        int backlogPosition,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default);

    Task AssignOrMoveOperationAsync(
        string batchOperationId,
        string machineId,
        int backlogPosition,
        string clientId,
        long editGeneration,
        MachineAssignmentCompatibilityOverride compatibilityOverride,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task UnassignOperationAsync(
        string batchOperationId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default);

    Task<MachineAssignment> ChangeMachineAssignmentPlanningModeAsync(
        string machineAssignmentId,
        int assignmentVersion,
        string planningMode,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<BatchOperationExecution> ChangeOperationExecutionAsync(
        string batchOperationId,
        string action,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<BatchOperationExecution> PauseOperationAsync(
        string batchOperationId, OperationPauseRequest pause, string clientId,
        long editGeneration, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<ManualOperationReport> RecordManualOperationReportAsync(
        string batchOperationId, string reportType, int? partTimeSeconds,
        string clientId, long editGeneration, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<LegacyWorkingPlanPreview> PreviewLegacyWorkingPlanAsync(
        Stream workbook,
        string fileName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<LegacyWorkingPlanPreview> PreviewLegacyWorkingPlanAsync(
        Stream workbook,
        string fileName,
        string? planningSheet,
        string? openOrdersSheet,
        IReadOnlyList<LegacyImportColumnMapping>? columnMappings,
        CancellationToken cancellationToken = default) =>
        PreviewLegacyWorkingPlanAsync(workbook, fileName, cancellationToken);

    Task<LegacyWorkingPlanCommitReceipt> CommitLegacyWorkingPlanAsync(
        LegacyWorkingPlanCommit commit,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal interface IPlannerApiClientFactory
{
    IPlannerApiClient Create(Uri serverBaseUri);
}

internal sealed class PlannerApiClientFactory : IPlannerApiClientFactory
{
    public IPlannerApiClient Create(Uri serverBaseUri) => new PlannerApiClient(
        new HttpClient
        {
            BaseAddress = serverBaseUri,
            Timeout = TimeSpan.FromSeconds(10)
        },
        new HttpClient
        {
            BaseAddress = serverBaseUri,
            Timeout = TimeSpan.FromSeconds(90)
        });
}

internal sealed class PlannerApiClient : IPlannerApiClient
{
    private const string ClientIdHeader = "X-Meimad-Client-Id";
    private const string UserIdHeader = "X-Meimad-User-Id";
    private const string EditGenerationHeader = "X-Meimad-Edit-Generation";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly HttpClient importHttpClient;
    private readonly bool ownsSeparateImportClient;

    internal PlannerApiClient(HttpClient httpClient, HttpClient? importHttpClient = null)
    {
        this.httpClient = httpClient;
        this.importHttpClient = importHttpClient ?? httpClient;
        ownsSeparateImportClient = importHttpClient is not null;
    }

    public async Task<ServerHealth> GetHealthAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("health", cancellationToken);
        var dto = await ReadSuccessAsync<ServerHealthDto>(response, cancellationToken);
        return new ServerHealth(
            Required(dto.Status, "health status"),
            Required(dto.Service, "service name"),
            Required(dto.Version, "service version"),
            dto.ServerTimeUtc);
    }

    public async Task<EditModeStatus> GetEditModeAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/edit-mode", clientId);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return Map(await ReadSuccessAsync<EditModeDto>(response, cancellationToken));
    }

    public async Task<EditModeStatus> RequestEditAsync(
        string clientId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/edit-mode/requests", clientId);
        request.Headers.Add(UserIdHeader, userId);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return Map(await ReadSuccessAsync<EditModeDto>(response, cancellationToken));
    }

    public async Task<EditModeStatus> ReleaseEditAsync(
        string clientId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/edit-mode/release", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return Map(await ReadSuccessAsync<EditModeDto>(response, cancellationToken));
    }

    public async Task<EditModeStatus> DecideTransferAsync(
        string clientId,
        long generation,
        string requestId,
        bool release,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/v1/edit-mode/requests/{Uri.EscapeDataString(requestId)}/decision",
            clientId);
        request.Headers.Add(
            EditGenerationHeader,
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(new
        {
            decision = release ? "release" : "reject"
        });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return Map(await ReadSuccessAsync<EditModeDto>(response, cancellationToken));
    }

    public async Task<IReadOnlyList<PlannerCase>> ListCasesAsync(
        CaseQuery query,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>();
        AddQueryParameter(parameters, "search", query.Search);
        AddQueryParameter(parameters, "customer", query.Customer);
        if (query.IsActive.HasValue)
        {
            parameters.Add($"isActive={query.IsActive.Value.ToString().ToLowerInvariant()}");
        }
        AddQueryParameter(parameters, "sort", query.Sort);

        var path = "api/v1/cases" + (parameters.Count == 0 ? string.Empty : "?" + string.Join("&", parameters));
        using var response = await httpClient.GetAsync(path, cancellationToken);
        return (await ReadSuccessAsync<ListResponse<PlannerCase>>(response, cancellationToken)).Items;
    }

    public async Task<CaseResource> GetCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}",
            cancellationToken);
        var value = await ReadSuccessAsync<PlannerCase>(response, cancellationToken);
        return new CaseResource(value, RequiredEntityTag(response));
    }

    public async Task<CaseResource> CreateCaseAsync(
        CaseUpdate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/cases", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var value = await ReadSuccessAsync<PlannerCase>(response, cancellationToken);
        return new CaseResource(value, RequiredEntityTag(response));
    }

    public async Task<CaseResource> UpdateCaseAsync(
        string caseId,
        CaseUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Patch,
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}",
            clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var value = await ReadSuccessAsync<PlannerCase>(response, cancellationToken);
        return new CaseResource(value, RequiredEntityTag(response));
    }

    public async Task<IReadOnlyList<CaseOperation>> ListCaseOperationsAsync(
        string caseId,
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<CaseOperation>(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations",
            cancellationToken);

    public async Task<CaseOperation> CreateCaseOperationAsync(
        string caseId,
        CaseOperationCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations",
            clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<CaseOperation>(response, cancellationToken);
    }

    public async Task<CaseOperation> UpdateCaseOperationAsync(
        string caseId,
        string operationId,
        CaseOperationUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Patch,
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(operationId)}",
            clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<CaseOperation>(response, cancellationToken);
    }

    public async Task<ServerMaintenanceCatalog> GetServerMaintenanceAsync(
        string clientId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateMaintenanceRequest(
            HttpMethod.Get, "api/v1/server-maintenance/database", clientId, userId);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<ServerMaintenanceCatalog>(response, cancellationToken);
    }

    public async Task<CollectedDataPreview> PreviewCollectedDataAsync(
        CollectedDataPreviewRequest preview,
        string clientId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateMaintenanceRequest(
            HttpMethod.Post, "api/v1/server-maintenance/collected-data/preview", clientId, userId);
        request.Content = JsonContent.Create(preview, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<CollectedDataPreview>(response, cancellationToken);
    }

    public async Task<CollectedDataPurgeResult> PurgeCollectedDataAsync(
        CollectedDataPurgeRequest purge,
        string clientId,
        string userId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateMaintenanceRequest(
            HttpMethod.Post, "api/v1/server-maintenance/collected-data/purge", clientId, userId, editGeneration);
        request.Content = JsonContent.Create(purge, options: JsonOptions);
        using var response = await importHttpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<CollectedDataPurgeResult>(response, cancellationToken);
    }

    public async Task<DatabaseBackupDownload> DownloadDatabaseBackupAsync(
        string destinationFolder,
        string clientId,
        string userId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder))
            throw new ArgumentException("A backup destination folder is required.", nameof(destinationFolder));
        Directory.CreateDirectory(destinationFolder);

        using var request = CreateMaintenanceRequest(
            HttpMethod.Post, "api/v1/server-maintenance/backups/download", clientId, userId, editGeneration);
        using var response = await importHttpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            await ThrowApiErrorAsync(response, cancellationToken);

        var serverFileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? $"meimad-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db";
        var safeFileName = Path.GetFileName(serverFileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
            throw new PlannerProtocolException("Server returned an invalid backup file name.");
        var localPath = UniquePath(destinationFolder, safeFileName);
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(
                localPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long length = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hasher.AppendData(buffer, 0, read);
                length += read;
            }
            await target.FlushAsync(cancellationToken);
            var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
            var expectedHash = Header(response, "X-Meimad-Checksum-SHA256");
            if (string.IsNullOrWhiteSpace(expectedHash)
                || !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new PlannerProtocolException("Downloaded backup checksum did not match the Server checksum.");

            return new(
                localPath,
                length,
                actualHash,
                DateTimeOffset.TryParse(Header(response, "X-Meimad-Backup-Created-At"),
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt) ? createdAt : null,
                string.Equals(Header(response, "X-Meimad-Integrity-Verified"), "true", StringComparison.OrdinalIgnoreCase),
                string.Equals(Header(response, "X-Meimad-Restore-Verified"), "true", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            if (File.Exists(localPath)) File.Delete(localPath);
            throw;
        }
    }

    public async Task<PlannerGCodeCatalog> GetOperationGCodeAsync(
        string caseId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(operationId)}/gcode",
            cancellationToken);
        return await ReadSuccessAsync<PlannerGCodeCatalog>(response, cancellationToken);
    }

    public async Task<PlannerGCodeRelease> ReleaseGCodeAsync(
        string caseId,
        string operationId,
        GCodeReleaseCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(operationId)}/gcode-releases",
            clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(create.PostprocessorId), "postprocessorId");
        content.Add(new StringContent(create.ChangeScope), "changeScope");
        content.Add(new StringContent(create.ReleaseComment), "releaseComment");
        if (!string.IsNullOrWhiteSpace(create.ProcessChangeDescription))
        {
            content.Add(new StringContent(create.ProcessChangeDescription), "processChangeDescription");
        }
        content.Add(new StringContent(create.ConfirmNewProcessRevision.ToString()), "confirmNewProcessRevision");
        content.Add(new StringContent(create.ReuseActiveToolTable.ToString()), "reuseActiveToolTable");
        content.Add(new StringContent(create.ConfirmToolTable.ToString()), "confirmToolTable");
        await using var gcodeStream = File.OpenRead(create.GCodeFilePath);
        using var gcodeContent = new StreamContent(gcodeStream);
        content.Add(gcodeContent, "gcodeFile", Path.GetFileName(create.GCodeFilePath));
        FileStream? toolStream = null;
        StreamContent? toolContent = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(create.ToolTableFilePath))
            {
                toolStream = File.OpenRead(create.ToolTableFilePath);
                toolContent = new StreamContent(toolStream);
                content.Add(toolContent, "toolTableFile", Path.GetFileName(create.ToolTableFilePath));
            }

            request.Content = content;
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return await ReadSuccessAsync<PlannerGCodeRelease>(response, cancellationToken);
        }
        finally
        {
            toolContent?.Dispose();
            if (toolStream is not null)
            {
                await toolStream.DisposeAsync();
            }
        }
    }

    public async Task<IReadOnlyList<CaseComponent>> ListCaseComponentsAsync(
        string caseId, CancellationToken cancellationToken = default) =>
        await ReadListAsync<CaseComponent>(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/components", cancellationToken);

    public async Task<IReadOnlyList<CaseComponent>> ListCaseWhereUsedAsync(
        string caseId, CancellationToken cancellationToken = default) =>
        await ReadListAsync<CaseComponent>(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/where-used", cancellationToken);

    public async Task<CaseComponent> CreateCaseComponentAsync(
        string caseId, CaseComponentCreate create, string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post,
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/components", clientId);
        request.Headers.Add(EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<CaseComponent>(response, cancellationToken);
    }

    public async Task<CaseComponent> UpdateCaseComponentAsync(
        string caseId, string componentId, CaseComponentUpdate update, string entityTag,
        string clientId, long editGeneration, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch,
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/components/{Uri.EscapeDataString(componentId)}", clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<CaseComponent>(response, cancellationToken);
    }

    public async Task DeactivateCaseComponentAsync(
        string caseId, string componentId, string entityTag, string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Delete,
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/components/{Uri.EscapeDataString(componentId)}", clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        _ = await ReadSuccessAsync<CaseComponent>(response, cancellationToken);
    }

    public async Task<ComponentDemandPreview> PreviewCaseComponentDemandAsync(
        string caseId, double quantity, CancellationToken cancellationToken = default)
    {
        var value = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var response = await httpClient.GetAsync(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/component-demand?quantity={Uri.EscapeDataString(value)}",
            cancellationToken);
        return await ReadSuccessAsync<ComponentDemandPreview>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<DerivedCaseOrder>> ListDerivedCaseOrdersAsync(
        string caseId, CancellationToken cancellationToken = default) =>
        await ReadListAsync<DerivedCaseOrder>(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/derived-orders", cancellationToken);

    public async Task<IReadOnlyList<PlannerOrder>> ListOrdersAsync(
        string caseId,
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<PlannerOrder>(
            $"api/v1/orders?caseId={Uri.EscapeDataString(caseId)}",
            cancellationToken);

    public async Task<PlannerOrder> CreateOrderAsync(
        OrderCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/orders", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<PlannerOrder>(response, cancellationToken);
    }

    public async Task<PlannerOrder> UpdateOrderAsync(
        string orderId,
        OrderUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Patch,
            $"api/v1/orders/{Uri.EscapeDataString(orderId)}",
            clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<PlannerOrder>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<ProductionBatch>> ListBatchesAsync(
        string caseId,
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<ProductionBatch>(
            $"api/v1/batches?caseId={Uri.EscapeDataString(caseId)}",
            cancellationToken);

    public async Task<ProductionBatch> CreateBatchAsync(
        ProductionBatchCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/batches", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<ProductionBatch>(response, cancellationToken);
    }

    public async Task<ProductionBatch> UpdateBatchAsync(
        string batchId,
        ProductionBatchUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"api/v1/batches/{Uri.EscapeDataString(batchId)}", clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<ProductionBatch>(response, cancellationToken);
    }

    public async Task<BatchMaterialReconciliation> GetBatchMaterialAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/batches/{Uri.EscapeDataString(batchId)}/material", cancellationToken);
        return await ReadSuccessAsync<BatchMaterialReconciliation>(response, cancellationToken);
    }

    public async Task<VerifiedMaterialReceipt> CreateVerifiedMaterialReceiptAsync(
        VerifiedMaterialReceiptCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/material-receipts", clientId);
        request.Headers.Add(EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<VerifiedMaterialReceipt>(response, cancellationToken);
    }

    public async Task<BatchMaterialReconciliation> ReplaceBatchMaterialReservationsAsync(
        string batchId,
        MaterialReservationsReplace update,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put,
            $"api/v1/batches/{Uri.EscapeDataString(batchId)}/material/reservations", clientId);
        request.Headers.Add(EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<BatchMaterialReconciliation>(response, cancellationToken);
    }

    public async Task<byte[]?> GetCasePreviewAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/preview",
            cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound
            or System.Net.HttpStatusCode.UnsupportedMediaType)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, cancellationToken);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<LegacyWorkingPlanPreview> PreviewLegacyWorkingPlanAsync(
        Stream workbook,
        string fileName,
        CancellationToken cancellationToken = default)
        => await PreviewLegacyWorkingPlanAsync(
            workbook,
            fileName,
            planningSheet: null,
            openOrdersSheet: null,
            columnMappings: null,
            cancellationToken);

    public async Task<LegacyWorkingPlanPreview> PreviewLegacyWorkingPlanAsync(
        Stream workbook,
        string fileName,
        string? planningSheet,
        string? openOrdersSheet,
        IReadOnlyList<LegacyImportColumnMapping>? columnMappings,
        CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(workbook);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(fileContent, "workbook", fileName);
        if (planningSheet is not null)
        {
            content.Add(new StringContent(planningSheet, Encoding.UTF8), "planningSheet");
        }
        if (openOrdersSheet is not null)
        {
            content.Add(new StringContent(openOrdersSheet, Encoding.UTF8), "openOrdersSheet");
        }
        if (columnMappings is not null)
        {
            content.Add(new StringContent(
                JsonSerializer.Serialize(columnMappings, JsonOptions),
                Encoding.UTF8,
                "application/json"), "columnMappings");
        }
        using var response = await importHttpClient.PostAsync(
            "api/v1/imports/legacy-working-plan/preview", content, cancellationToken);
        return await ReadSuccessAsync<LegacyWorkingPlanPreview>(response, cancellationToken);
    }

    public async Task<LegacyWorkingPlanCommitReceipt> CommitLegacyWorkingPlanAsync(
        LegacyWorkingPlanCommit commit,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            "api/v1/imports/legacy-working-plan/commit",
            clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(commit);
        using var response = await importHttpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<LegacyWorkingPlanCommitReceipt>(response, cancellationToken);
    }

    public async Task<PlannerMachine> CreateMachineAsync(
        MachineCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/machines", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<PlannerMachine>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<PlannerMachine>> ListMachinesAsync(
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<PlannerMachine>("api/v1/machines", cancellationToken);

    public async Task<IReadOnlyList<UserTerminal>> ListUserTerminalsAsync(
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<UserTerminal>("api/v1/eink/device-registrations", cancellationToken);

    public async Task<UserTerminal> CreateUserTerminalAsync(
        CreateUserTerminalRequest value, string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/eink/device-registrations", clientId);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(value, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<UserTerminal>(response, cancellationToken);
    }

    public async Task<UserTerminal> UpdateUserTerminalAsync(
        string deviceId, UpdateUserTerminalRequest value, string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch,
            $"api/v1/eink/device-registrations/{Uri.EscapeDataString(deviceId)}", clientId);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(value, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<UserTerminal>(response, cancellationToken);
    }

    public async Task DeleteUserTerminalAsync(string deviceId, string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Delete,
            $"api/v1/eink/device-registrations/{Uri.EscapeDataString(deviceId)}", clientId);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessWithoutBodyAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<QcQueueItem>> ListQcQueueAsync(
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<QcQueueItem>("api/v1/qc-queue", cancellationToken);

    public async Task<IReadOnlyList<PreparationQueueItem>> ListPreparationQueueAsync(
        string stage,
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<PreparationQueueItem>(
            $"api/v1/preparation-queues/{Uri.EscapeDataString(stage)}",
            cancellationToken);

    public async Task<ProductionPackageInfo> CreateProductionPackageAsync(
        string batchOperationId, string clientId, string userId,
        string toolOffsetMode = "MEASURED",
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post,
            $"api/v1/batch-operations/{Uri.EscapeDataString(batchOperationId)}/production-package?toolOffsetMode={Uri.EscapeDataString(toolOffsetMode)}",
            clientId);
        request.Headers.Add(UserIdHeader, userId);
        request.Content = JsonContent.Create(new { }, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<ProductionPackageInfo>(response, cancellationToken);
    }

    public async Task<ProductionPackageInfo?> GetCurrentProductionPackageAsync(
        string batchOperationId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/batch-operations/{Uri.EscapeDataString(batchOperationId)}/production-package",
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadSuccessAsync<ProductionPackageInfo>(response, cancellationToken);
    }

    public async Task<byte[]> ReadProductionPackageArtifactAsync(
        string batchOperationId, string artifactId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/batch-operations/{Uri.EscapeDataString(batchOperationId)}/production-package/artifacts/{Uri.EscapeDataString(artifactId)}",
            cancellationToken);
        return await ReadBytesSuccessAsync(response, cancellationToken);
    }

    public async Task<string> ReadGCodeFileTextAsync(
        string caseId, string caseOperationId, string releaseId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(caseOperationId)}/gcode-releases/{Uri.EscapeDataString(releaseId)}/file",
            cancellationToken);
        var bytes = await ReadBytesSuccessAsync(response, cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task<byte[]> ReadToolTableFileAsync(
        string caseId, string caseOperationId, string releaseId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(caseOperationId)}/tool-table-releases/{Uri.EscapeDataString(releaseId)}/file",
            cancellationToken);
        return await ReadBytesSuccessAsync(response, cancellationToken);
    }

    public async Task<QcDecisionResult> DecideQcAsync(
        string productionRunId, QcDecisionRequest value,
        string clientId, string userId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/v1/qc-queue/{Uri.EscapeDataString(productionRunId)}/decision",
            clientId);
        request.Headers.Add(UserIdHeader, userId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(value, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<QcDecisionResult>(response, cancellationToken);
    }

    public async Task<MachineResource> GetMachineAsync(
        string machineId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"api/v1/machines/{Uri.EscapeDataString(machineId)}", cancellationToken);
        return new MachineResource(
            await ReadSuccessAsync<PlannerMachine>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public async Task<MachineResource> UpdateMachineAsync(
        string machineId,
        MachineCreate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"api/v1/machines/{Uri.EscapeDataString(machineId)}", clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return new MachineResource(
            await ReadSuccessAsync<PlannerMachine>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public Task DeleteCaseAsync(string caseId, string clientId, long generation, CancellationToken token = default) =>
        DeleteAsync($"api/v1/cases/{Uri.EscapeDataString(caseId)}", clientId, generation, token);
    public Task DeleteCaseOperationAsync(string caseId, string operationId, string clientId, long generation, CancellationToken token = default) =>
        DeleteAsync($"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(operationId)}", clientId, generation, token);
    public Task DeleteOrderAsync(string orderId, string clientId, long generation, CancellationToken token = default) =>
        DeleteAsync($"api/v1/orders/{Uri.EscapeDataString(orderId)}", clientId, generation, token);
    public Task DeleteBatchAsync(string batchId, string clientId, long generation, CancellationToken token = default) =>
        DeleteAsync($"api/v1/batches/{Uri.EscapeDataString(batchId)}", clientId, generation, token);
    public Task DeleteMachineAsync(string machineId, string clientId, long generation, CancellationToken token = default) =>
        DeleteAsync($"api/v1/machines/{Uri.EscapeDataString(machineId)}", clientId, generation, token);

    public async Task<IReadOnlyList<MachineDowntime>> ListDowntimesAsync(
        string? machineId = null,
        CancellationToken cancellationToken = default)
    {
        var path = "api/v1/downtimes";
        if (!string.IsNullOrWhiteSpace(machineId))
            path += $"?machineId={Uri.EscapeDataString(machineId)}";
        return await ReadListAsync<MachineDowntime>(path, cancellationToken);
    }

    public async Task<MachineDowntime> CreateDowntimeAsync(
        MachineDowntimeCreate create, string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/downtimes", clientId);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<MachineDowntime>(response, cancellationToken);
    }

    public async Task<MachineDowntimeResource> UpdatePlannedMaintenanceAsync(
        string downtimeId, PlannedMaintenanceUpdate update, string entityTag,
        string clientId, long editGeneration, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch,
            $"api/v1/downtimes/{Uri.EscapeDataString(downtimeId)}", clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return new(await ReadSuccessAsync<MachineDowntime>(response, cancellationToken), RequiredEntityTag(response));
    }

    public async Task<MachineDowntimeResource> RestoreBreakdownAsync(
        string downtimeId, BreakdownRestore restore, string entityTag,
        string clientId, long editGeneration, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post,
            $"api/v1/downtimes/{Uri.EscapeDataString(downtimeId)}/restore", clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(restore);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return new(await ReadSuccessAsync<MachineDowntime>(response, cancellationToken), RequiredEntityTag(response));
    }

    private async Task DeleteAsync(string path, string clientId, long generation, CancellationToken token)
    {
        using var request = CreateRequest(HttpMethod.Delete, path, clientId);
        request.Headers.Add(EditGenerationHeader, generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, token);
        await EnsureSuccessWithoutBodyAsync(response, token);
    }

    public async Task<IReadOnlyList<WorkingCalendar>> ListWorkingCalendarsAsync(
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<WorkingCalendar>("api/v1/working-calendars", cancellationToken);

    public async Task<WorkingCalendar> CreateWorkingCalendarAsync(
        WorkingCalendarCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/working-calendars", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<WorkingCalendar>(response, cancellationToken);
    }

    public async Task<WorkingCalendarResource> GetWorkingCalendarAsync(
        string workingCalendarId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/working-calendars/{Uri.EscapeDataString(workingCalendarId)}",
            cancellationToken);
        return new WorkingCalendarResource(
            await ReadSuccessAsync<WorkingCalendar>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public async Task<HaasConnectionSettings> GetHaasConnectionAsync(
        string machineId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/machines/{Uri.EscapeDataString(machineId)}/haas/connection", cancellationToken);
        return await ReadSuccessAsync<HaasConnectionSettings>(response, cancellationToken);
    }

    public async Task<HaasConnectionSettings> UpdateHaasConnectionAsync(
        string machineId, HaasConnectionUpdate update, string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put,
            $"api/v1/machines/{Uri.EscapeDataString(machineId)}/haas/connection", clientId);
        request.Headers.Add(EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<HaasConnectionSettings>(response, cancellationToken);
    }

    public async Task<HaasConnectionTest> TestHaasMdcAsync(
        string machineId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync(
            $"api/v1/machines/{Uri.EscapeDataString(machineId)}/haas/test-mdc", null, cancellationToken);
        return await ReadHaasConnectionTestAsync(response, cancellationToken);
    }

    public async Task<HaasConnectionTest> TestHaasMtConnectAsync(
        string machineId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync(
            $"api/v1/machines/{Uri.EscapeDataString(machineId)}/haas/test-mtconnect", null, cancellationToken);
        return await ReadHaasConnectionTestAsync(response, cancellationToken);
    }

    public async Task<HaasConnectionTest> TestHaasNetShareAsync(
        string machineId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync(
            $"api/v1/machines/{Uri.EscapeDataString(machineId)}/haas/test-net-share", null, cancellationToken);
        return await ReadHaasConnectionTestAsync(response, cancellationToken);
    }

    public async Task<HaasMachineMonitor> GetHaasMonitorAsync(
        string machineId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/machines/{Uri.EscapeDataString(machineId)}/haas/monitor", cancellationToken);
        return await ReadSuccessAsync<HaasMachineMonitor>(response, cancellationToken);
    }

    public async Task<CncVerificationSettings> GetCncVerificationSettingsAsync(
        string machineId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/machines/{Uri.EscapeDataString(machineId)}/verification-configuration", cancellationToken);
        return await ReadSuccessAsync<CncVerificationSettings>(response, cancellationToken);
    }

    public async Task<CncVerificationSettings> UpdateCncVerificationSettingsAsync(
        string machineId, CncVerificationSettingsUpdate update, string clientId,
        long editGeneration, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put,
            $"api/v1/machines/{Uri.EscapeDataString(machineId)}/verification-configuration", clientId);
        request.Headers.Add(EditGenerationHeader,
            editGeneration.ToString(CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<CncVerificationSettings>(response, cancellationToken);
    }

    public async Task<OffsetLoaderRelease> CreateOffsetLoaderReleaseAsync(
        string productionRunId, CreateOffsetLoaderReleaseRequest create,
        string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post,
            $"api/v1/production-runs/{Uri.EscapeDataString(productionRunId)}/offset-loader-releases",
            clientId);
        request.Headers.Add(EditGenerationHeader,
            editGeneration.ToString(CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<OffsetLoaderRelease>(response, cancellationToken);
    }

    public Task<CncRecoveryResult> InvalidateCncVerificationAsync(
        string productionRunId, CncRecoveryRequest recovery,
        string clientId, long editGeneration,
        CancellationToken cancellationToken = default) =>
        SubmitCncRecoveryAsync(productionRunId, "verification/invalidate", recovery,
            clientId, editGeneration, cancellationToken);

    public Task<CncRecoveryResult> RevokeCurrentOffsetLoaderAsync(
        string productionRunId, CncRecoveryRequest recovery,
        string clientId, long editGeneration,
        CancellationToken cancellationToken = default) =>
        SubmitCncRecoveryAsync(productionRunId, "offset-loader/current/revoke", recovery,
            clientId, editGeneration, cancellationToken);

    private async Task<CncRecoveryResult> SubmitCncRecoveryAsync(
        string productionRunId, string suffix, CncRecoveryRequest recovery,
        string clientId, long editGeneration, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post,
            $"api/v1/production-runs/{Uri.EscapeDataString(productionRunId)}/{suffix}", clientId);
        request.Headers.Add(EditGenerationHeader,
            editGeneration.ToString(CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(recovery);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<CncRecoveryResult>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<CncAdapterDefinition>> ListCncAdaptersAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("api/v1/cnc-adapters", cancellationToken);
        return await ReadSuccessAsync<IReadOnlyList<CncAdapterDefinition>>(response, cancellationToken);
    }

    public async Task ReconnectCncAsync(
        string machineId, string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post,
            $"api/v1/machines/{Uri.EscapeDataString(machineId)}/cnc-connection/reconnect", clientId);
        request.Headers.Add(EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        _ = await ReadSuccessAsync<JsonElement>(response, cancellationToken);
    }

    public async Task<WorkingCalendarResource> UpdateWorkingCalendarAsync(
        string workingCalendarId,
        WorkingCalendarUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Patch,
            $"api/v1/working-calendars/{Uri.EscapeDataString(workingCalendarId)}",
            clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return new WorkingCalendarResource(
            await ReadSuccessAsync<WorkingCalendar>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public Task DeleteWorkingCalendarAsync(
        string workingCalendarId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            $"api/v1/working-calendars/{Uri.EscapeDataString(workingCalendarId)}",
            clientId,
            editGeneration,
            cancellationToken);

    public async Task<SetupCalendarSelection> GetSetupCalendarAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("api/v1/setup-calendar", cancellationToken);
        return await ReadSuccessAsync<SetupCalendarSelection>(response, cancellationToken);
    }

    public async Task<SetupCalendarSelection> SetSetupCalendarAsync(
        string workingCalendarId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put, "api/v1/setup-calendar", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(new SetupCalendarUpdate(workingCalendarId));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<SetupCalendarSelection>(response, cancellationToken);
    }

    public Task ClearSetupCalendarAsync(
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        DeleteAsync("api/v1/setup-calendar", clientId, editGeneration, cancellationToken);

    public async Task<SetupCalendarSelection> GetMasterCalendarAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("api/v1/master-calendar", cancellationToken);
        return await ReadSuccessAsync<SetupCalendarSelection>(response, cancellationToken);
    }

    public async Task<SetupCalendarSelection> SetMasterCalendarAsync(
        string workingCalendarId, string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put, "api/v1/master-calendar", clientId);
        request.Headers.Add(EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(new SetupCalendarUpdate(workingCalendarId));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<SetupCalendarSelection>(response, cancellationToken);
    }

    public Task ClearMasterCalendarAsync(string clientId, long editGeneration,
        CancellationToken cancellationToken = default) =>
        DeleteAsync("api/v1/master-calendar", clientId, editGeneration, cancellationToken);

    public async Task<IReadOnlyList<PlannerMachineType>> ListMachineTypesAsync(
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<PlannerMachineType>("api/v1/machine-types", cancellationToken);

    public async Task<MachineTypeResource> GetMachineTypeAsync(
        string machineTypeId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/machine-types/{Uri.EscapeDataString(machineTypeId)}",
            cancellationToken);
        return new MachineTypeResource(
            await ReadSuccessAsync<PlannerMachineType>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public async Task<PlannerMachineType> CreateMachineTypeAsync(
        MachineTypeCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/machine-types", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<PlannerMachineType>(response, cancellationToken);
    }

    public async Task<MachineTypeResource> UpdateMachineTypeAsync(
        string machineTypeId,
        MachineTypeUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Patch,
            $"api/v1/machine-types/{Uri.EscapeDataString(machineTypeId)}",
            clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return new MachineTypeResource(
            await ReadSuccessAsync<PlannerMachineType>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public Task DeleteMachineTypeAsync(
        string machineTypeId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            $"api/v1/machine-types/{Uri.EscapeDataString(machineTypeId)}",
            clientId,
            editGeneration,
            cancellationToken);

    public async Task<IReadOnlyList<PlannerPostprocessor>> ListPostprocessorsAsync(
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<PlannerPostprocessor>("api/v1/postprocessors", cancellationToken);

    public async Task<PostprocessorResource> GetPostprocessorAsync(
        string postprocessorId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/postprocessors/{Uri.EscapeDataString(postprocessorId)}",
            cancellationToken);
        return new PostprocessorResource(
            await ReadSuccessAsync<PlannerPostprocessor>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public async Task<PlannerPostprocessor> CreatePostprocessorAsync(
        PostprocessorCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/postprocessors", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<PlannerPostprocessor>(response, cancellationToken);
    }

    public async Task<PostprocessorResource> UpdatePostprocessorAsync(
        string postprocessorId,
        PostprocessorUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Patch,
            $"api/v1/postprocessors/{Uri.EscapeDataString(postprocessorId)}",
            clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return new PostprocessorResource(
            await ReadSuccessAsync<PlannerPostprocessor>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public Task DeletePostprocessorAsync(
        string postprocessorId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            $"api/v1/postprocessors/{Uri.EscapeDataString(postprocessorId)}",
            clientId,
            editGeneration,
            cancellationToken);

    public async Task<IReadOnlyList<PlannerResource>> ListResourcesAsync(
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<PlannerResource>("api/v1/resources", cancellationToken);

    public async Task<IReadOnlyList<PlannerSkill>> ListSkillsAsync(CancellationToken cancellationToken = default) =>
        await ReadArrayAsync<PlannerSkill>("api/v1/resources/skills", cancellationToken);

    public Task<PlannerSkill> CreateSkillAsync(SkillCreate create, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) =>
        PostResourceAsync<PlannerSkill>("api/v1/resources/skills", create, clientId, editGeneration, cancellationToken);
    public Task<PlannerSkill> UpdateSkillAsync(string id,SkillUpdate value,string clientId,long generation,CancellationToken token=default)=>PatchResourceAsync<PlannerSkill>($"api/v1/resources/skills/{Uri.EscapeDataString(id)}",value,clientId,generation,token);
    public Task DeleteSkillAsync(string id,int version,string clientId,long generation,CancellationToken token=default)=>DeleteAsync($"api/v1/resources/skills/{Uri.EscapeDataString(id)}?version={version}",clientId,generation,token);

    public async Task<IReadOnlyList<PlannerWorkstationType>> ListWorkstationTypesAsync(CancellationToken cancellationToken = default) =>
        await ReadArrayAsync<PlannerWorkstationType>("api/v1/resources/workstation-types", cancellationToken);

    public Task<PlannerWorkstationType> CreateWorkstationTypeAsync(WorkstationTypeCreate create, string clientId,
        long editGeneration, CancellationToken cancellationToken = default) =>
        PostResourceAsync<PlannerWorkstationType>("api/v1/resources/workstation-types", create, clientId, editGeneration, cancellationToken);
    public Task<PlannerWorkstationType> UpdateWorkstationTypeAsync(string id,WorkstationTypeUpdate value,string clientId,long generation,CancellationToken token=default)=>PatchResourceAsync<PlannerWorkstationType>($"api/v1/resources/workstation-types/{Uri.EscapeDataString(id)}",value,clientId,generation,token);
    public Task DeleteWorkstationTypeAsync(string id,int version,string clientId,long generation,CancellationToken token=default)=>DeleteAsync($"api/v1/resources/workstation-types/{Uri.EscapeDataString(id)}?version={version}",clientId,generation,token);

    public async Task<IReadOnlyList<PlannerWorkstation>> ListWorkstationsAsync(CancellationToken cancellationToken = default) =>
        await ReadArrayAsync<PlannerWorkstation>("api/v1/resources/workstations", cancellationToken);

    public Task<PlannerWorkstation> CreateWorkstationAsync(WorkstationCreate create, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) =>
        PostResourceAsync<PlannerWorkstation>("api/v1/resources/workstations", create, clientId, editGeneration, cancellationToken);
    public Task<PlannerWorkstation> UpdateWorkstationAsync(string id,WorkstationUpdate value,string clientId,long generation,CancellationToken token=default)=>PatchResourceAsync<PlannerWorkstation>($"api/v1/resources/workstations/{Uri.EscapeDataString(id)}",value,clientId,generation,token);
    public Task DeleteWorkstationAsync(string id,int version,string clientId,long generation,CancellationToken token=default)=>DeleteAsync($"api/v1/resources/workstations/{Uri.EscapeDataString(id)}?version={version}",clientId,generation,token);

    public async Task<IReadOnlyList<PlannerExternalResource>> ListExternalResourcesAsync(CancellationToken cancellationToken = default) =>
        await ReadArrayAsync<PlannerExternalResource>("api/v1/resources/external", cancellationToken);

    public Task<PlannerExternalResource> CreateExternalResourceAsync(ExternalResourceCreate create, string clientId,
        long editGeneration, CancellationToken cancellationToken = default) =>
        PostResourceAsync<PlannerExternalResource>("api/v1/resources/external", create, clientId, editGeneration, cancellationToken);
    public Task<PlannerExternalResource> UpdateExternalResourceAsync(string id,ExternalResourceUpdate value,string clientId,long generation,CancellationToken token=default)=>PatchResourceAsync<PlannerExternalResource>($"api/v1/resources/external/{Uri.EscapeDataString(id)}",value,clientId,generation,token);
    public Task DeleteExternalResourceAsync(string id,int version,string clientId,long generation,CancellationToken token=default)=>DeleteAsync($"api/v1/resources/external/{Uri.EscapeDataString(id)}?version={version}",clientId,generation,token);

    public async Task<PlannerEmployeeSkills> GetEmployeeSkillsAsync(string employeeId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/resources/employees/{Uri.EscapeDataString(employeeId)}/skills", cancellationToken);
        return await ReadSuccessAsync<PlannerEmployeeSkills>(response, cancellationToken);
    }

    public async Task<PlannerEmployeeSkills> SetEmployeeSkillsAsync(string employeeId, EmployeeSkillsUpdate update,
        string clientId, long editGeneration, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put,
            $"api/v1/resources/employees/{Uri.EscapeDataString(employeeId)}/skills", clientId);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<PlannerEmployeeSkills>(response, cancellationToken);
    }

    public async Task<PlannerResource> CreateResourceAsync(
        ResourceCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/resources", clientId);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<PlannerResource>(response, cancellationToken);
    }

    public async Task<ResourceResource> GetResourceAsync(
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"api/v1/resources/{Uri.EscapeDataString(resourceId)}", cancellationToken);
        return new ResourceResource(
            await ReadSuccessAsync<PlannerResource>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public async Task<ResourceResource> UpdateResourceAsync(
        string resourceId,
        ResourceUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"api/v1/resources/{Uri.EscapeDataString(resourceId)}", clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return new ResourceResource(
            await ReadSuccessAsync<PlannerResource>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public Task DeleteResourceAsync(
        string resourceId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/v1/resources/{Uri.EscapeDataString(resourceId)}", clientId, editGeneration, cancellationToken);

    public async Task<IReadOnlyList<EmployeeCalendarException>> ListEmployeeExceptionsAsync(
        string resourceId,
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<EmployeeCalendarException>(
            $"api/v1/resources/{Uri.EscapeDataString(resourceId)}/exceptions", cancellationToken);

    public async Task<EmployeeCalendarException> CreateEmployeeExceptionAsync(
        string resourceId, EmployeeCalendarExceptionCreate create,
        string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post,
            $"api/v1/resources/{Uri.EscapeDataString(resourceId)}/exceptions", clientId);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<EmployeeCalendarException>(response, cancellationToken);
    }

    public async Task<EmployeeCalendarExceptionResource> UpdateEmployeeExceptionAsync(
        string resourceId, string exceptionId, EmployeeCalendarExceptionUpdate update,
        string entityTag, string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch,
            $"api/v1/resources/{Uri.EscapeDataString(resourceId)}/exceptions/{Uri.EscapeDataString(exceptionId)}", clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return new(await ReadSuccessAsync<EmployeeCalendarException>(response, cancellationToken), RequiredEntityTag(response));
    }

    public Task DeleteEmployeeExceptionAsync(
        string resourceId, string exceptionId, string clientId, long editGeneration,
        CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/v1/resources/{Uri.EscapeDataString(resourceId)}/exceptions/{Uri.EscapeDataString(exceptionId)}",
            clientId, editGeneration, cancellationToken);

    public async Task<EmployeeAvailability> GetEmployeeAvailabilityAsync(
        string resourceId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/resources/{Uri.EscapeDataString(resourceId)}/availability?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}",
            cancellationToken);
        return await ReadSuccessAsync<EmployeeAvailability>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<IsraeliHoliday>> ListIsraeliHolidaysAsync(
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<IsraeliHoliday>("api/v1/israeli-holidays", cancellationToken);

    public async Task<IsraeliHoliday> CreateIsraeliHolidayAsync(
        IsraeliHolidayCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/israeli-holidays", clientId);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<IsraeliHoliday>(response, cancellationToken);
    }

    public async Task<IsraeliHolidayResource> GetIsraeliHolidayAsync(
        string israeliHolidayId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"api/v1/israeli-holidays/{Uri.EscapeDataString(israeliHolidayId)}", cancellationToken);
        return new IsraeliHolidayResource(
            await ReadSuccessAsync<IsraeliHoliday>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public async Task<IsraeliHolidayResource> UpdateIsraeliHolidayAsync(
        string israeliHolidayId,
        IsraeliHolidayUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"api/v1/israeli-holidays/{Uri.EscapeDataString(israeliHolidayId)}", clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return new IsraeliHolidayResource(
            await ReadSuccessAsync<IsraeliHoliday>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public Task DeleteIsraeliHolidayAsync(
        string israeliHolidayId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/v1/israeli-holidays/{Uri.EscapeDataString(israeliHolidayId)}", clientId, editGeneration, cancellationToken);

    public async Task<IsraeliHolidaySyncResult> SynchronizeIsraeliHolidaysAsync(
        IsraeliHolidaySyncRequest sync, string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request=CreateRequest(HttpMethod.Post,"api/v1/israeli-holidays/sync",clientId);
        request.Headers.Add(EditGenerationHeader,editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content=JsonContent.Create(sync);
        using var response=await httpClient.SendAsync(request,cancellationToken);
        return await ReadSuccessAsync<IsraeliHolidaySyncResult>(response,cancellationToken);
    }

    public async Task<ReportEmailSettingsResource> GetReportEmailSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("api/v1/report-email-settings", cancellationToken);
        return new ReportEmailSettingsResource(
            await ReadSuccessAsync<ReportEmailSettings>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public async Task<ReportEmailSettingsResource> UpdateReportEmailSettingsAsync(
        ReportEmailSettingsUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put, "api/v1/report-email-settings", clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return new ReportEmailSettingsResource(
            await ReadSuccessAsync<ReportEmailSettings>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public async Task<WeeklyMaterialReport> SendWeeklyMaterialReportAsync(
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/reports/weekly-material-order/send", clientId);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<WeeklyMaterialReport>(response, cancellationToken);
    }

    public async Task<WeeklyEmployeeEfficiencyReport> SendWeeklyEmployeeEfficiencyReportAsync(
        string clientId, long editGeneration, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/reports/weekly-employee-efficiency/send", clientId);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<WeeklyEmployeeEfficiencyReport>(response, cancellationToken);
    }

    public async Task<byte[]?> GetMachinePictureAsync(
        string machineId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/machines/{Uri.EscapeDataString(machineId)}/picture",
            cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound
            or System.Net.HttpStatusCode.UnsupportedMediaType)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, cancellationToken);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<PlanningBoardSnapshot> GetPlanningBoardAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("api/v1/planning-board", cancellationToken);
        return await ReadSuccessAsync<PlanningBoardSnapshot>(response, cancellationToken);
    }

    public async Task<ProductionRunResource> CreateProductionRunAsync(
        ProductionRunCreate value, string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/production-runs", clientId);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(value);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<ProductionRunResource>(response, cancellationToken);
    }

    public async Task<PlannerProductionReadiness> GetProductionReadinessAsync(
        string batchOperationId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/batch-operations/{Uri.EscapeDataString(batchOperationId)}/readiness",
            cancellationToken);
        return await ReadSuccessAsync<PlannerProductionReadiness>(response, cancellationToken);
    }

    public async Task<PlannerProductionReadiness> UpdateProductionReadinessInputsAsync(
        string batchOperationId,
        ProductionReadinessInputUpdate update,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Put,
            $"api/v1/batch-operations/{Uri.EscapeDataString(batchOperationId)}/readiness-inputs",
            clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<PlannerProductionReadiness>(response, cancellationToken);
    }

    public async Task<TimelineSnapshot> GetTimelineAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
        => await GetTimelineAsync(
            from, to, asOf: null, cancellationToken: cancellationToken);

    public async Task<TimelineSnapshot> GetTimelineAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset? asOf,
        CancellationToken cancellationToken = default)
    {
        var fromValue = Uri.EscapeDataString(from.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var toValue = Uri.EscapeDataString(to.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var asOfQuery = asOf.HasValue
            ? $"&asOf={Uri.EscapeDataString(asOf.Value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture))}"
            : string.Empty;
        using var response = await httpClient.GetAsync(
            $"api/v1/timeline?from={fromValue}&to={toValue}{asOfQuery}",
            cancellationToken);
        return await ReadSuccessAsync<TimelineSnapshot>(response, cancellationToken);
    }

    public async Task AssignOrMoveOperationAsync(
        string batchOperationId,
        string machineId,
        int backlogPosition,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        await AssignOrMoveOperationCoreAsync(
            batchOperationId,
            machineId,
            backlogPosition,
            clientId,
            editGeneration,
            compatibilityOverride: null,
            cancellationToken);
    }

    public Task AssignOrMoveOperationAsync(
        string batchOperationId,
        string machineId,
        int backlogPosition,
        string clientId,
        long editGeneration,
        MachineAssignmentCompatibilityOverride compatibilityOverride,
        CancellationToken cancellationToken = default) =>
        AssignOrMoveOperationCoreAsync(
            batchOperationId,
            machineId,
            backlogPosition,
            clientId,
            editGeneration,
            compatibilityOverride,
            cancellationToken);

    private async Task AssignOrMoveOperationCoreAsync(
        string batchOperationId,
        string machineId,
        int backlogPosition,
        string clientId,
        long editGeneration,
        MachineAssignmentCompatibilityOverride? compatibilityOverride,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Put,
            $"api/v1/batch-operations/{Uri.EscapeDataString(batchOperationId)}/assignment",
            clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(new
        {
            machineId,
            backlogPosition,
            compatibilityOverride
        });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessWithoutBodyAsync(response, cancellationToken);
    }

    public async Task UnassignOperationAsync(
        string batchOperationId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Delete,
            $"api/v1/batch-operations/{Uri.EscapeDataString(batchOperationId)}/assignment",
            clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessWithoutBodyAsync(response, cancellationToken);
    }

    public async Task<MachineAssignment> ChangeMachineAssignmentPlanningModeAsync(
        string machineAssignmentId,
        int assignmentVersion,
        string planningMode,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        var normalizedMode = planningMode?.Trim().ToLowerInvariant();
        if (normalizedMode is not ("forward" or "backward" or "manual"))
        {
            throw new ArgumentException(
                "Assignment planning mode must be 'forward', 'backward', or 'manual'.",
                nameof(planningMode));
        }

        if (string.IsNullOrWhiteSpace(machineAssignmentId))
        {
            throw new ArgumentException("Machine assignment ID is required.", nameof(machineAssignmentId));
        }

        if (assignmentVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(assignmentVersion),
                "Machine assignment version must be positive.");
        }

        using var request = CreateRequest(
            HttpMethod.Patch,
            $"api/v1/machine-assignments/{Uri.EscapeDataString(machineAssignmentId)}",
            clientId);
        request.Headers.TryAddWithoutValidation(
            "If-Match",
            $"\"machine-assignment:{machineAssignmentId}:v{assignmentVersion}\"");
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(new { planningMode = normalizedMode }, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<MachineAssignment>(response, cancellationToken);
    }

    public async Task<BatchOperationExecution> ChangeOperationExecutionAsync(
        string batchOperationId,
        string action,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/v1/batch-operations/{Uri.EscapeDataString(batchOperationId)}/{Uri.EscapeDataString(action)}",
            clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<BatchOperationExecution>(response, cancellationToken);
    }

    public async Task<BatchOperationExecution> PauseOperationAsync(
        string batchOperationId, OperationPauseRequest pause, string clientId,
        long editGeneration, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/v1/batch-operations/{Uri.EscapeDataString(batchOperationId)}/suspend",
            clientId);
        request.Headers.Add(EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(pause, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<BatchOperationExecution>(response, cancellationToken);
    }

    public async Task<ManualOperationReport> RecordManualOperationReportAsync(
        string batchOperationId, string reportType, int? partTimeSeconds,
        string clientId, long editGeneration, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post,
            $"api/v1/batch-operations/{Uri.EscapeDataString(batchOperationId)}/manual-report", clientId);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(new { reportType, partTimeSeconds }, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<ManualOperationReport>(response, cancellationToken);
    }

    public void Dispose()
    {
        if (ownsSeparateImportClient)
        {
            importHttpClient.Dispose();
        }
        httpClient.Dispose();
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string clientId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(ClientIdHeader, clientId);
        return request;
    }

    private static HttpRequestMessage CreateMaintenanceRequest(
        HttpMethod method,
        string path,
        string clientId,
        string userId,
        long? editGeneration = null)
    {
        var request = CreateRequest(method, path, clientId);
        request.Headers.Add(UserIdHeader, userId);
        if (editGeneration.HasValue)
            request.Headers.Add(EditGenerationHeader,
                editGeneration.Value.ToString(CultureInfo.InvariantCulture));
        return request;
    }

    private async Task<IReadOnlyList<T>> ReadArrayAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken);
        return await ReadSuccessAsync<T[]>(response, cancellationToken);
    }

    private async Task<T> PostResourceAsync<T>(string path, object value, string clientId,
        long editGeneration, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, path, clientId);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(value, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<T>(response, cancellationToken);
    }

    private async Task<T> PatchResourceAsync<T>(string path,object value,string clientId,long editGeneration,CancellationToken cancellationToken)
    {
        using var request=CreateRequest(HttpMethod.Patch,path,clientId);
        request.Headers.Add(EditGenerationHeader,editGeneration.ToString(CultureInfo.InvariantCulture));
        request.Content=JsonContent.Create(value,options:JsonOptions);
        using var response=await httpClient.SendAsync(request,cancellationToken);
        return await ReadSuccessAsync<T>(response,cancellationToken);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;

    private static string UniquePath(string folder, string fileName)
    {
        var initial = Path.Combine(folder, fileName);
        if (!File.Exists(initial)) return initial;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var candidate = Path.Combine(folder, $"{stem}-{suffix}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not choose an unused backup file name.");
    }

    private static async Task<T> ReadSuccessAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, cancellationToken);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                ?? throw new PlannerProtocolException("Server returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new PlannerProtocolException(
                $"Server returned an invalid {typeof(T).Name} response: {exception.Message}");
        }
    }

    private static async Task<byte[]> ReadBytesSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            await ThrowApiErrorAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static async Task<HaasConnectionTest> ReadHaasConnectionTestAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        HaasConnectionTest? result = null;
        try
        {
            result = JsonSerializer.Deserialize<HaasConnectionTest>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            // A non-test error envelope is handled by the normal safe API-error path below.
        }

        // Connection-test failures deliberately return HTTP 502 with the typed diagnostic body.
        // Preserve that result so Setup can show the actual failed component instead of replacing
        // it with the generic "Server returned HTTP 502" fallback.
        if ((response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.BadGateway)
            && result is not null
            && !string.IsNullOrWhiteSpace(result.Message))
            return result;

        if (!response.IsSuccessStatusCode)
            ThrowApiError(response, payload);

        throw new PlannerProtocolException("Server returned an invalid HaasConnectionTest response.");
    }

    private async Task<IReadOnlyList<T>> ReadListAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken);
        return (await ReadSuccessAsync<ListResponse<T>>(response, cancellationToken)).Items;
    }

    private static async Task EnsureSuccessWithoutBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, cancellationToken);
        }
    }

    private static async Task ThrowApiErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        ThrowApiError(response, payload);
    }

    private static void ThrowApiError(HttpResponseMessage response, byte[] payload)
    {
        ErrorEnvelope? error = null;
        try
        {
            error = JsonSerializer.Deserialize<ErrorEnvelope>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            // The safe fallback below avoids showing raw server content.
        }

        throw new PlannerApiException(
            response.StatusCode,
            error?.Error?.Code ?? "server_error",
            error?.Error?.Message ?? $"Server returned HTTP {(int)response.StatusCode}.",
            DetailString(error?.Error?.Details, "requiredMachineType"),
            DetailString(error?.Error?.Details, "selectedMachineType"));
    }

    private static string? DetailString(IReadOnlyList<JsonElement>? details, string propertyName)
    {
        foreach (var detail in details ?? [])
            if (detail.ValueKind == JsonValueKind.Object
                && detail.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static void AddQueryParameter(List<string> parameters, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
        }
    }

    private static string RequiredEntityTag(HttpResponseMessage response) =>
        response.Headers.ETag?.ToString()
        ?? throw new PlannerProtocolException("Server response is missing the resource ETag.");

    private static EditModeStatus Map(EditModeDto dto)
    {
        var state = dto.State switch
        {
            "viewer" => ClientEditState.Viewer,
            "editor" => ClientEditState.Editor,
            "requestingEdit" => ClientEditState.RequestingEdit,
            _ => throw new PlannerProtocolException(
                $"Server returned unknown Edit Mode state '{dto.State ?? "<null>"}'.")
        };
        var holder = dto.Holder is null
            ? null
            : new EditModeHolder(
                Required(dto.Holder.ClientId, "holder client ID"),
                Required(dto.Holder.UserId, "holder user ID"),
                dto.Holder.Generation,
                dto.Holder.AcquiredAt);
        var pending = dto.PendingRequest is null
            ? null
            : new EditTransferRequest(
                Required(dto.PendingRequest.RequestId, "request ID"),
                Required(dto.PendingRequest.RequesterClientId, "requester client ID"),
                Required(dto.PendingRequest.RequesterUserId, "requester user ID"),
                Required(dto.PendingRequest.Status, "request status"),
                dto.PendingRequest.RequestedAt,
                dto.PendingRequest.DecisionDeadline);
        return new EditModeStatus(
            state,
            dto.Generation,
            holder,
            pending,
            dto.ServerTime,
            dto.TransferTimeoutSeconds);
    }

    private static string Required(string? value, string field)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new PlannerProtocolException($"Server response is missing {field}.")
            : value;
    }

    private sealed record ServerHealthDto(
        string? Status,
        string? Service,
        string? Version,
        DateTimeOffset ServerTimeUtc);

    private sealed record EditModeDto(
        string? State,
        long Generation,
        EditModeHolderDto? Holder,
        EditTransferRequestDto? PendingRequest,
        DateTimeOffset ServerTime,
        int TransferTimeoutSeconds);

    private sealed record EditModeHolderDto(
        string? ClientId,
        string? UserId,
        long Generation,
        DateTimeOffset AcquiredAt);

    private sealed record EditTransferRequestDto(
        string? RequestId,
        string? RequesterClientId,
        string? RequesterUserId,
        string? Status,
        DateTimeOffset RequestedAt,
        DateTimeOffset DecisionDeadline);

    private sealed record ErrorEnvelope(ErrorBody? Error);

    private sealed record ErrorBody(string? Code, string? Message, IReadOnlyList<JsonElement>? Details);

    private sealed record ListResponse<T>(IReadOnlyList<T> Items, string? NextCursor);
}
