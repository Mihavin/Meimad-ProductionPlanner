using System.Globalization;
using System.Text.RegularExpressions;

namespace Meimad.Planner.Server.Application.LocalAi;

/// <summary>
/// A file that may hold simulation data for an Operation's NC program. <c>Kind</c> is <c>stock</c>
/// (rest material / raw stock model), <c>fixture</c>, <c>tool-table</c> or <c>part-model</c>;
/// <c>Source</c> is <c>AI</c> when the local model classified it, else <c>RULE</c>.
/// </summary>
internal sealed record SimulationDataCandidate(
    string Path,
    string Name,
    string Kind,
    double Confidence,
    string Reason,
    string Source,
    long SizeBytes,
    DateTimeOffset ModifiedAt,
    bool AlreadyLinked);

/// <summary>What the search knows about the Operation and where it may look.</summary>
internal sealed record SimulationDataScope(
    string PartNumber,
    int OperationNumber,
    string OperationName,
    string? NcFilePath,
    IReadOnlyList<string> Folders,
    IReadOnlySet<string> LinkedPaths);

/// <summary>
/// The rule-based half of the simulation-data search (owner request 2026-10-10): which folders belong to
/// the part, which files in them can be simulation data, and a first classification from names and
/// folders. It only lists and reads file metadata; it never opens CAD content or changes files. The local
/// model then re-ranks these candidates (<see cref="SimulationDataSearchService"/>), and the planner links
/// what is right.
/// </summary>
internal static class SimulationDataDiscovery
{
    internal const int MaxFolders = 6;
    internal const int MaxCandidates = 300;
    internal const int MaxEntriesVisited = 20000;
    internal const int MaxDepth = 5;

    private static readonly string[] StockWords = ["stock", "rest", "remain", "blank", "raw", "billet", "חומר", "גלם"];
    private static readonly string[] FixtureWords = ["fixture", "fixt", "vise", "vice", "jig", "clamp", "chuck", "jaw", "tomb", "pallet", "plate", "מתקן", "מלחצ", "לסת", "קליבה"];
    private static readonly Regex OperationToken = new(@"(?:^|[^A-Z0-9])(?:OP|OPER|OPERATION)[\s_\-]?0*(\d{1,3})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsCandidateFile(string path)
    {
        var name = System.IO.Path.GetFileName(path).ToLowerInvariant();
        var extension = System.IO.Path.GetExtension(name);
        if (name.EndsWith(".bac", StringComparison.Ordinal) || name.StartsWith('~')) return false;
        return extension is ".stl" or ".stp" or ".step" or ".mht" or ".mhtml"
            || (extension is ".csv" or ".xlsx" or ".xls" && name.Contains("tool", StringComparison.Ordinal));
    }

    /// <summary>The part number without a dash suffix (30P410126102-001 → 30P410126102), for folder names.</summary>
    internal static string PartBase(string partNumber)
    {
        var value = partNumber.Trim().ToUpperInvariant();
        var dash = value.LastIndexOf('-');
        return dash > 3 ? value[..dash] : value;
    }

    /// <summary>
    /// The folder of the part that holds an NC file: the nearest ancestor (up to four levels) whose name
    /// contains the part number, else the parent of an NC/Gcode folder, else the NC file's own folder.
    /// </summary>
    internal static string? PartFolderOfNcFile(string? ncFilePath, string partNumber)
    {
        if (string.IsNullOrWhiteSpace(ncFilePath)) return null;
        var folder = System.IO.Path.GetDirectoryName(ncFilePath);
        if (string.IsNullOrEmpty(folder)) return null;
        var partBase = PartBase(partNumber);
        var current = new DirectoryInfo(folder);
        for (var level = 0; current is not null && level < 4; level++, current = current.Parent)
        {
            if (current.Name.ToUpperInvariant().Contains(partBase, StringComparison.Ordinal)) return current.FullName;
        }
        var own = new DirectoryInfo(folder);
        return own.Name.ToUpperInvariant() is "NC" or "GCODE" or "CNC" && own.Parent is not null ? own.Parent.FullName : own.FullName;
    }

    /// <summary>
    /// The part folders an approved document path points at: the path up to and including the first
    /// segment that contains the part number (relative paths from the document index).
    /// </summary>
    internal static string? PartFolderOfDocument(string rootPath, string relativePath, string partNumber)
    {
        var partBase = PartBase(partNumber);
        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (segments[index].ToUpperInvariant().Contains(partBase, StringComparison.Ordinal))
                return System.IO.Path.Combine([rootPath, .. segments[..(index + 1)]]);
        }
        return null;
    }

    /// <summary>True when <paramref name="path"/> is one of the roots or lies below one.</summary>
    internal static bool IsUnder(string path, IEnumerable<string> roots)
    {
        var full = Trim(path);
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(Trim))
        {
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
            if (full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full[root.Length] == '\\') return true;
        }
        return false;
    }

    private static string Trim(string path) => path.Replace('/', '\\').TrimEnd('\\');

    /// <summary>Lists candidate files below the folders, bounded in depth, entries and results.</summary>
    internal static IReadOnlyList<FileInfo> Enumerate(IEnumerable<string> folders, CancellationToken token)
    {
        var found = new List<FileInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = 0;
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFolders))
        {
            var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
            try
            {
                var start = new DirectoryInfo(folder);
                if (!start.Exists) continue;
                pending.Push((start, 0));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            while (pending.Count > 0 && visited < MaxEntriesVisited && found.Count < MaxCandidates)
            {
                token.ThrowIfCancellationRequested();
                var (directory, depth) = pending.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = directory.EnumerateFileSystemInfos().ToArray(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                foreach (var entry in entries)
                {
                    if (++visited > MaxEntriesVisited || found.Count >= MaxCandidates) break;
                    if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                    if (entry is DirectoryInfo child)
                    {
                        // Planner-generated cache is never simulation input.
                        if (depth + 1 <= MaxDepth && !child.Name.Equals("_MeimadPlanner", StringComparison.OrdinalIgnoreCase))
                            pending.Push((child, depth + 1));
                    }
                    else if (entry is FileInfo file && IsCandidateFile(file.FullName) && seen.Add(file.FullName))
                    {
                        found.Add(file);
                    }
                }
            }
        }
        return found;
    }

    /// <summary>The first classification of a file from its name, folder and the Operation.</summary>
    internal static SimulationDataCandidate Classify(FileInfo file, SimulationDataScope scope)
    {
        var name = file.Name;
        var lower = name.ToLowerInvariant();
        var extension = file.Extension.ToLowerInvariant();
        var folder = file.Directory?.Name.ToLowerInvariant() ?? string.Empty;
        var reasons = new List<string>();
        string kind;
        double score;
        if (extension is ".mht" or ".mhtml" or ".csv" or ".xlsx" or ".xls")
        {
            kind = "tool-table";
            score = extension is ".mht" or ".mhtml" ? 0.55 : 0.4;
            reasons.Add(extension is ".mht" or ".mhtml" ? "Cimatron tool list (MHT)" : "tool list by name");
            if (lower.Contains("tp_model", StringComparison.Ordinal) || lower.Contains("tools", StringComparison.Ordinal)) { score += 0.15; reasons.Add("named like a Cimatron TOOLS report"); }
        }
        else if (StockWords.Any(word => lower.Contains(word, StringComparison.Ordinal)) || folder == "stock")
        {
            kind = "stock";
            score = extension == ".stl" ? 0.55 : 0.4;
            reasons.Add(folder == "stock" ? "in a STOCK folder" : "stock / rest-material name");
            if (folder == "stock" && StockWords.Any(word => lower.Contains(word, StringComparison.Ordinal))) score += 0.1;
        }
        else if (FixtureWords.Any(word => lower.Contains(word, StringComparison.Ordinal))
                 || folder.Contains("fixture", StringComparison.Ordinal) || folder.Contains("מתקן", StringComparison.Ordinal))
        {
            kind = "fixture";
            score = 0.5;
            reasons.Add("fixture / workholding name");
        }
        else if (extension is ".stp" or ".step" && name.ToUpperInvariant().Contains(PartBase(scope.PartNumber), StringComparison.Ordinal))
        {
            kind = "part-model";
            score = 0.45;
            reasons.Add("STEP model named after the part");
        }
        else
        {
            kind = extension == ".stl" ? "stock" : "fixture";
            score = 0.15;
            reasons.Add("3D model without a telling name");
        }

        // An OP number in the name or folder: the Operation's own number (10 or 1 for OP10) counts for it.
        var tokens = OperationToken.Matches(name + " " + (file.Directory?.Name ?? string.Empty))
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();
        if (tokens.Count > 0)
        {
            var own = new HashSet<int> { scope.OperationNumber };
            if (scope.OperationNumber % 10 == 0) own.Add(scope.OperationNumber / 10);
            if (tokens.Overlaps(own)) { score += 0.2; reasons.Add($"OP number matches Operation {scope.OperationNumber}"); }
            else { score -= 0.2; reasons.Add("OP number of another Operation"); }
        }
        if (scope.NcFilePath is { } nc && System.IO.Path.GetDirectoryName(nc) is { } ncFolder
            && file.DirectoryName is { } directory
            && (directory.Equals(ncFolder, StringComparison.OrdinalIgnoreCase) || IsUnder(directory, [ncFolder])))
        {
            score += 0.1;
            reasons.Add("next to the NC program");
        }
        return new SimulationDataCandidate(
            file.FullName,
            name,
            kind,
            Math.Round(Math.Clamp(score, 0.05, 0.95), 2),
            string.Join("; ", reasons),
            "RULE",
            SafeLength(file),
            SafeModified(file),
            scope.LinkedPaths.Contains(file.FullName));
    }

    /// <summary>Kinds in the order a planner reviews them, then most likely first.</summary>
    internal static IReadOnlyList<SimulationDataCandidate> Order(IEnumerable<SimulationDataCandidate> candidates) =>
        candidates
            .OrderBy(candidate => candidate.Kind switch { "stock" => 0, "fixture" => 1, "tool-table" => 2, _ => 3 })
            .ThenByDescending(candidate => candidate.Confidence)
            .ThenByDescending(candidate => candidate.ModifiedAt)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; }
        catch (IOException) { return 0; }
    }

    private static DateTimeOffset SafeModified(FileInfo file)
    {
        try { return new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero); }
        catch (IOException) { return DateTimeOffset.UnixEpoch; }
    }
}
