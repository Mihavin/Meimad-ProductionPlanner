using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Meimad.Planner.Server.Application.MachineEvidence;

/// <summary>The transport-independent streaming parser of measured work offset messages (exporter definition §20.8).</summary>
internal interface IOffsetEvidenceParser
{
    /// <summary>Feeds one received line; lines that are no work offset records are ignored.</summary>
    OffsetEvidenceParseResult Accept(string line);
}

internal enum OffsetEvidenceParseOutcome
{
    /// <summary>Not a work offset line; nothing changed.</summary>
    Ignored,

    /// <summary>The line was taken into the open session; nothing is complete yet.</summary>
    Pending,

    /// <summary>A complete, well-formed message (<see cref="OffsetEvidenceParseResult.Message"/>), ready for validation.</summary>
    Completed,

    /// <summary>
    /// A complete frame whose content is invalid (bad number, unknown units, duplicate axis, missing
    /// identity): <see cref="OffsetEvidenceParseResult.Message"/> carries it with
    /// <see cref="WorkOffsetMessage.RejectCode"/> so the evidence can be kept as REJECTED.
    /// </summary>
    Rejected,

    /// <summary>A line that does not fit the state machine (value or end without start, session mismatch); nothing is kept.</summary>
    RefusedLine,

    /// <summary>An incomplete session was dropped (a new start, or END with a required axis missing); never persisted.</summary>
    Discarded
}

/// <summary>One step of the parser: what happened and, for a finished frame, the message.</summary>
internal sealed record OffsetEvidenceParseResult(
    OffsetEvidenceParseOutcome Outcome,
    string? Code = null,
    string? Session = null,
    WorkOffsetMessage? Message = null)
{
    internal static readonly OffsetEvidenceParseResult Ignored = new(OffsetEvidenceParseOutcome.Ignored);
}

/// <summary>
/// A complete measured work offset message as the controller sent it, before any matching. Identity
/// values are the upper-case text sent; <see cref="Values"/> are in <see cref="Units"/> (millimetres
/// or inches, as reported), axis letter to value.
/// </summary>
internal sealed record WorkOffsetMessage(
    string? Session,
    long? Sequence,
    string? Machine,
    string? Program,
    string? Operation,
    string? Release,
    string? WorkOffset,
    string? Units,
    string? DeclaredAxes,
    IReadOnlyDictionary<string, double> Values,
    IReadOnlyList<string> RawLines,
    bool SingleLine,
    string? RejectCode)
{
    internal string RawText => string.Join("\n", RawLines);

    /// <summary>
    /// SHA-256 (lower-case hex) of the normalized message: identity, session, units and values in a fixed
    /// order, numbers in invariant round-trip form. The same measurement sent again - by another
    /// transport, with other blanks, padding or field order - has the same hash, which makes ingest
    /// idempotent; any changed value or identity changes it.
    /// </summary>
    internal string ContentHash()
    {
        var builder = new StringBuilder("MEIMAD-WORK-OFFSET-V1");
        void Add(string key, string? value) => builder.Append('|').Append(key).Append('=').Append(value ?? string.Empty);
        Add("session", Session);
        Add("machine", Machine);
        Add("program", Program);
        Add("operation", Operation);
        Add("release", Release);
        Add("offset", WorkOffset);
        Add("units", Units);
        foreach (var axis in WorkOffsetAxes.All)
            Add(axis, Values.TryGetValue(axis, out var value) ? value.ToString("R", CultureInfo.InvariantCulture) : null);
        Add("reject", RejectCode);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

internal static class WorkOffsetAxes
{
    internal static readonly IReadOnlyList<string> All = ["X", "Y", "Z", "A", "B", "C"];
    internal static readonly IReadOnlyList<string> Linear = ["X", "Y", "Z"];
    internal static bool IsRotary(string axis) => axis is "A" or "B" or "C";
}

/// <summary>
/// The §20.9 state machine: <c>Idle → Started(session) → Collecting(values) → Ended</c>. Validation
/// against Server data and persistence happen in <see cref="WorkOffsetIngestService"/>. One parser
/// serves one channel (one Machine's DPRNT output); it is not thread-safe.
/// <list type="bullet">
/// <item>Value or END without START: the line is refused.</item>
/// <item>A START while a session is open drops the open session (incomplete, never persisted).</item>
/// <item>Duplicate axis, invalid number, unknown units, a conflicting offset or units inside a frame: the
/// frame is completed as <see cref="OffsetEvidenceParseOutcome.Rejected"/> at its END.</item>
/// <item>END with a required axis missing: the session is dropped (not persisted).</item>
/// </list>
/// Sequence numbers are kept as evidence only (AGENTS rule 31); they never decide acceptance.
/// </summary>
internal sealed class OffsetEvidenceParser : IOffsetEvidenceParser
{
    /// <summary>A session that never ends must not grow without bound.</summary>
    internal const int MaximumLinesPerSession = 32;

    private Session? open;

    /// <summary>The session currently being collected, if any.</summary>
    internal string? OpenSession => open?.Id;

    public OffsetEvidenceParseResult Accept(string line)
    {
        var normalized = DprntLineNormalizer.Normalize(line);
        if (!normalized.IsWorkOffsetLine) return OffsetEvidenceParseResult.Ignored;
        if (normalized.Record is null)
        {
            // A malformed line inside an open frame spoils the frame; outside one it is simply refused.
            if (open is null) return new(OffsetEvidenceParseOutcome.RefusedLine, normalized.ErrorCode);
            open.RawLines.Add(DprntLineNormalizer.StripControlCharacters(line).Trim());
            open.Poison(normalized.ErrorCode!);
            return new(OffsetEvidenceParseOutcome.Pending, normalized.ErrorCode, open.Id);
        }

        var record = normalized.Record;
        return record.Kind switch
        {
            OffsetRecordKind.Start => Start(record),
            OffsetRecordKind.Values when open is null => SingleLine(record),
            OffsetRecordKind.Values or OffsetRecordKind.Value => Collect(record),
            OffsetRecordKind.End => End(record),
            _ => OffsetEvidenceParseResult.Ignored
        };
    }

    /// <summary>Drops an open session, for example when the channel reconnects; the session is never persisted.</summary>
    internal string? Reset()
    {
        var dropped = open?.Id;
        open = null;
        return dropped;
    }

    private OffsetEvidenceParseResult Start(OffsetWireRecord record)
    {
        var dropped = open;
        var session = new Session(record.Field("SESSION"));
        session.RawLines.Add(record.RawLine);
        session.Apply(record);
        open = session;
        return dropped is null
            ? new(OffsetEvidenceParseOutcome.Pending, null, session.Id)
            : new(OffsetEvidenceParseOutcome.Discarded, "interrupted_by_new_start", dropped.Id);
    }

    private OffsetEvidenceParseResult SingleLine(OffsetWireRecord record)
    {
        // Without an open session a values line is a complete message only when it carries the identity.
        if (record.Field("MACHINE") is null && record.Field("RELEASE") is null
            && record.Field("PROGRAM") is null && record.Field("OPERATION") is null)
            return new(OffsetEvidenceParseOutcome.RefusedLine, "value_without_start", record.Field("SESSION"));
        var session = new Session(record.Field("SESSION")) { SingleLine = true };
        session.RawLines.Add(record.RawLine);
        session.Apply(record);
        return Finish(session);
    }

    private OffsetEvidenceParseResult Collect(OffsetWireRecord record)
    {
        if (open is null)
            return new(OffsetEvidenceParseOutcome.RefusedLine, "value_without_start", record.Field("SESSION"));
        var session = record.Field("SESSION");
        if (session is not null && open.Id is not null && session != open.Id)
            return new(OffsetEvidenceParseOutcome.RefusedLine, "session_mismatch", session);
        open.RawLines.Add(record.RawLine);
        if (open.RawLines.Count > MaximumLinesPerSession) open.Poison("too_many_lines");
        if (session is not null && open.Id is null) open.Id = session;
        open.Apply(record);
        return new(OffsetEvidenceParseOutcome.Pending, open.Error, open.Id);
    }

    private OffsetEvidenceParseResult End(OffsetWireRecord record)
    {
        if (open is null)
            return new(OffsetEvidenceParseOutcome.RefusedLine, "end_without_start", record.Field("SESSION"));
        var session = record.Field("SESSION");
        if (session is not null && open.Id is not null && session != open.Id)
            return new(OffsetEvidenceParseOutcome.RefusedLine, "session_mismatch", session);
        var finished = open;
        open = null;
        finished.RawLines.Add(record.RawLine);
        if (session is not null && finished.Id is null) finished.Id = session;
        foreach (var field in record.Fields)
        {
            if (field.Key is "SESSION" or "MACROVERSION") continue;
            finished.Poison("unexpected_end_field");
        }
        return Finish(finished);
    }

    private static OffsetEvidenceParseResult Finish(Session session)
    {
        if (session.Error is null)
        {
            // A frame whose declared axes did not all arrive is incomplete: it is never persisted.
            var missing = session.RequiredAxes().Where(axis => !session.Values.ContainsKey(axis)).ToArray();
            if (missing.Length > 0)
                return new(OffsetEvidenceParseOutcome.Discarded, "missing_axis", session.Id);
            if (session.Machine is null) session.Poison("machine_missing");
            else if (session.Release is null && session.Program is null) session.Poison("release_missing");
            else if (session.Operation is null) session.Poison("operation_missing");
            else if (session.WorkOffset is null) session.Poison("offset_missing");
            else if (session.Units is null) session.Poison("units_missing");
        }
        var message = new WorkOffsetMessage(
            session.Id, session.Sequence, session.Machine, session.Program, session.Operation, session.Release,
            session.WorkOffset, session.Units, session.DeclaredAxes,
            new Dictionary<string, double>(session.Values, StringComparer.Ordinal),
            session.RawLines.ToArray(), session.SingleLine, session.Error);
        return new(
            session.Error is null ? OffsetEvidenceParseOutcome.Completed : OffsetEvidenceParseOutcome.Rejected,
            session.Error, session.Id, message);
    }

    /// <summary>Parses a coordinate printed by a control: optional sign, digits with an optional decimal point.</summary>
    internal static bool TryParseNumber(string text, out double value)
    {
        value = 0;
        if (text.Length is 0 or > 24) return false;
        var digits = 0;
        var dots = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character is '+' or '-' && index == 0) continue;
            if (character == '.') { dots++; continue; }
            if (character is < '0' or > '9') return false;
            digits++;
        }
        if (digits == 0 || dots > 1) return false;
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var parsed)) return false;
        value = (double)parsed;
        return double.IsFinite(value);
    }

    /// <summary><c>MM</c> or <c>INCH</c> from what a control prints: words, or the modal G-code number #4006 holds (21 = G21 mm, 20 = G20 inch).</summary>
    internal static string? NormalizeUnits(string text)
    {
        switch (text)
        {
            case "MM" or "METRIC" or "MILLIMETER" or "MILLIMETRE":
                return "MM";
            case "IN" or "INCH" or "INCHES":
                return "INCH";
        }
        if (!TryParseNumber(text, out var number)) return null;
        return number switch
        {
            21 => "MM",
            20 => "INCH",
            _ => null
        };
    }

    private sealed class Session(string? id)
    {
        internal string? Id { get; set; } = id;
        internal bool SingleLine { get; init; }
        internal List<string> RawLines { get; } = [];
        internal Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);
        internal string? Error { get; private set; }
        internal string? Machine { get; private set; }
        internal string? Program { get; private set; }
        internal string? Operation { get; private set; }
        internal string? Release { get; private set; }
        internal string? WorkOffset { get; private set; }
        internal string? Units { get; private set; }
        internal string? DeclaredAxes { get; private set; }
        internal long? Sequence { get; private set; }
        private string? pendingAxis;

        internal void Poison(string code) => Error ??= code;

        internal IEnumerable<string> RequiredAxes() => DeclaredAxes is null
            ? WorkOffsetAxes.Linear
            : DeclaredAxes.Select(character => character.ToString());

        internal void Apply(OffsetWireRecord record)
        {
            foreach (var (key, value) in record.Fields)
            {
                switch (key)
                {
                    case "SESSION":
                        break;
                    case "SEQUENCE":
                        if (long.TryParse(value.TrimEnd('.'), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
                            Sequence ??= sequence;
                        else Poison("invalid_sequence");
                        break;
                    case "MACROVERSION":
                        break;
                    case "MACHINE":
                        Set(ref machine, value, "machine_conflict");
                        Machine = machine;
                        break;
                    case "PROGRAM":
                        Set(ref program, value, "program_conflict");
                        Program = program;
                        break;
                    case "OPERATION":
                        Set(ref operation, value, "operation_conflict");
                        Operation = operation;
                        break;
                    case "RELEASE":
                        Set(ref release, value, "release_conflict");
                        Release = release;
                        break;
                    case "OFFSET":
                        var name = WorkOffsetNames.Parse(value);
                        if (name is null) { Poison("invalid_work_offset"); break; }
                        Set(ref workOffset, name.Canonical, "offset_mismatch");
                        WorkOffset = workOffset;
                        break;
                    case "UNITS":
                        var units = NormalizeUnits(value);
                        if (units is null) { Poison("invalid_units"); break; }
                        Set(ref unitsValue, units, "units_conflict");
                        Units = unitsValue;
                        break;
                    case "AXES":
                        var letters = value.Replace(",", string.Empty, StringComparison.Ordinal);
                        if (letters.Length == 0 || letters.Any(letter => !WorkOffsetAxes.All.Contains(letter.ToString()))
                            || letters.Distinct().Count() != letters.Length)
                        {
                            Poison("invalid_axes");
                            break;
                        }
                        Set(ref axes, letters, "axes_conflict");
                        DeclaredAxes = axes;
                        break;
                    case "AXIS":
                        if (!WorkOffsetAxes.All.Contains(value)) { Poison("invalid_axis"); break; }
                        pendingAxis = value;
                        break;
                    case "VALUE":
                        if (pendingAxis is null) { Poison("value_without_axis"); break; }
                        AddValue(pendingAxis, value);
                        pendingAxis = null;
                        break;
                    default:
                        AddValue(key, value);
                        break;
                }
            }
            if (pendingAxis is not null)
            {
                Poison("axis_without_value");
                pendingAxis = null;
            }
        }

        private string? machine;
        private string? program;
        private string? operation;
        private string? release;
        private string? workOffset;
        private string? unitsValue;
        private string? axes;

        private void Set(ref string? field, string value, string conflictCode)
        {
            if (field is null) field = value;
            else if (field != value) Poison(conflictCode);
        }

        private void AddValue(string axis, string text)
        {
            if (!TryParseNumber(text, out var value)) { Poison("invalid_number"); return; }
            if (!Values.TryAdd(axis, value)) Poison("duplicate_axis");
        }
    }
}
