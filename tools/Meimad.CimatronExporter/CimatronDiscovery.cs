using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Meimad.CimatronExporter;

/// <summary>
/// What this PC has of Cimatron automation, for <c>--probe</c> when the expected ProgID is missing: every COM ProgID whose
/// name mentions Cimatron (64-bit and 32-bit registry views) with its server file and whether this process can load it,
/// the Cimatron installations, the interop assemblies beside them and the objects Cimatron put in the running object
/// table. Read-only: nothing is registered, started or changed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CimatronDiscovery
{
    private static readonly string[] NameHints = ["cimatron", "3dsystems", "3d systems"];

    internal static IEnumerable<string> Report()
    {
        yield return $"Exporter process: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}, Windows {Environment.OSVersion.Version}, user {Environment.UserName}, "
            + (CimatronRunningApplication.Elevated ? "RUN AS ADMINISTRATOR (cannot reach a Cimatron started normally)" : "not elevated");
        yield return "COM ProgIDs mentioning Cimatron:";
        var found = 0;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var line in ProgIds(view))
            {
                found += 1;
                yield return "  " + line;
            }
        }
        if (found == 0) yield return "  (none: Cimatron's automation is not registered for COM on this PC)";
        yield return "Cimatron installations:";
        var installs = 0;
        foreach (var line in Installations())
        {
            installs += 1;
            yield return "  " + line;
        }
        if (installs == 0) yield return "  (none found in the uninstall list)";
        yield return "Interop and API files:";
        var files = 0;
        foreach (var line in ApiFiles())
        {
            files += 1;
            yield return "  " + line;
        }
        if (files == 0) yield return "  (none found below Program Files)";
        yield return "Cimatron processes:";
        var processes = 0;
        foreach (var line in Processes())
        {
            processes += 1;
            yield return "  " + line;
        }
        if (processes == 0) yield return "  (none: Cimatron is not running for this user)";
        yield return "Running object table (all entries; Cimatron may register itself under a class id):";
        var running = 0;
        foreach (var line in RunningObjects())
        {
            running += 1;
            yield return "  " + line;
        }
        if (running == 0) yield return "  (empty)";
    }

    // "Cimatron", "3D Systems", or a name starting with "Cim" + a capital other than I (CimAppAccess, CimBaseAPI...;
    // not Windows' Japanese IME classes, "CIme...").
    private static bool Mentions(string? text) =>
        text is not null && (NameHints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase))
            || System.Text.RegularExpressions.Regex.IsMatch(text, @"(^|[.\/ ])Cim[A-HJ-Z]"));

    private static IEnumerable<string> ProgIds(RegistryView view)
    {
        var lines = new List<string>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            foreach (var name in root.GetSubKeyNames())
            {
                if (!Mentions(name) || name.StartsWith('.')) continue;
                using var key = root.OpenSubKey(name);
                var clsid = key?.OpenSubKey("CLSID")?.GetValue(null) as string;
                if (clsid is null) continue;
                var (kind, server) = Server(root, clsid);
                var loadable = kind switch
                {
                    "LocalServer32" => "out of process: any exporter can use it",
                    "InprocServer32" when view == RegistryView.Registry32 && Environment.Is64BitProcess => "32-bit DLL: needs a 32-bit exporter",
                    "InprocServer32" => "in-process DLL",
                    _ => "no server registered"
                };
                lines.Add($"[{(view == RegistryView.Registry64 ? "64" : "32")}] {name}  {clsid}  {kind} {server}  ({loadable})");
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            lines.Add($"[{view}] registry not readable: {exception.Message}");
        }
        return lines;
    }

    private static (string Kind, string? Path) Server(RegistryKey root, string clsid)
    {
        using var key = root.OpenSubKey($@"CLSID\{clsid}");
        foreach (var kind in new[] { "LocalServer32", "InprocServer32" })
        {
            using var server = key?.OpenSubKey(kind);
            if (server is not null) return (kind, server.GetValue(null) as string);
        }
        return ("none", null);
    }

    private static IEnumerable<string> Installations()
    {
        var lines = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    var display = entry?.GetValue("DisplayName") as string;
                    if (!Mentions(display)) continue;
                    lines.Add($"{display} {entry?.GetValue("DisplayVersion")}  {entry?.GetValue("InstallLocation")}");
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                lines.Add($"uninstall list not readable: {exception.Message}");
            }
        }
        return lines.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ApiFiles()
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var root in roots)
        {
            IEnumerable<string> folders;
            try
            {
                folders = Directory.EnumerateDirectories(root).Where(folder => Mentions(Path.GetFileName(folder))).ToArray();
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (var folder in folders)
            {
                try
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 5 };
                    foreach (var file in Directory.EnumerateFiles(folder, "*.*", options))
                    {
                        var name = Path.GetFileName(file);
                        if (!(name.Contains("interop", StringComparison.OrdinalIgnoreCase) || name.Contains("AppAccess", StringComparison.OrdinalIgnoreCase)
                              || name.EndsWith(".tlb", StringComparison.OrdinalIgnoreCase))) continue;
                        lines.Add(file);
                        if (lines.Count >= 60) return lines;
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    lines.Add($"{folder}: {exception.Message}");
                }
            }
        }
        return lines;
    }

    private static IEnumerable<string> Processes()
    {
        var lines = new List<string>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                if (!process.ProcessName.StartsWith("cim", StringComparison.OrdinalIgnoreCase)) continue;
                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Another user's or an elevated process.
                }
                lines.Add($"{process.ProcessName} pid {process.Id} session {process.SessionId} {path ?? "(path not readable: elevated or another user)"}");
            }
        }
        return lines;
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx context);

    private static IEnumerable<string> RunningObjects()
    {
        var lines = new List<string>();
        try
        {
            if (GetRunningObjectTable(0, out var table) != 0 || CreateBindCtx(0, out var context) != 0) return lines;
            table.EnumRunning(out var monikers);
            var batch = new IMoniker[1];
            while (monikers.Next(1, batch, IntPtr.Zero) == 0)
            {
                try
                {
                    batch[0].GetDisplayName(context, null, out var name);
                    lines.Add(name);
                    if (lines.Count >= 60) break;
                }
                catch (COMException)
                {
                    // A moniker without a display name.
                }
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            lines.Add($"running object table not readable: {exception.Message}");
        }
        return lines;
    }
}
