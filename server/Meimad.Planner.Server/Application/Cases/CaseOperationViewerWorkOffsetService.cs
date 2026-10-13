using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Application.Cases;

/// <summary>
/// The work offsets of a Case Operation on one NC viewer machine (schema v120): the absolute machine coordinates of each
/// work offset label (G54…G59, G54.1 Pn, G154 Pn, G110…G129, EXT), axis letter to value in mm or degrees. The single
/// source the NC viewer places the toolpath and the Operation's geometry with, for every user. Planner-entered CAM context
/// only: it changes no Case Operation version, planning data or Timeline.
/// </summary>
internal sealed record CaseOperationViewerWorkOffsets(
    string CaseOperationId,
    string CaseId,
    string ViewerMachine,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Offsets,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

internal enum CaseOperationViewerWorkOffsetOutcome
{
    Created,
    Updated,
    Deleted,
    OperationNotFound,
    OffsetsNotFound,
    AlreadyExists,
    Stale
}

/// <summary>The outcome of a write; <see cref="Offsets"/> is the saved set on success and the current one when refused as stale.</summary>
internal sealed record CaseOperationViewerWorkOffsetWriteResult(
    CaseOperationViewerWorkOffsetOutcome Outcome, CaseOperationViewerWorkOffsets? Offsets);

/// <summary>The sets of an Operation that belongs to the Case, or <c>OperationExists = false</c>.</summary>
internal sealed record CaseOperationViewerWorkOffsetList(bool OperationExists, IReadOnlyList<CaseOperationViewerWorkOffsets> Items);

internal sealed class CaseOperationViewerWorkOffsetValidationException(IReadOnlyList<CaseOperationCamSetupIssue> issues)
    : Exception("The work offsets are not valid.")
{
    internal IReadOnlyList<CaseOperationCamSetupIssue> Issues { get; } = issues;
}

internal interface ICaseOperationViewerWorkOffsetRepository
{
    Task<CaseOperationViewerWorkOffsetList> ListAsync(string caseId, string caseOperationId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates (<paramref name="expectedVersion"/> null) or replaces the set of one viewer machine in one immediate
    /// transaction; a create over an existing set or a replace of another version is refused.
    /// </summary>
    Task<CaseOperationViewerWorkOffsetWriteResult> SaveAsync(
        string caseId,
        string caseOperationId,
        string viewerMachine,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> offsets,
        int? expectedVersion,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<CaseOperationViewerWorkOffsetWriteResult> DeleteAsync(
        string caseId,
        string caseOperationId,
        string viewerMachine,
        int expectedVersion,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

internal sealed partial class CaseOperationViewerWorkOffsetService(ICaseOperationViewerWorkOffsetRepository repository, TimeProvider timeProvider)
{
    internal const double ValueLimit = 100000;
    internal const int MaximumLabels = 64;

    public Task<CaseOperationViewerWorkOffsetList> ListAsync(string caseId, string caseOperationId, CancellationToken cancellationToken) =>
        repository.ListAsync(caseId.Trim(), caseOperationId.Trim(), cancellationToken);

    public Task<CaseOperationViewerWorkOffsetWriteResult> SaveAsync(
        string caseId,
        string caseOperationId,
        string viewerMachine,
        IReadOnlyDictionary<string, Dictionary<string, double?>?>? offsets,
        int? expectedVersion,
        EditAuthority authority,
        CancellationToken cancellationToken)
    {
        var machine = ValidateMachine(viewerMachine);
        var values = Validate(offsets);
        return repository.SaveAsync(
            caseId.Trim(), caseOperationId.Trim(), machine, values, expectedVersion,
            SignedInActor.Require(authority), timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<CaseOperationViewerWorkOffsetWriteResult> DeleteAsync(
        string caseId,
        string caseOperationId,
        string viewerMachine,
        int expectedVersion,
        EditAuthority authority,
        CancellationToken cancellationToken) =>
        repository.DeleteAsync(
            caseId.Trim(), caseOperationId.Trim(), ValidateMachine(viewerMachine), expectedVersion,
            SignedInActor.Require(authority), timeProvider.GetUtcNow(), cancellationToken);

    /// <summary>The viewer machine id, lower case; an engine id is letters, digits, '-', '_' and '.' (at most 100).</summary>
    internal static string ValidateMachine(string viewerMachine)
    {
        var machine = viewerMachine.Trim().ToLowerInvariant();
        if (!MachinePattern().IsMatch(machine))
            throw new CaseOperationViewerWorkOffsetValidationException([
                new("viewerMachine", "invalid_viewer_machine",
                    "The viewer machine must be an NC viewer machine id: letters, digits, '-', '_' or '.', at most 100 characters.")]);
        return machine;
    }

    /// <summary>
    /// Labels upper case with single spaces, axes upper case; a null axis is left out. At least one label with a value,
    /// at most <see cref="MaximumLabels"/> labels, every value finite and within ±<see cref="ValueLimit"/>.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Validate(
        IReadOnlyDictionary<string, Dictionary<string, double?>?>? offsets)
    {
        var issues = new List<CaseOperationCamSetupIssue>();
        var result = new SortedDictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);
        foreach (var (rawLabel, axes) in offsets ?? new Dictionary<string, Dictionary<string, double?>?>())
        {
            var label = Spaces().Replace(rawLabel.Trim().ToUpperInvariant(), " ");
            if (!LabelPattern().IsMatch(label))
            {
                issues.Add(new($"offsets.{rawLabel}", "invalid_work_offset",
                    "A work offset must be G54..G59, G54.1 P1..P999, G154 P1..P999, G110..G129 or EXT."));
                continue;
            }
            if (result.ContainsKey(label))
            {
                issues.Add(new($"offsets.{rawLabel}", "duplicate_work_offset", $"{label} is given more than once."));
                continue;
            }
            var row = new SortedDictionary<string, double>(StringComparer.Ordinal);
            foreach (var (rawAxis, value) in axes ?? [])
            {
                var axis = rawAxis.Trim().ToUpperInvariant();
                if (!AxisPattern().IsMatch(axis))
                    issues.Add(new($"offsets.{label}.{rawAxis}", "invalid_axis", "An axis must be one of X, Y, Z, A, B, C, U, V or W."));
                else if (value is null)
                    continue;
                else if (!double.IsFinite(value.Value) || Math.Abs(value.Value) > ValueLimit)
                    issues.Add(new($"offsets.{label}.{axis}", "out_of_range",
                        string.Create(CultureInfo.InvariantCulture, $"{label} {axis} must be a number between -{ValueLimit} and {ValueLimit}.")));
                else if (!row.TryAdd(axis, value.Value))
                    issues.Add(new($"offsets.{label}.{axis}", "duplicate_axis", $"{label} {axis} is given more than once."));
            }
            if (row.Count > 0) result[label] = row;
        }
        if (result.Count == 0 && issues.Count == 0)
            issues.Add(new("offsets", "required", "Give at least one work offset with a value, or delete the work offsets."));
        if (result.Count > MaximumLabels)
            issues.Add(new("offsets", "too_many", $"At most {MaximumLabels} work offsets can be saved."));
        if (issues.Count > 0) throw new CaseOperationViewerWorkOffsetValidationException(issues);
        return result.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    internal static string Serialize(IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> offsets) =>
        JsonSerializer.Serialize(offsets);

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Deserialize(string json) =>
        (JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(json) ?? [])
            .ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, double>)pair.Value, StringComparer.Ordinal);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex MachinePattern();

    // The NC viewer's mill work offsets (engine sanitizeWorkOffsets): EXT, G54..G59, G110..G129, G154 Pn, G54.1 Pn.
    [GeneratedRegex(@"^(EXT|G5[4-9]|G1[12][0-9]|G154 ?P[1-9][0-9]{0,2}|G54\.1 ?P[1-9][0-9]{0,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"^[XYZABCUVW]$", RegexOptions.CultureInvariant)]
    private static partial Regex AxisPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
