namespace Meimad.Planner.Server.Application.EInk;

internal sealed record EInkVersionResponse(
    int SchemaVersion,
    string TabletId,
    string? MachineId,
    string MachineScreenRevision,
    EInkVersionPackage? Package,
    string TimeConfigRevision);

internal sealed record EInkVersionPackage(string PackageId, string Revision);

internal sealed record EInkMachineScreenResponse(
    int SchemaVersion,
    string TabletId,
    string MachineScreenRevision,
    DateTimeOffset GeneratedAt,
    EInkMachineResponse? Machine,
    EInkStatusResponse Status,
    EInkJobResponse? Current,
    IReadOnlyList<EInkJobResponse> Next,
    IReadOnlyList<EInkConflictResponse> Conflicts,
    EInkScreenPackageResponse? Package);

internal sealed record EInkMachineResponse(
    string MachineId,
    string Number,
    string Name,
    string ProcessType);

internal sealed record EInkStatusResponse(string Code, string Label, string Icon, string Color);

internal sealed record EInkJobResponse(
    string PartNumber,
    string BatchNumber,
    string BatchOperationId,
    int OperationNumber,
    string OperationName,
    int Quantity,
    string Status,
    DateTimeOffset? ProjectedFinish);

internal sealed record EInkConflictResponse(string Code, string Severity, string Message);

internal sealed record EInkScreenPackageResponse(
    string PackageId,
    string Revision,
    string ManifestPath);

internal sealed record EInkManifestResponse(
    int SchemaVersion,
    string PackageId,
    string Revision,
    string MachineId,
    string BatchId,
    string BatchOperationId,
    string? ToolCartId,
    DateTimeOffset PublishedAt,
    EInkManifestMetadataResponse? Metadata,
    IReadOnlyList<EInkManifestFileResponse> Files);

internal sealed record EInkManifestMetadataResponse(
    EInkManifestMachineResponse Machine,
    EInkManifestPartResponse Part,
    EInkManifestBatchResponse Batch,
    EInkManifestOperationResponse Operation,
    EInkManifestSetupResponse Setup,
    EInkManifestToolsResponse Tools,
    EInkLocalChecklistResponse LocalChecklist,
    EInkTabletPolicyResponse TabletPolicy);

internal sealed record EInkManifestMachineResponse(string MachineId, string Number, string Name);

internal sealed record EInkManifestPartResponse(
    string CaseId,
    string PartNumber,
    string Name,
    string? Revision,
    string? Customer);

internal sealed record EInkManifestBatchResponse(
    string BatchId,
    string BatchNumber,
    int PlannedQuantity);

internal sealed record EInkManifestOperationResponse(
    string BatchOperationId,
    int OperationNumber,
    string Name);

internal sealed record EInkManifestSetupResponse(
    EInkManifestSetupWorkerResponse? Worker,
    DateTimeOffset? PlannedStartsAt,
    DateTimeOffset? PlannedEndsAt);

internal sealed record EInkManifestSetupWorkerResponse(
    string ResourceId,
    string FirstName,
    string LastName,
    string? PhotoFileId,
    string? PhotoDownloadPath);

internal sealed record EInkManifestToolsResponse(
    IReadOnlyList<EInkToolResponse> Job,
    IReadOnlyList<EInkToolResponse> ExpectedOnMachine);

internal sealed record EInkToolResponse(
    string ToolId,
    string Description,
    string? Diameter,
    string? Length,
    string? Note);

internal sealed record EInkLocalChecklistResponse(
    string Storage,
    bool SyncToServer,
    bool CommentsSupported,
    IReadOnlyList<EInkChecklistItemResponse> Items);

internal sealed record EInkChecklistItemResponse(string ItemId, string Label);

internal sealed record EInkTabletPolicyResponse(
    string Transport,
    string PersistentStorage,
    string ServerAccess,
    bool ReverseSynchronization,
    bool UsbMassStorage);

internal sealed record EInkManifestFileResponse(
    string FileId,
    string AssetType,
    string LogicalPath,
    string DownloadPath,
    string MediaType,
    long ByteLength,
    DateTimeOffset ModifiedAt,
    EInkChecksumResponse Checksum);

internal sealed record EInkChecksumResponse(string Algorithm, string Value);

internal sealed record EInkTimeConfigResponse(
    int SchemaVersion,
    string Revision,
    string TimeZoneId,
    IReadOnlyList<string> Workdays,
    IReadOnlyList<EInkShiftWindowResponse> ShiftWindows,
    int PollIntervalSeconds,
    EInkRetryResponse Retry,
    IReadOnlyList<object> DatedExceptions);

internal sealed record EInkShiftWindowResponse(string StartsAtLocal, string EndsAtLocal);

internal sealed record EInkRetryResponse(int MaximumAttempts, int InitialBackoffSeconds);

internal sealed record EInkFileDownload(
    string FullPath,
    string MediaType,
    long ByteLength,
    string Sha256);

internal sealed record EInkResource<T>(T Value, string EntityTag);
