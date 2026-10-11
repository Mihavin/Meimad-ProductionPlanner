using System.Text;
using System.Text.Json;
using Microsoft.ClearScript;

namespace Meimad.Planner.NcEngine;

/// <summary>
/// The only file-system surface the engine scripts see (<c>__meimadHost</c> in bootstrap.js).
/// It is read-only and answers only for paths below an allow-listed root: the engine folder,
/// plus, on the client, the NC program's folder and the configured program-memory folders.
/// Every other path looks absent, so NC text can never make the engine read arbitrary files.
/// </summary>
[DefaultScriptUsage(ScriptAccess.None)]
public sealed class NcEngineFileHost
{
    internal const long MaximumTextBytes = 32L * 1024 * 1024;
    private const int MaximumDirectoryEntries = 10_000;
    private readonly object gate = new();
    private readonly string engineRoot;
    private IReadOnlyList<string> roots;
    private readonly string runtimeMachineFolder;
    private IReadOnlyDictionary<string, string> runtimeMachines = new Dictionary<string, string>();

    internal NcEngineFileHost(string engineRoot)
    {
        this.engineRoot = Root(engineRoot);
        roots = [this.engineRoot];
        runtimeMachineFolder = Path.Combine(this.engineRoot, "meimad", RuntimeMachineFolderName);
    }

    /// <summary>The in-memory folder (below meimad/) the adapter loads Machine Designer definitions from.</summary>
    internal const string RuntimeMachineFolderName = "runtime-machines";

    /// <summary>Replaces the in-memory machine definition files (file name to JSON).</summary>
    internal void SetRuntimeMachines(IReadOnlyDictionary<string, string> files)
    {
        var next = files.ToDictionary(
            entry => Path.Combine(runtimeMachineFolder, entry.Key),
            entry => entry.Value,
            StringComparer.OrdinalIgnoreCase);
        lock (gate) runtimeMachines = next;
    }

    // The in-memory folder answers before the disk: null when the path is not in it.
    private bool? RuntimeExists(string full, out string? text, out bool directory)
    {
        text = null;
        directory = false;
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(trimmed, runtimeMachineFolder, StringComparison.OrdinalIgnoreCase))
        {
            directory = true;
            return true;
        }
        if (!trimmed.StartsWith(runtimeMachineFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        IReadOnlyDictionary<string, string> current;
        lock (gate) current = runtimeMachines;
        return current.TryGetValue(trimmed, out text);
    }

    /// <summary>Replaces the non-engine roots (program folder, program memory folders).</summary>
    internal void SetAdditionalRoots(IEnumerable<string?> folders)
    {
        var next = new List<string> { engineRoot };
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                var root = Root(folder);
                if (!next.Contains(root, StringComparer.OrdinalIgnoreCase)) next.Add(root);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
            {
                // An invalid configured folder is simply not readable.
            }
        }
        lock (gate) roots = next;
    }

    [ScriptMember("currentDirectory")]
    public string CurrentDirectory() => engineRoot.TrimEnd(Path.DirectorySeparatorChar);

    [ScriptMember("exists")]
    public bool Exists(string path)
    {
        var full = Allowed(path);
        if (full is not null && RuntimeExists(full, out _, out _) is { } known) return known;
        return full is not null && (File.Exists(full) || Directory.Exists(full));
    }

    [ScriptMember("stat")]
    public string Stat(string path)
    {
        var full = Allowed(path);
        if (full is null) return string.Empty;
        if (RuntimeExists(full, out var runtimeText, out var runtimeDirectory) is { } runtime)
        {
            return runtime
                ? JsonSerializer.Serialize(new
                {
                    file = !runtimeDirectory,
                    directory = runtimeDirectory,
                    size = runtimeText is null ? 0 : Encoding.UTF8.GetByteCount(runtimeText),
                    mtimeMs = 0
                })
                : string.Empty;
        }
        try
        {
            if (File.Exists(full))
            {
                var info = new FileInfo(full);
                return JsonSerializer.Serialize(new
                {
                    file = true,
                    directory = false,
                    size = info.Length,
                    mtimeMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()
                });
            }
            if (Directory.Exists(full))
            {
                var info = new DirectoryInfo(full);
                return JsonSerializer.Serialize(new
                {
                    file = false,
                    directory = true,
                    size = 0,
                    mtimeMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()
                });
            }
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
        }
        return string.Empty;
    }

    [ScriptMember("readText")]
    public string ReadText(string path)
    {
        var full = Allowed(path);
        if (full is not null && RuntimeExists(full, out var runtimeText, out _) is not null) return runtimeText ?? string.Empty;
        if (full is null || !File.Exists(full)) return string.Empty;
        try
        {
            if (new FileInfo(full).Length > MaximumTextBytes) return string.Empty;
            return NcTextFile.Decode(File.ReadAllBytes(full)).Text;
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return string.Empty;
        }
    }

    [ScriptMember("readPrefix")]
    public string ReadPrefix(string path, int maximumBytes)
    {
        var full = Allowed(path);
        if (full is null || !File.Exists(full) || maximumBytes <= 0) return string.Empty;
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(maximumBytes, 1024 * 1024)];
            var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Encoding.UTF8.GetString(buffer, 0, length);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return string.Empty;
        }
    }

    [ScriptMember("listDirectory")]
    public string ListDirectory(string path)
    {
        var full = Allowed(path);
        if (full is not null && RuntimeExists(full, out _, out var runtimeDirectory) is not null)
        {
            if (!runtimeDirectory) return string.Empty;
            IReadOnlyDictionary<string, string> current;
            lock (gate) current = runtimeMachines;
            return JsonSerializer.Serialize(current.Keys
                .Select(key => new { name = Path.GetFileName(key), directory = false })
                .OrderBy(entry => entry.name, StringComparer.Ordinal)
                .ToArray());
        }
        if (full is null || !Directory.Exists(full)) return string.Empty;
        try
        {
            var entries = new DirectoryInfo(full).EnumerateFileSystemInfos()
                .Take(MaximumDirectoryEntries)
                .Select(entry => new
                {
                    name = entry.Name,
                    directory = (entry.Attributes & FileAttributes.Directory) != 0
                })
                .ToArray();
            return JsonSerializer.Serialize(entries);
        }
        catch (Exception exception) when (IsFileSystemFailure(exception))
        {
            return string.Empty;
        }
    }

    private string? Allowed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
        IReadOnlyList<string> current;
        lock (gate) current = roots;
        var withSeparator = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        return current.Any(root => withSeparator.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            ? full
            : null;
    }

    private static string Root(string folder)
    {
        var full = Path.GetFullPath(folder);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or NotSupportedException;
}
