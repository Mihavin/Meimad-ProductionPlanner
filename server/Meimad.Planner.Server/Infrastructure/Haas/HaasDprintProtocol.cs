using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Meimad.Planner.Server.Application.ProductionRuns;

namespace Meimad.Planner.Server.Infrastructure.Haas;

internal sealed record HaasDprintEvent(
    string EventType, string SourceEventId, long Sequence, int MacroVersion,
    string? ProductionRunId, string? ProgramIdentity,
    int? OffsetReleaseToken, int? Nonce, string RawLine);

/// <summary>
/// Strict CNC-safe v1 wire format:
/// MEIMAD/V/1/EVENT/OLC/ID/.../SEQ/.../MACROVERSION/...[ /RUN/...][ /PROGRAM/...][ /OFFSETRELEASE/...][ /NONCE/...]
/// </summary>
internal static partial class HaasDprintProtocol
{
    internal const int MaximumLineBytes = 512;
    private static readonly string[] OptionalOrder = ["RUN", "PROGRAM", "OFFSETRELEASE", "NONCE"];
    private static readonly IReadOnlyDictionary<string, string> EventTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OLC"] = "OFFSET_LOADER_COMPLETED",
            ["SVR"] = "SETUP_VERIFICATION_REQUESTED",
            ["SVS"] = "SETUP_VERIFICATION_SUCCEEDED",
            ["SVF"] = "SETUP_VERIFICATION_FAILED",
            ["STQ"] = "SEND_TO_QC",
            ["QCP"] = "QC_PASS",
            ["QCF"] = "QC_FAIL",
            ["CST"] = "CYCLE_START",
            ["CEN"] = "CYCLE_END",
            ["CIN"] = "CYCLE_INTERRUPTED",
            ["PSO"] = "PRODUCTION_SESSION_OPENED",
            ["PSC"] = "PRODUCTION_SESSION_CLOSED"
        };

    internal static bool TryParse(string? line, out HaasDprintEvent? value, out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(line)) return Fail("empty_line", out error);
        var trimmed = line.Trim();
        if (!CncSafeLine().IsMatch(trimmed)
            || Encoding.ASCII.GetByteCount(trimmed) > MaximumLineBytes)
            return Fail("invalid_encoding_or_length", out error);
        var segments = trimmed.Split('/');
        if (segments.Length < 11 || segments.Length % 2 == 0 || segments[0] != "MEIMAD")
            return Fail("invalid_prefix", out error);
        var fields = new List<KeyValuePair<string, string>>((segments.Length - 1) / 2);
        for (var index = 1; index < segments.Length; index += 2)
            fields.Add(new(segments[index], segments[index + 1]));
        if (fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != fields.Count)
            return Fail("duplicate_field", out error);
        var required = new[] { "V", "EVENT", "ID", "SEQ", "MACROVERSION" };
        if (fields.Count < required.Length
            || !fields.Take(required.Length).Select(field => field.Key).SequenceEqual(required))
            return Fail("invalid_field_order", out error);
        var optionalKeys = fields.Skip(required.Length).Select(field => field.Key).ToArray();
        var indexes = optionalKeys.Select(key => Array.IndexOf(OptionalOrder, key)).ToArray();
        if (indexes.Any(index => index < 0) || !indexes.SequenceEqual(indexes.Order()))
            return Fail("invalid_optional_field", out error);
        var map = fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
        if (map["V"] != "1") return Fail("unsupported_protocol_version", out error);
        if (!EventTypes.TryGetValue(map["EVENT"], out var eventType)
            || !ProductionRunWorkflowEventTypes.All.Contains(eventType))
            return Fail("unsupported_event", out error);
        if (!SafeIdentity().IsMatch(map["ID"])) return Fail("invalid_event_id", out error);
        if (!long.TryParse(map["SEQ"], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || sequence < 0) return Fail("invalid_sequence", out error);
        if (!int.TryParse(map["MACROVERSION"], NumberStyles.None, CultureInfo.InvariantCulture, out var macroVersion)
            || macroVersion <= 0) return Fail("invalid_macro_version", out error);
        if (!OptionalIdentity(map, "RUN", out var runId)
            || !OptionalIdentity(map, "PROGRAM", out var program))
            return Fail("invalid_identity", out error);
        if (!OptionalPositiveInt(map, "OFFSETRELEASE", out var releaseToken)
            || !OptionalNonnegativeInt(map, "NONCE", out var nonce))
            return Fail("invalid_numeric_field", out error);
        if (eventType == "OFFSET_LOADER_COMPLETED" && (!releaseToken.HasValue || !nonce.HasValue))
            return Fail("missing_offset_evidence", out error);
        if (eventType != "OFFSET_LOADER_COMPLETED" && (releaseToken.HasValue || nonce.HasValue))
            return Fail("unexpected_offset_evidence", out error);
        value = new(eventType, map["ID"], sequence, macroVersion,
            runId, program, releaseToken, nonce, trimmed);
        return true;
    }

    private static bool OptionalIdentity(
        IReadOnlyDictionary<string, string> values, string key, out string? value)
    {
        value = null;
        if (!values.TryGetValue(key, out var present)) return true;
        if (!SafeIdentity().IsMatch(present)) return false;
        value = present;
        return true;
    }
    private static bool OptionalPositiveInt(
        IReadOnlyDictionary<string, string> values, string key, out int? value) =>
        OptionalInt(values, key, 1, out value);
    private static bool OptionalNonnegativeInt(
        IReadOnlyDictionary<string, string> values, string key, out int? value) =>
        OptionalInt(values, key, 0, out value);
    private static bool OptionalInt(
        IReadOnlyDictionary<string, string> values, string key, int minimum, out int? value)
    {
        value = null;
        if (!values.TryGetValue(key, out var present)) return true;
        if (!int.TryParse(present, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum) return false;
        value = parsed;
        return true;
    }
    private static bool Fail(string code, out string? error) { error = code; return false; }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentity();

    [GeneratedRegex("^[A-Z0-9/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CncSafeLine();
}
