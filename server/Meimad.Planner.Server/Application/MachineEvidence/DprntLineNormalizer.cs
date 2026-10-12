using System.Text;

namespace Meimad.Planner.Server.Application.MachineEvidence;

/// <summary>The four record kinds of the measured work offset message (exporter definition §20.2).</summary>
internal enum OffsetRecordKind
{
    /// <summary><c>MEIMAD/OFFSET_START</c>: opens a session and carries the identity fields.</summary>
    Start,

    /// <summary><c>MEIMAD/OFFSET</c>: axis values (several per line), or the whole single-line message.</summary>
    Values,

    /// <summary><c>MEIMAD/OFFSET_VALUE</c>: one axis value of an open session.</summary>
    Value,

    /// <summary><c>MEIMAD/OFFSET_END</c>: closes the session.</summary>
    End
}

/// <summary>
/// One received work offset line after normalization: its kind, its fields in the order they were sent
/// (keys upper-case, values upper-case with every blank removed) and the text it was normalized from.
/// </summary>
internal sealed record OffsetWireRecord(
    OffsetRecordKind Kind,
    IReadOnlyList<KeyValuePair<string, string>> Fields,
    string RawLine)
{
    internal string? Field(string key)
    {
        foreach (var field in Fields)
            if (field.Key == key) return field.Value;
        return null;
    }
}

/// <summary>Result of normalizing one line: a record, not a work offset line, or a malformed work offset line.</summary>
internal sealed record OffsetLineNormalization(OffsetWireRecord? Record, bool IsWorkOffsetLine, string? ErrorCode)
{
    internal static readonly OffsetLineNormalization NotWorkOffset = new(null, false, null);
}

/// <summary>
/// Turns one DPRNT/PUT line into a <see cref="OffsetWireRecord"/>, independent of the transport
/// (TCP, serial bridge, print file, FTP) and of the control that printed it.
/// <para>
/// Two spellings of the same records are accepted (docs/api-contract.md, "Measured work offsets"):
/// the definition's <c>MEIMAD/OFFSET_START;machine=..;offset=G54</c> form, and the CNC-safe
/// <c>MEIMAD/OFFSET-START/MACHINE/10/OFFSET/G54</c> form that FANUC-family DPRNT and Okuma PUT
/// can print (no <c>;</c>, <c>=</c>, lower-case letters or <c>_</c>, which those controls cannot
/// print or treat as end of block). Control codes (FANUC DC2/DC4, NUL padding) and every blank
/// (FANUC parameter 6001#1, Okuma <c>PUT VC5,6,0</c> padding) are removed first, exactly as for the
/// other <c>MEIMAD/</c> event lines.
/// </para>
/// </summary>
internal static class DprntLineNormalizer
{
    internal const int MaximumLineLength = 1024;
    private const int MaximumValueLength = 64;

    private static readonly IReadOnlyDictionary<string, OffsetRecordKind> Kinds =
        new Dictionary<string, OffsetRecordKind>(StringComparer.Ordinal)
        {
            ["OFFSET_START"] = OffsetRecordKind.Start,
            ["OFFSET"] = OffsetRecordKind.Values,
            ["OFFSET_VALUE"] = OffsetRecordKind.Value,
            ["OFFSET_END"] = OffsetRecordKind.End
        };

    /// <summary>Field names after alias resolution; anything else is refused so a typo cannot drop a value silently.</summary>
    private static readonly IReadOnlyDictionary<string, string> KeyAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MACHINE"] = "MACHINE",
            ["PROGRAM"] = "PROGRAM",
            ["OPERATION"] = "OPERATION",
            ["OP"] = "OPERATION",
            ["RELEASE"] = "RELEASE",
            ["NCRELEASE"] = "RELEASE",
            ["NC_RELEASE"] = "RELEASE",
            ["OFFSET"] = "OFFSET",
            ["WORKOFFSET"] = "OFFSET",
            ["WORK_OFFSET"] = "OFFSET",
            ["UNITS"] = "UNITS",
            ["UNIT"] = "UNITS",
            ["AXES"] = "AXES",
            ["AXIS"] = "AXIS",
            ["VALUE"] = "VALUE",
            ["SESSION"] = "SESSION",
            ["SEQ"] = "SEQUENCE",
            ["SEQUENCE"] = "SEQUENCE",
            ["MACROVERSION"] = "MACROVERSION",
            ["V"] = "MACROVERSION",
            ["X"] = "X",
            ["Y"] = "Y",
            ["Z"] = "Z",
            ["A"] = "A",
            ["B"] = "B",
            ["C"] = "C"
        };

    /// <summary>True for a line that claims to be a work offset record (<c>MEIMAD/OFFSET...</c>), well-formed or not.</summary>
    internal static bool IsWorkOffsetLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        var compact = Compact(line);
        return compact.StartsWith("MEIMAD/OFFSET", StringComparison.OrdinalIgnoreCase);
    }

    internal static OffsetLineNormalization Normalize(string? line)
    {
        if (string.IsNullOrEmpty(line)) return OffsetLineNormalization.NotWorkOffset;
        var raw = StripControlCharacters(line).Trim();
        var compact = Compact(raw);
        if (!compact.StartsWith("MEIMAD/OFFSET", StringComparison.OrdinalIgnoreCase))
            return OffsetLineNormalization.NotWorkOffset;
        if (compact.Length > MaximumLineLength) return Fail("line_too_long");
        var body = compact["MEIMAD/".Length..];

        string keyword;
        var fields = new List<KeyValuePair<string, string>>();
        if (body.Contains(';') || body.Contains('='))
        {
            // The definition's form: MEIMAD/OFFSET_START;machine=...;offset=G54
            var parts = body.Split(';');
            keyword = parts[0];
            foreach (var part in parts.Skip(1))
            {
                if (part.Length == 0) continue;
                var equals = part.IndexOf('=');
                if (equals <= 0) return Fail("invalid_field");
                fields.Add(new(part[..equals], part[(equals + 1)..]));
            }
        }
        else
        {
            // The CNC-safe form: MEIMAD/OFFSET-START/MACHINE/10/OFFSET/G54
            var parts = body.Split('/');
            keyword = parts[0];
            var rest = parts.Skip(1).ToList();
            if (rest.Count > 0 && rest[^1].Length == 0) rest.RemoveAt(rest.Count - 1);
            if (rest.Count % 2 != 0) return Fail("invalid_field_pairs");
            for (var index = 0; index < rest.Count; index += 2)
                fields.Add(new(rest[index], rest[index + 1]));
        }

        if (!Kinds.TryGetValue(keyword.ToUpperInvariant().Replace('-', '_'), out var kind))
            return Fail("unsupported_record");

        var normalized = new List<KeyValuePair<string, string>>(fields.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            if (!KeyAliases.TryGetValue(key.ToUpperInvariant().Replace('-', '_'), out var canonical))
                return Fail("unsupported_field");
            if (!seen.Add(canonical)) return Fail(IsAxis(canonical) ? "duplicate_axis" : "duplicate_field");
            var upper = value.ToUpperInvariant();
            if (upper.Length is 0 or > MaximumValueLength || !upper.All(IsValueCharacter))
                return Fail("invalid_field_value");
            normalized.Add(new(canonical, upper));
        }
        return new(new OffsetWireRecord(kind, normalized, raw), true, null);
    }

    internal static bool IsAxis(string key) => key is "X" or "Y" or "Z" or "A" or "B" or "C";

    /// <summary>
    /// Removes the ASCII control codes some controls wrap around DPRNT output (FANUC DC2 on POPEN and
    /// DC4 on PCLOS, serial NUL padding, DEL); tabs become blanks.
    /// </summary>
    internal static string StripControlCharacters(string line)
    {
        if (line.All(character => character >= ' ' && character != '\u007f')) return line;
        var builder = new StringBuilder(line.Length);
        foreach (var character in line)
        {
            if (character == '\t') builder.Append(' ');
            else if (character >= ' ' && character != '\u007f') builder.Append(character);
        }
        return builder.ToString();
    }

    private static string Compact(string line)
    {
        var builder = new StringBuilder(line.Length);
        foreach (var character in line)
        {
            if (character < ' ' || character == '\u007f' || char.IsWhiteSpace(character)) continue;
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static bool IsValueCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '+' or '_' or ':';

    private static OffsetLineNormalization Fail(string code) => new(null, true, code);
}
