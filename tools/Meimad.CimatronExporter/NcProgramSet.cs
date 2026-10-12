using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Meimad.CimatronExporter;

/// <summary>One NC file a post wrote: its program number, the programs it calls, and a short description.</summary>
internal sealed record NcProgramFile(string Path, string? Number, IReadOnlySet<string> Calls, IReadOnlyList<int> Tools, long Bytes);

/// <summary>
/// Several NC files from one post (Meimad 2026-10-12: <c>5999.EIA</c> and <c>6000.EIA</c> for one toolpath). When exactly one
/// file is called by none of the others and reaches every other through subprogram calls (<c>M98 P…</c>, <c>M98 &lt;…&gt;</c>,
/// <c>G65 P…</c>, <c>M198 P…</c>), it is the main program and the rest are its subprograms; otherwise the choice stays with
/// the user (<c>--nc-file</c>). The NC text is only read.
/// </summary>
internal static partial class NcProgramSet
{
    public static NcProgramFile Read(string path)
    {
        var text = File.ReadAllText(path, Encoding.Latin1);
        string? number = null;
        var calls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tools = new SortedSet<int>();
        foreach (var raw in text.Split('\n'))
        {
            var line = Comment().Replace(raw, "").Trim().ToUpperInvariant();
            if (line.Length == 0) continue;
            if (number is null && ProgramHeader().Match(line) is { Success: true } header)
                number = Normalize(header.Groups[1].Success ? header.Groups[1].Value : header.Groups[2].Value);
            foreach (Match call in Call().Matches(line)) calls.Add(Normalize(call.Groups[1].Success ? call.Groups[1].Value : call.Groups[2].Value));
            foreach (Match tool in Tool().Matches(line))
                if (int.TryParse(tool.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var t)) tools.Add(t);
        }

        return new NcProgramFile(path, number, calls, tools.ToArray(), new FileInfo(path).Length);
    }

    /// <summary>The main program when the files form one call tree, else null.</summary>
    public static NcProgramFile? MainProgram(IReadOnlyList<NcProgramFile> files)
    {
        if (files.Count < 2) return files.Count == 1 ? files[0] : null;
        bool Calls(NcProgramFile caller, NcProgramFile callee) =>
            Names(callee).Any(name => caller.Calls.Contains(name)) && !ReferenceEquals(caller, callee);
        var roots = files.Where(file => !files.Any(other => Calls(other, file))).ToArray();
        if (roots.Length != 1) return null;
        var reached = new HashSet<NcProgramFile> { roots[0] };
        var queue = new Queue<NcProgramFile>([roots[0]]);
        while (queue.TryDequeue(out var current))
            foreach (var next in files.Where(file => Calls(current, file) && reached.Add(file)))
                queue.Enqueue(next);
        return reached.Count == files.Count ? roots[0] : null;
    }

    public static string Describe(NcProgramFile file, string root) =>
        $"{System.IO.Path.GetRelativePath(root, file.Path).Replace('\\', '/')} ({file.Bytes} bytes, program {file.Number ?? "?"}, "
        + $"{(file.Tools.Count == 0 ? "no tools" : "T" + string.Join(",T", file.Tools))}"
        + $"{(file.Calls.Count == 0 ? "" : ", calls " + string.Join(", ", file.Calls.Order(StringComparer.Ordinal)))})";

    // A program answers to its O-number and to its file name ("5999.EIA" is program 5999).
    private static IEnumerable<string> Names(NcProgramFile file)
    {
        if (file.Number is not null) yield return file.Number;
        yield return Normalize(System.IO.Path.GetFileNameWithoutExtension(file.Path));
    }

    private static string Normalize(string name)
    {
        var trimmed = name.Trim().TrimStart('O', 'o', ':');
        return trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit) ? trimmed.TrimStart('0') is { Length: > 0 } digits ? digits : "0" : trimmed.ToUpperInvariant();
    }

    [GeneratedRegex(@"\([^)]*\)?", RegexOptions.CultureInvariant)]
    private static partial Regex Comment();

    [GeneratedRegex(@"^(?:O|:)(?:(\d{1,8})|<([^>]+)>)", RegexOptions.CultureInvariant)]
    private static partial Regex ProgramHeader();

    [GeneratedRegex(@"(?:M98|M198|G65)\s*(?:P\s*(\d{1,8})|P?\s*<([^>]+)>)", RegexOptions.CultureInvariant)]
    private static partial Regex Call();

    [GeneratedRegex(@"(?<![A-Z])T(\d{1,4})", RegexOptions.CultureInvariant)]
    private static partial Regex Tool();
}
