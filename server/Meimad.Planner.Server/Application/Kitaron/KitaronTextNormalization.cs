namespace Meimad.Planner.Server.Application.Kitaron;

// Kitaron sometimes wraps RTL-adjacent text (part numbers, order references, names, revisions) in
// invisible Unicode bidi/formatting marks so its own UI renders mixed Hebrew/Latin text correctly.
// Those marks are not part of the value's real identity, but a naive Trim() leaves them in place
// since they are not whitespace. Left unstripped, the exact same real-world part or order can sync
// as two different records depending on whether a given Kitaron row happens to carry the marks,
// because Case/Order identity, matching, and hashing all key off this text verbatim.
internal static class KitaronTextNormalization
{
    private static readonly char[] InvisibleMarks =
    [
        '​', // zero width space
        '‎', // left-to-right mark
        '‏', // right-to-left mark
        '؜', // Arabic letter mark
        '‪', '‫', '‬', '‭', '‮', // directional embedding/override/pop
        '⁦', '⁧', '⁨', '⁩', // directional isolates
        '﻿' // BOM / zero width no-break space
    ];

    internal static string? Clean(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.IndexOfAny(InvisibleMarks) < 0) return trimmed;
        var cleaned = new string(trimmed.Where(character => Array.IndexOf(InvisibleMarks, character) < 0).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    internal static string CleanRequired(string value) => Clean(value) ?? value.Trim();
}
