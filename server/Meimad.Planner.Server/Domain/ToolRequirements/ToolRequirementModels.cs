using System.Globalization;
using System.Text.RegularExpressions;

namespace Meimad.Planner.Server.Domain.ToolRequirements;

/// <summary>
/// One planned use of a tool: the rows of one operation's released tool table that name the same
/// tool (name and diameter), while that operation holds its Machine on the Timeline.
/// </summary>
internal sealed record ToolUse(
    string MaterialGroup,
    string ToolName,
    double? Diameter,
    string ToolType,
    string ToolTypeSource,
    int Copies,
    IReadOnlyList<string> ToolNumbers,
    string? Holder,
    double? Length,
    string MachineId,
    string MachineLabel,
    string OperationId,
    string WorkOrderNumber,
    string PartNumber,
    int OperationNumber,
    string OperationName,
    string? Material,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

/// <summary>Where one physical copy of a tool is needed, in time order.</summary>
internal sealed record ToolCopyStop(string MachineId, string MachineLabel, DateTimeOffset From, DateTimeOffset To);

internal sealed record ToolCopyRoute(int CopyNumber, IReadOnlyList<ToolCopyStop> Stops);

/// <summary>
/// A tool family member needed in the period for one material group: how many physical copies the
/// plan needs at its busiest moment, on which Machines, and how the copies move between them.
/// </summary>
internal sealed record ToolRequirement(
    string MaterialGroup,
    string ToolType,
    string ToolTypeSource,
    double? Diameter,
    string ToolName,
    int CopiesNeeded,
    IReadOnlyList<string> Machines,
    int MachineChanges,
    DateTimeOffset FirstNeed,
    DateTimeOffset LastNeed,
    IReadOnlyList<ToolCopyRoute> Routes,
    IReadOnlyList<ToolUse> Uses);

/// <summary>
/// Groups part materials so a tool that cuts one group is never counted as available for another
/// (owner decision 2026-09-27). The text is the Case material, else the Kitaron raw material of the
/// Work Order, e.g. "AL 7050-T7451 AMS 4050H Plate 2.5"" or "TI-6AL-4V-ANNEALED AMS 4911L".
/// </summary>
internal static class MaterialGroups
{
    internal const string Aluminum = "ALUMINUM";
    internal const string Titanium = "TITANIUM";
    internal const string Stainless = "STAINLESS";
    internal const string Nickel = "NICKEL";
    internal const string Steel = "STEEL";
    internal const string Copper = "COPPER";
    internal const string Plastic = "PLASTIC";
    internal const string Unknown = "UNKNOWN";

    internal static readonly IReadOnlyList<string> Order =
        [Aluminum, Titanium, Stainless, Nickel, Steel, Copper, Plastic, Unknown];

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

    // Checked in this order: titanium before aluminum ("TI-6AL-4V"), stainless and nickel before steel.
    private static readonly (string Group, Regex Pattern)[] Patterns =
    [
        (Titanium, Pattern(@"\bTI\b|\bTI-|TITAN|6AL-?4V")),
        (Stainless, Pattern(@"STAINLESS|\bCRES\b|\b1[357]-[4578]\s?(PH|MO)\b|\b(303|304|316|321|347|410|416|420|430|440)[A-Z]?\b|\bA286\b")),
        (Nickel, Pattern(@"INCONEL|HASTELLOY|MONEL|NICKEL|WASPALOY|\b(625|718)\b")),
        (Aluminum, Pattern(@"\bAL\b|\bAL-|ALUMIN|\b(2011|2014|2024|2124|2219|5052|5083|6061|6082|7050|7075|7175|7475)\b")),
        (Steel, Pattern(@"STEEL|\b(1018|1020|1045|4130|4140|4340|8620|300M|H13|D2|A2|O1)\b")),
        (Copper, Pattern(@"BRASS|BRONZE|COPPER|\bCU\b|BERYLLIUM")),
        (Plastic, Pattern(@"PEEK|DELRIN|ACETAL|NYLON|ULTEM|PTFE|TEFLON|PLASTIC|\bPOM\b|TORLON|VESPEL|POLY"))
    ];

    internal static string Classify(string? material)
    {
        if (string.IsNullOrWhiteSpace(material)) return Unknown;
        var text = material.ToUpperInvariant();
        foreach (var (group, pattern) in Patterns)
        {
            if (pattern.IsMatch(text)) return group;
        }
        return Unknown;
    }

    internal static int Rank(string group)
    {
        var index = Order.ToList().IndexOf(group);
        return index < 0 ? Order.Count : index;
    }

    private static Regex Pattern(string value) =>
        new(value, RegexOptions.CultureInvariant | RegexOptions.Compiled, PatternTimeout);
}

/// <summary>
/// Reads the factory's Cimatron tool names ("FIN_10_R1", "CADURI_6", "DRILL_2.5", "MERASEK_16"):
/// the identity key, the diameter written in the name, and the tool type the name prefix means when
/// the tool catalog does not know the tool.
/// </summary>
internal static class ToolNaming
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex Separators = new(@"[\s_]+", RegexOptions.CultureInvariant, PatternTimeout);
    private static readonly Regex Number = new(@"(?<![A-Z])(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant, PatternTimeout);
    private static readonly Regex CornerRadius = new(@"(^|_)R\d", RegexOptions.CultureInvariant, PatternTimeout);

    // Prefixes of the factory's names (Hebrew terms written in Latin letters) and English ones.
    private static readonly (string Prefix, string Type)[] Prefixes =
    [
        ("FLYCUTTER", "FACE_MILL"), ("FLAYCAT", "FACE_MILL"), ("FACE", "FACE_MILL"),
        ("CADURI", "BALL_END_MILL"), ("BALL", "BALL_END_MILL"),
        ("MECADED", "SPOT_DRILL"), ("SPOT", "SPOT_DRILL"), ("MERCUZ", "SPOT_DRILL"), ("MERKUZ", "SPOT_DRILL"),
        ("KERNER", "CENTER_DRILL"), ("CENTER", "CENTER_DRILL"),
        ("FAZA", "CHAMFER_MILL"), ("CHAMFER", "CHAMFER_MILL"),
        ("MAVR", "TAP"), ("TAP", "TAP"),
        ("REAMER", "REAMER"), ("MARHIV", "REAMER"),
        ("THREAD", "THREAD_MILL"), ("HAVRAGA", "THREAD_MILL"), ("AVRAGA", "THREAD_MILL"),
        ("SAKIN_AVRAGA", "THREAD_MILL"), ("KARS_AVR", "THREAD_MILL"),
        ("SLOT", "SLOT_MILL"), ("DISK", "SLOT_MILL"),
        ("ENGRAV", "ENGRAVER"), ("HARITA", "ENGRAVER"),
        ("PROBE", "PROBE"),
        ("DRILL", "DRILL"),
        ("MERASEK", "END_MILL"), ("RESEK", "END_MILL"), ("RES", "END_MILL"), ("FIN", "END_MILL"), ("END", "END_MILL")
    ];

    /// <summary>The name that identifies a tool: upper case, spaces and underscores alike.</summary>
    internal static string Key(string name) =>
        Separators.Replace(name.Trim().ToUpperInvariant(), "_").Trim('_');

    /// <summary>The diameter written in the name ("DRILL_2.5" is 2.5, "FIN_10_R1" is 10).</summary>
    internal static double? DiameterFromName(string name)
    {
        var match = Number.Match(Key(name));
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value > 0
            ? value
            : null;
    }

    /// <summary>The tool type a name prefix means; a flat mill with a corner radius ("_R1") is a bull-nose mill.</summary>
    internal static string TypeFromName(string name)
    {
        var key = Key(name);
        foreach (var (prefix, type) in Prefixes)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            return type == "END_MILL" && CornerRadius.IsMatch(key[prefix.Length..]) ? "BULL_NOSE_END_MILL" : type;
        }
        return "OTHER";
    }
}
