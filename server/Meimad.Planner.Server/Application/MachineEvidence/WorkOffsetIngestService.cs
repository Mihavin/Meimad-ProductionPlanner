using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Configuration;

namespace Meimad.Planner.Server.Application.MachineEvidence;

/// <summary>
/// Validates, matches and stores measured work offsets (exporter definition §20.10-§20.14). The same
/// rules apply to a message read from a Machine's DPRNT output and to one posted to the ingest
/// endpoint. Ingest is idempotent: the same message (same normalized hash) returns the stored record.
/// <list type="number">
/// <item>Same hash: duplicate, nothing new is stored.</item>
/// <item>Same session with other content: REJECTED (<c>session_reused</c>).</item>
/// <item>Content the parser refused (number, units, duplicate axis, missing identity): REJECTED.</item>
/// <item>Machine, NC release or Operation not matched unambiguously: UNMATCHED, never applied.</item>
/// <item>Work offset the Machine's NC dialect does not have, or a value out of range: REJECTED.</item>
/// <item>An NC release that is no longer the current G-code, a measurement older than the current one,
/// or a change beyond the review thresholds: REVIEW_REQUIRED, never current automatically.</item>
/// <item>Otherwise VALID: it becomes current and the previous current record becomes SUPERSEDED.</item>
/// </list>
/// Event sequence numbers are evidence only (AGENTS rule 31): currency is decided by event identity
/// (hash, session) and the measured time, never by sequence continuity.
/// </summary>
internal sealed class WorkOffsetIngestService(
    IWorkOffsetRepository repository,
    WorkOffsetOptions options,
    TimeProvider timeProvider,
    ILogger<WorkOffsetIngestService> logger)
{
    internal static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);
    private const double InchToMillimetre = 25.4;

    internal async Task<WorkOffsetIngestResult> IngestAsync(WorkOffsetEvidence evidence, CancellationToken token = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var unit = await repository.BeginIngestAsync(token);
        var existing = await unit.FindByHashAsync(evidence.RawMessageHash, token);
        if (existing is not null) return new WorkOffsetIngestResult(true, existing);

        var session = string.IsNullOrWhiteSpace(evidence.Session)
            ? "H-" + evidence.RawMessageHash[..Math.Min(16, evidence.RawMessageHash.Length)]
            : evidence.Session.Trim();
        var sessionKey = (evidence.SourceMachineId ?? "CLAIM:" + (evidence.Machine ?? "?")) + "|" + session;
        var claim = new WorkOffsetClaim(evidence.Machine, evidence.Program, evidence.Operation, evidence.Release);
        var measuredAt = evidence.MeasuredAt ?? now;
        var machines = await unit.MachinesAsync(token);
        var sourceMachine = evidence.SourceMachineId is null
            ? null
            : machines.FirstOrDefault(machine => machine.MachineId == evidence.SourceMachineId);

        WorkOffsetRow Row(string status, string? reason, WorkOffsetMachineCandidate? machine = null,
            WorkOffsetReleaseCandidate? release = null, string? workOffset = null,
            IReadOnlyDictionary<string, double>? millimetres = null, string? supersedes = null) => new(
            Guid.NewGuid().ToString("N"),
            machine?.MachineId ?? sourceMachine?.MachineId,
            release?.CaseOperationId,
            release?.ReleaseId,
            workOffset ?? evidence.WorkOffset ?? "UNKNOWN",
            Value(millimetres, "X"), Value(millimetres, "Y"), Value(millimetres, "Z"),
            Value(millimetres, "A"), Value(millimetres, "B"), Value(millimetres, "C"),
            evidence.Units,
            evidence.Source,
            session,
            sessionKey,
            evidence.Sequence,
            claim,
            evidence.RawMessage,
            evidence.RawMessageHash,
            measuredAt,
            now,
            evidence.ReceivedBy,
            status,
            reason,
            supersedes);

        // Evidence that is not matched or not valid keeps its values in millimetres when the units are
        // known; with unknown units only the raw message holds them.
        var reported = evidence.Units is "MM" or "INCH"
            ? ToMillimetres(evidence.Values, evidence.Units)
            : new Dictionary<string, double>(StringComparer.Ordinal);

        double? Value(IReadOnlyDictionary<string, double>? values, string axis)
        {
            var source = values ?? reported;
            return source.TryGetValue(axis, out var value) ? value : null;
        }

        async Task<WorkOffsetIngestResult> StoreAsync(WorkOffsetRow row)
        {
            var record = await unit.InsertAsync(row, token);
            await unit.CommitAsync(token);
            if (row.Status != WorkOffsetStatuses.Valid)
                logger.LogWarning(
                    "Measured work offset stored as {Status}. MachineId={MachineId} Session={Session} Reason={Reason}",
                    row.Status, row.MachineId ?? evidence.Machine, session, row.StatusReason);
            else
                logger.LogInformation(
                    "Measured work offset is current. MachineId={MachineId} Operation={Operation} Release={Release} Offset={Offset}",
                    row.MachineId, row.CaseOperationId, row.ReleaseId, row.WorkOffset);
            return new WorkOffsetIngestResult(false, record);
        }

        // Matching first, so that rejected evidence still shows under the Machine and Operation it names.
        var (machine, release, unmatched) = await MatchAsync(unit, machines, sourceMachine, evidence, token);
        if (!string.IsNullOrWhiteSpace(evidence.Session) && await unit.FindBySessionAsync(sessionKey, token) is not null)
            return await StoreAsync(Row(WorkOffsetStatuses.Rejected, "session_reused", machine, release));
        if (evidence.RejectCode is not null)
            return await StoreAsync(Row(WorkOffsetStatuses.Rejected, evidence.RejectCode, machine, release));
        if (unmatched is not null)
            return await StoreAsync(Row(unmatched == "release_missing" ? WorkOffsetStatuses.Rejected : WorkOffsetStatuses.Unmatched,
                unmatched, machine));
        if (machine is null || release is null) throw new InvalidOperationException("A matched message has a Machine and a release.");

        // Content: the dialect's work offset name, units, values in range.
        var parsedName = WorkOffsetNames.Parse(evidence.WorkOffset);
        var workOffset = parsedName is null ? null : NcDialects.Profile(machine.NcDialect).NormalizeWorkOffset(parsedName);
        if (workOffset is null)
            return await StoreAsync(Row(WorkOffsetStatuses.Rejected, "unsupported_work_offset", machine, release));
        if (evidence.Units is not ("MM" or "INCH"))
            return await StoreAsync(Row(WorkOffsetStatuses.Rejected, "units_missing", machine, release, workOffset));
        if (evidence.Values.Any(pair => !WorkOffsetAxes.All.Contains(pair.Key) || !double.IsFinite(pair.Value)))
            return await StoreAsync(Row(WorkOffsetStatuses.Rejected, "invalid_number", machine, release, workOffset));
        var millimetres = reported;
        if (!WorkOffsetAxes.Linear.Any(millimetres.ContainsKey))
            return await StoreAsync(Row(WorkOffsetStatuses.Rejected, "axis_missing", machine, release, workOffset));
        if (millimetres.Any(pair => WorkOffsetAxes.IsRotary(pair.Key)
                ? Math.Abs(pair.Value) > options.MaximumRotaryDeg
                : Math.Abs(pair.Value) > options.MaximumLinearMm))
            return await StoreAsync(Row(WorkOffsetStatuses.Rejected, "value_out_of_range", machine, release, workOffset, millimetres));

        // Currency: stale NC release, older than the current measurement, or a large change need review.
        var current = await unit.CurrentAsync(machine.MachineId, release.CaseOperationId, release.ReleaseId, workOffset, token);
        string? reviewReason = null;
        if (!release.IsCurrent) reviewReason = "stale_release";
        else if (current is not null && measuredAt < current.MeasuredAt) reviewReason = "older_than_current";
        else if (current is not null && LargeChange(current, millimetres)) reviewReason = "large_change";
        return reviewReason is null
            ? await StoreAsync(Row(WorkOffsetStatuses.Valid, null, machine, release, workOffset, millimetres, current?.WorkOffsetId))
            : await StoreAsync(Row(WorkOffsetStatuses.ReviewRequired, reviewReason, machine, release, workOffset, millimetres));
    }

    /// <summary>
    /// The Machine (it must be the Machine whose channel delivered the message), the NC release (its
    /// six-digit NC identity, its release ID or its file hash; the program's NC identity stands in when no
    /// release is named) and the Operation (its number must be the release's Operation number). The
    /// reason is set when any of them is not matched unambiguously; what was matched is returned anyway.
    /// </summary>
    private static async Task<(WorkOffsetMachineCandidate? Machine, WorkOffsetReleaseCandidate? Release, string? Unmatched)> MatchAsync(
        IWorkOffsetIngestUnit unit, IReadOnlyList<WorkOffsetMachineCandidate> machines,
        WorkOffsetMachineCandidate? sourceMachine, WorkOffsetEvidence evidence, CancellationToken token)
    {
        var named = MatchMachines(machines, evidence.Machine);
        WorkOffsetMachineCandidate machine;
        if (sourceMachine is not null)
        {
            if (!named.Any(candidate => candidate.MachineId == sourceMachine.MachineId)) return (null, null, "wrong_machine");
            machine = sourceMachine;
        }
        else if (named.Count == 1)
        {
            machine = named[0];
        }
        else
        {
            return (null, null, named.Count == 0 ? "unknown_machine" : "ambiguous_machine");
        }
        if (!machine.IsActive) return (machine, null, "machine_inactive");

        var programIdentity = ProgramIdentity(evidence.Program);
        var reference = evidence.Release ?? programIdentity;
        if (reference is null) return (machine, null, "release_missing");
        var releases = await unit.FindReleasesAsync(reference, token);
        if (releases.Count == 0) return (machine, null, "unknown_release");
        if (releases.Count > 1) return (machine, null, "ambiguous_release");
        var release = releases[0];
        if (programIdentity is not null && programIdentity.Length == 6 && release.NcIdentityToken is { } identity
            && programIdentity != identity.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return (machine, null, "program_release_mismatch");
        if (!OperationMatches(evidence.Operation, release)) return (machine, null, "operation_mismatch");
        return (machine, release, null);
    }

    private static Dictionary<string, double> ToMillimetres(IReadOnlyDictionary<string, double> values, string units)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (axis, value) in values)
        {
            if (!WorkOffsetAxes.All.Contains(axis) || !double.IsFinite(value)) continue;
            var converted = WorkOffsetAxes.IsRotary(axis) || units == "MM" ? value : value * InchToMillimetre;
            result[axis] = Math.Round(converted, 6);
        }
        return result;
    }

    /// <summary>The Machines the message's <c>machine</c> text names: by ID or by number, letters and digits only, any case.</summary>
    internal static IReadOnlyList<WorkOffsetMachineCandidate> MatchMachines(
        IReadOnlyList<WorkOffsetMachineCandidate> machines, string? claimed)
    {
        if (string.IsNullOrWhiteSpace(claimed)) return [];
        var key = Key(claimed);
        if (key.Length == 0) return [];
        var byId = machines.Where(machine => Key(machine.MachineId) == key).ToArray();
        if (byId.Length > 0) return byId;
        return machines.Where(machine => Key(machine.Number) == key).ToArray();
    }

    private static string Key(string value) =>
        new(value.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    /// <summary>The NC identity a program text carries: <c>O654321</c>, <c>654321</c> or <c>O01500</c> without the O and leading zeros; null for none.</summary>
    internal static string? ProgramIdentity(string? program)
    {
        if (string.IsNullOrWhiteSpace(program)) return null;
        var text = program.Trim().ToUpperInvariant();
        if (text.StartsWith('O')) text = text[1..];
        if (text.Length == 0 || !text.All(char.IsAsciiDigit)) return null;
        var trimmed = text.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    private static bool OperationMatches(string? claimed, WorkOffsetReleaseCandidate release)
    {
        if (string.IsNullOrWhiteSpace(claimed)) return false;
        var text = claimed.Trim().ToUpperInvariant();
        if (text.StartsWith("OP", StringComparison.Ordinal)) text = text[2..];
        if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
            return number == release.OperationNumber;
        return string.Equals(claimed.Trim(), release.CaseOperationId, StringComparison.OrdinalIgnoreCase);
    }

    private bool LargeChange(WorkOffsetRecord current, IReadOnlyDictionary<string, double> next)
    {
        double? Previous(string axis) => axis switch
        {
            "X" => current.PositionMm.X,
            "Y" => current.PositionMm.Y,
            "Z" => current.PositionMm.Z,
            "A" => current.RotationDeg.A,
            "B" => current.RotationDeg.B,
            _ => current.RotationDeg.C
        };
        foreach (var axis in WorkOffsetAxes.All)
        {
            var before = Previous(axis);
            var hasAfter = next.TryGetValue(axis, out var after);
            if (before is null || !hasAfter) continue;
            var limit = WorkOffsetAxes.IsRotary(axis) ? options.ReviewRotaryThresholdDeg : options.ReviewLinearThresholdMm;
            if (Math.Abs(after - before.Value) > limit) return true;
        }
        return false;
    }
}
