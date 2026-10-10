using System.Text.Json;
namespace Meimad.Planner.Client.Windows.Api;
internal sealed record LocalAiControl(int Version, bool Suspended, bool ConfiguredEnabled);
internal sealed record LocalAiSettings(int Version, bool Enabled, string ModelId, string ModelDigest, string RuntimeVersion,
    string BaseUrl, DateTimeOffset? UpdatedAt, string? UpdatedBy, int CpuThreads = 0, int KeepAliveSeconds = 300, int MaxModelMemoryMb = 0, int ProcessorCount = 0);
internal sealed record LocalAiInstalledModel(string Name, string Digest, bool Remote, long SizeMb = 0)
{
    public override string ToString() => Name + (SizeMb > 0 ? $" ({SizeMb} MB)" : "") + (Remote ? " (remote)" : "");
}
internal sealed record LocalAiRuntimeProbe(bool Reachable, string BaseUrl, string? RuntimeVersion,
    IReadOnlyList<LocalAiInstalledModel> Models, string Detail);
internal sealed record LocalAiFeedback(int Version, bool Useful, string Correction, DateTimeOffset UpdatedAt);
internal sealed record EvidenceInspection(EvidenceFact Fact, string Currency, DateTimeOffset? CheckedAt, DateTimeOffset SampledAt);
internal sealed record ExplanationRequest(string Intent, string? ProductionRunId = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, string? MachineId = null, string? Question = null, string? SearchTerm = null);
internal sealed record EvidenceLink(string EntityType, string EntityId);
internal sealed record EvidenceFact(string EvidenceId, string Kind, string Code, JsonElement Value,
    string? Unit, EvidenceLink Source, string? SourceVersion, DateTimeOffset? ObservedAt,
    DateTimeOffset? ReceivedAt, string Validity, string Description);
internal sealed record EvidenceClaim(string Code, string Text, IReadOnlyList<string> EvidenceIds)
{ public string Citations => string.Join(", ", EvidenceIds); }
internal sealed record EvidenceBundle(int SchemaVersion, string Intent, DateTimeOffset AsOfUtc,
    string PlanRevision, string? ProductionRunId, string? MachineId, DateTimeOffset? From, DateTimeOffset? To,
    IReadOnlyList<EvidenceFact> Facts, IReadOnlyList<EvidenceClaim> Claims, IReadOnlyList<string> KnownLimitations);
internal sealed record LocalExplanation(string RequestId, string Mode, EvidenceBundle Evidence,
    IReadOnlyList<EvidenceClaim> Claims, string? FallbackReason = null,
    string Currency = "SNAPSHOT", DateTimeOffset? CheckedAt = null);
internal sealed record LocalAiRequestStatus(string RequestId, string Status, DateTimeOffset CreatedAt,
    LocalExplanation? Result = null, string? ResultCode = null);
internal sealed record CorpusRoot(string RootId, string Path);
internal sealed record CorpusSettings(int Version, IReadOnlyList<CorpusRoot> Roots);
internal sealed record CorpusIndexProgress(string JobId, int ConfigurationVersion, string Status,
    long FilesDiscovered, long FilesProcessed, long IndexedFiles, long SkippedFiles, long BytesRead,
    string? CurrentPath, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Error,
    long OcrPages = 0, bool OcrAvailable = false);
internal sealed record CorpusIndexResult(int ConfigurationVersion, long IndexedFiles, long SkippedFiles, DateTimeOffset CompletedAt);
internal sealed record CorpusMatch(string DocumentId, string Title, string ContentHash, string Excerpt,
    int StartCharacter, int EndCharacter, DateTimeOffset IndexedAt, string MatchReason, string Extraction = "TEXT");
internal sealed record AiChatTurn(string Role, string Content);
/// <summary>The current selection sent with a question; every ID is optional.</summary>
internal sealed record AiChatContext(string? CaseId = null, string? CaseOperationId = null, string? BatchId = null,
    string? BatchOperationId = null, string? ProcessRevisionId = null, string? GCodeReleaseId = null,
    string? MachineId = null, string? ProductionRunId = null);
internal sealed record AiChatSource(string Kind, string Title, string? Reference = null);
/// <summary>
/// A file the simulation-data search proposes for an Operation (Server <c>/api/v1/local-ai/simulation-data</c>):
/// kind <c>stock</c>, <c>fixture</c>, <c>tool-table</c> or <c>part-model</c>; source <c>AI</c> or <c>RULE</c>.
/// </summary>
internal sealed record SimulationDataCandidate(
    string Path, string Name, string Kind, double Confidence, string Reason, string Source,
    long SizeBytes, DateTimeOffset ModifiedAt, bool AlreadyLinked);

/// <summary>States: QUEUED, RUNNING, COMPLETED, FAILED, CANCELLED, EXPIRED, INTERRUPTED.</summary>
internal sealed record SimulationDataSearchStatus(
    string SearchId, string Status, DateTimeOffset CreatedAt, string? Progress = null,
    IReadOnlyList<SimulationDataCandidate>? Candidates = null, IReadOnlyList<string>? Folders = null,
    string? Summary = null, bool AiRanked = false, string? ResultCode = null, DateTimeOffset? CompletedAt = null);

internal sealed record AiChatStatus(string ChatId, string Status, DateTimeOffset CreatedAt, string? Progress = null,
    string? Answer = null, IReadOnlyList<AiChatSource>? Sources = null, string? ResultCode = null, DateTimeOffset? AnsweredAt = null);
