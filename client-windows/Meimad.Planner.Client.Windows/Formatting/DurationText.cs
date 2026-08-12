using System.Globalization;

namespace Meimad.Planner.Client.Windows.Formatting;

internal static class DurationText
{
    internal static string Format(long seconds)
    {
        if (seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        var remainingSeconds = seconds % 60;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:D2}:{minutes:D2}:{remainingSeconds:D2}");
    }

    internal static string FormatOptional(int? seconds) =>
        seconds.HasValue ? Format(seconds.Value) : string.Empty;

    internal static bool TryParseOptional(string? text, out int? seconds)
    {
        seconds = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length != 3
            || parts[0].Length < 2
            || parts[1].Length != 2
            || parts[2].Length != 2
            || !AllDigits(parts[0])
            || !AllDigits(parts[1])
            || !AllDigits(parts[2])
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var remainingSeconds)
            || minutes > 59
            || remainingSeconds > 59)
        {
            return false;
        }

        try
        {
            var total = checked(hours * 3600L + minutes * 60L + remainingSeconds);
            if (total > int.MaxValue)
            {
                return false;
            }

            seconds = (int)total;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool AllDigits(string value) =>
        value.All(character => character is >= '0' and <= '9');
}
