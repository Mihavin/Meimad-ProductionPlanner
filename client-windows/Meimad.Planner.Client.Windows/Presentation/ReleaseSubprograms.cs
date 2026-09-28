using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Meimad.Planner.NcEngine;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// A subprogram file offered with an NC release: found next to the program because the program
/// calls it (M98 / G65), or added by the programmer. Only ticked rows are released.
/// </summary>
internal sealed class ReleaseSubprogramRow : INotifyPropertyChanged
{
    private bool isIncluded;

    internal ReleaseSubprogramRow(string path, int? programNumber, bool detected)
    {
        Path = path;
        ProgramNumber = programNumber;
        Detected = detected;
        isIncluded = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public int? ProgramNumber { get; }

    /// <summary>True when the program calls it and it was found next to the program.</summary>
    public bool Detected { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public string Label => ProgramNumber is int number
        ? $"{FileName} (O{number})"
        : $"{FileName} (no program number)";

    public bool IsIncluded
    {
        get => isIncluded;
        set
        {
            if (isIncluded == value) return;
            isIncluded = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Finds the subprogram files an NC program calls in its own folder, the way the NC engine and the
/// Server read calls (<see cref="NcSubprogramCalls"/>): each called number, then the numbers those
/// files call. Numbers not found are reported; the release still goes ahead (owner decision
/// 2026-09-28), so a macro kept on the machine does not have to be released with every program.
/// </summary>
internal static class ReleaseSubprogramDetection
{
    internal static (IReadOnlyList<ReleaseSubprogramRow> Found, IReadOnlyList<int> Missing) Detect(string mainPath)
    {
        var folder = System.IO.Path.GetDirectoryName(mainPath);
        var lines = ReadLines(mainPath);
        return folder is null || lines is null ? ([], []) : Detect(lines, folder, mainPath);
    }

    /// <summary>The same for program text (possibly unsaved) and the folder to look in.</summary>
    internal static (IReadOnlyList<ReleaseSubprogramRow> Found, IReadOnlyList<int> Missing) Detect(
        IReadOnlyList<string> mainLines, string folder, string? mainPath)
    {
        var found = new List<ReleaseSubprogramRow>();
        var missing = new List<int>();
        var visited = new HashSet<int>();
        var pending = new Queue<int>(NcSubprogramCalls.CalledPrograms(mainLines));
        while (pending.Count > 0)
        {
            var number = pending.Dequeue();
            if (!visited.Add(number)) continue;
            var path = NcSubprogramCalls.FindInFolder(folder, number, excludePath: mainPath);
            var called = path is null ? null : ReadLines(path);
            if (path is null || called is null)
            {
                missing.Add(number);
                continue;
            }
            if (found.Any(row => string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            found.Add(new ReleaseSubprogramRow(path, number, detected: true));
            foreach (var nested in NcSubprogramCalls.CalledPrograms(called)) pending.Enqueue(nested);
        }
        return (found, missing);
    }

    /// <summary>The numbers the program (or an included file) calls that no included file provides.</summary>
    internal static IReadOnlyList<int> Missing(string mainPath, IEnumerable<string> includedPaths)
    {
        var lines = ReadLines(mainPath);
        return lines is null ? [] : Missing(lines, includedPaths);
    }

    internal static IReadOnlyList<int> Missing(IReadOnlyList<string> mainLines, IEnumerable<string> includedPaths)
    {
        var included = includedPaths
            .Select(path => (Path: path, Lines: ReadLines(path)))
            .Where(file => file.Lines is not null)
            .Select(file => (NcSubprogramCalls.ProgramNumber(file.Path, file.Lines!.Take(20)), (IReadOnlyList<string>)file.Lines!))
            .ToArray();
        return NcSubprogramCalls.MissingPrograms(mainLines, included);
    }

    internal static int? ProgramNumber(string path)
    {
        var lines = ReadLines(path);
        return lines is null ? null : NcSubprogramCalls.ProgramNumber(path, lines.Take(20));
    }

    internal static string MissingNote(IReadOnlyList<int> missing)
    {
        if (missing.Count == 0) return string.Empty;
        var programs = string.Join(", ", missing.Select(number => $"O{number}"));
        return $"Called but not included: {programs}. The release goes ahead; these programs must already be on the machine.";
    }

    private static string[]? ReadLines(string path)
    {
        try
        {
            return NcTextFile.Decode(File.ReadAllBytes(path)).Text.Split('\n');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
