namespace Meimad.Planner.Server.Application.MachineEvidence;

/// <summary>A Machine a work offset message may name: matched by its ID or its number.</summary>
internal sealed record WorkOffsetMachineCandidate(string MachineId, string Number, string Name, bool IsActive, string NcDialect);

/// <summary>An NC release a work offset message may name, with what matching and staleness need.</summary>
internal sealed record WorkOffsetReleaseCandidate(
    string ReleaseId,
    string CaseOperationId,
    int OperationNumber,
    int? NcIdentityToken,
    string FileHash,
    bool IsCurrent);

/// <summary>A new row of <c>machine_work_offsets</c> as the ingest decided it.</summary>
internal sealed record WorkOffsetRow(
    string Id,
    string? MachineId,
    string? CaseOperationId,
    string? ReleaseId,
    string WorkOffset,
    double? X, double? Y, double? Z,
    double? A, double? B, double? C,
    string? Units,
    string Source,
    string SourceSession,
    string SessionKey,
    long? SourceSequence,
    WorkOffsetClaim Claimed,
    string RawMessage,
    string RawMessageHash,
    DateTimeOffset MeasuredAt,
    DateTimeOffset ReceivedAt,
    string ReceivedBy,
    string Status,
    string? StatusReason,
    string? SupersedesId);

/// <summary>Filter of the evidence list (review queue, unmatched evidence, one Operation across Machines).</summary>
internal sealed record WorkOffsetListQuery(
    IReadOnlyList<string> Statuses,
    string? MachineId,
    string? CaseOperationId,
    int Limit);

/// <summary>What the work offset report program of one Machine, Operation and NC release is rendered from.</summary>
internal sealed record WorkOffsetMacroContext(
    string MachineId,
    string MachineNumber,
    string MachineName,
    string NcDialect,
    string CaseOperationId,
    int OperationNumber,
    string? ReleaseId,
    int? NcIdentityToken,
    int? EventSequenceVariable);

/// <summary>
/// One ingest as a single immediate transaction: the duplicate and session checks, the matching reads,
/// the insert and the supersession of the previous current record commit together or not at all.
/// </summary>
internal interface IWorkOffsetIngestUnit : IAsyncDisposable
{
    Task<WorkOffsetRecord?> FindByHashAsync(string rawMessageHash, CancellationToken token);
    Task<WorkOffsetRecord?> FindBySessionAsync(string sessionKey, CancellationToken token);
    Task<IReadOnlyList<WorkOffsetMachineCandidate>> MachinesAsync(CancellationToken token);
    Task<IReadOnlyList<WorkOffsetReleaseCandidate>> FindReleasesAsync(string reference, CancellationToken token);
    Task<WorkOffsetRecord?> CurrentAsync(string machineId, string caseOperationId, string releaseId, string workOffset, CancellationToken token);
    Task<WorkOffsetRecord> InsertAsync(WorkOffsetRow row, CancellationToken token);
    Task CommitAsync(CancellationToken token);
}

internal interface IWorkOffsetRepository
{
    Task<IWorkOffsetIngestUnit> BeginIngestAsync(CancellationToken token);
    Task<WorkOffsetRecord?> GetAsync(string workOffsetId, CancellationToken token);

    /// <summary>Current records and history of a Machine and Case Operation; null when either does not exist.</summary>
    Task<WorkOffsetOperationView?> ReadOperationAsync(
        string machineId, string caseOperationId, string? workOffset, int limit, CancellationToken token);

    Task<IReadOnlyList<WorkOffsetRecord>> ListAsync(WorkOffsetListQuery query, CancellationToken token);

    /// <summary>
    /// Applies a review decision atomically; throws <see cref="WorkOffsetNotFoundException"/> or
    /// <see cref="Concurrency.EditConflictException"/> when the candidate or the current record changed.
    /// </summary>
    Task<WorkOffsetRecord> ReviewAsync(WorkOffsetReviewCommand command, DateTimeOffset now, CancellationToken token);

    /// <summary>The configured DPRNT source of the Machine's CNC connection (TCP, FILE, FTP, SERIAL), if any.</summary>
    Task<string?> GetDprntSourceAsync(string machineId, CancellationToken token);

    /// <summary>Null when the Machine or the Case Operation does not exist; throws when the release is not one of the Operation's.</summary>
    Task<WorkOffsetMacroContext?> GetMacroContextAsync(
        string machineId, string caseOperationId, string? releaseId, CancellationToken token);
}
