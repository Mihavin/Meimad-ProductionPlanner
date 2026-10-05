using System.Text.RegularExpressions;

namespace Meimad.Planner.NcEngine;

/// <summary>
/// The external subprograms an NC program calls and the program number an NC file declares, read
/// the way the NC engine resolves them (meimad-macro.js and the engine's src/subprograms.js):
/// <c>M98 P…</c> and <c>G65 P…</c> call a program by number (a FANUC <c>M98 P</c> with eight digits
/// is a four-digit repeat count followed by the program number); <c>M97 P…</c> calls a local N block
/// and needs no file. A file's number is the O (or ":") number on its first code line, else a
/// leading number in its file name ("O09810_probe.nc", "1001.nc").
/// </summary>
public static partial class NcSubprogramCalls
{
    /// <summary>
    /// Extensions the engine tries, in order, for a called number in the program's folder: the
    /// vendored resolver's, then Mazak's ".eia" (meimad-subprograms.js).
    /// </summary>
    public static readonly IReadOnlyList<string> FolderExtensions = [".nc", ".tap", ".cnc", ".txt", "", ".eia"];

    /// <summary>Extensions of files that may hold a subprogram.</summary>
    public static readonly IReadOnlySet<string> FileExtensions = new HashSet<string>(
        ["", ".nc", ".tap", ".cnc", ".txt", ".gcode", ".iso", ".mpf", ".spf", ".min", ".ngc", ".eia"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>The program numbers the lines call with M98 or G65, each once, in order of first call.</summary>
    public static IReadOnlyList<int> CalledPrograms(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var programs = new List<int>();
        foreach (var line in lines)
        {
            var code = Code(line);
            var m98 = M98().IsMatch(code);
            if (!m98 && !G65().IsMatch(code)) continue;
            var p = ProgramWord().Match(code);
            if (!p.Success) continue;
            var digits = p.Groups[1].Value;
            // FANUC M98 Pkkkknnnn: repeat count then program number.
            var text = m98 && digits.Length == 8 ? digits[4..] : digits;
            if (int.TryParse(text, out var program) && program > 0 && !programs.Contains(program))
                programs.Add(program);
        }
        return programs;
    }

    /// <summary>
    /// The number an NC file declares: its first code line's O or ":" number (after "%" and
    /// comment-only lines), else a leading number in the file name; null when neither has one.
    /// </summary>
    public static int? ProgramNumber(string fileName, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines)
        {
            var code = Code(line).Trim();
            if (code.Length == 0 || code == "%") continue;
            var header = HeaderNumber().Match(code);
            if (header.Success && int.TryParse(header.Groups[1].Value, out var number) && number > 0) return number;
            break;
        }
        var name = FileNameNumber().Match(Path.GetFileName(fileName ?? string.Empty));
        return name.Success && int.TryParse(name.Groups[1].Value, out var fromName) && fromName > 0 ? fromName : null;
    }

    /// <summary>
    /// The numbers called by the main program, or by an included file it reaches, that no included
    /// file provides. A file without a number provides nothing.
    /// </summary>
    public static IReadOnlyList<int> MissingPrograms(
        IEnumerable<string> mainLines,
        IReadOnlyList<(int? Number, IReadOnlyList<string> Lines)> included)
    {
        ArgumentNullException.ThrowIfNull(included);
        var byNumber = included
            .Where(file => file.Number is not null)
            .GroupBy(file => file.Number!.Value)
            .ToDictionary(group => group.Key, group => group.First().Lines);
        var missing = new List<int>();
        var visited = new HashSet<int>();
        var pending = new Queue<int>(CalledPrograms(mainLines));
        while (pending.Count > 0)
        {
            var program = pending.Dequeue();
            if (!visited.Add(program)) continue;
            if (!byNumber.TryGetValue(program, out var lines))
            {
                missing.Add(program);
                continue;
            }
            foreach (var nested in CalledPrograms(lines)) pending.Enqueue(nested);
        }
        return missing;
    }

    /// <summary>
    /// The file in <paramref name="folder"/> that holds program <paramref name="program"/>: first
    /// the names the engine tries (O01001.nc, O1001.nc, 1001.nc, …), then any NC file whose own
    /// number is it. Null when there is none or the folder cannot be read.
    /// </summary>
    public static string? FindInFolder(string folder, int program, string? excludePath = null)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }

        bool Excluded(string path) => excludePath is not null
            && string.Equals(Path.GetFullPath(path), Path.GetFullPath(excludePath), StringComparison.OrdinalIgnoreCase);
        var byName = files
            .Where(path => !Excluded(path))
            .GroupBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var number = program.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string[] stems = [$"O{number.PadLeft(5, '0')}", $"O{number.PadLeft(4, '0')}", $"O{number}", number.PadLeft(5, '0'), number];
        foreach (var stem in stems)
        {
            foreach (var extension in FolderExtensions)
            {
                if (byName.TryGetValue(stem + extension, out var path)) return path;
            }
        }

        foreach (var path in files.Where(path => !Excluded(path)).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!FileExtensions.Contains(Path.GetExtension(path))) continue;
            try
            {
                if (ProgramNumber(path, File.ReadLines(path).Take(20)) == program) return path;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    private static string Code(string? line) =>
        Comment().Replace(line ?? string.Empty, " ").Split(';')[0].ToUpperInvariant();

    [GeneratedRegex(@"\([^)]*\)?")]
    private static partial Regex Comment();

    [GeneratedRegex(@"(?<![A-Z#])M0*98(?![\d.])")]
    private static partial Regex M98();

    [GeneratedRegex(@"(?<![A-Z#])G0*65(?![\d.])")]
    private static partial Regex G65();

    [GeneratedRegex(@"(?<![A-Z#])P\s*(\d+)")]
    private static partial Regex ProgramWord();

    [GeneratedRegex(@"^[O:]\s*(\d+)")]
    private static partial Regex HeaderNumber();

    [GeneratedRegex(@"^O?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameNumber();
}
