using System.Security.AccessControl;
using System.Security.Principal;

namespace Meimad.Planner.Server.Backup;

internal static class RecoveryPaths
{
    internal static string Resolve(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(relative) || parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".."
            || p.Contains(':') || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Unsafe recovery file path.");
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Recovery path escaped its root.");
        RejectLinks(path);
        return path;
    }

    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Recovery paths cannot traverse symbolic links or junctions.");
    }

    internal static void CreatePrivateDirectory(string path)
    {
        RejectLinks(path);
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static void DeleteOwnedDirectory(string parent, string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing cleanup outside recovery workspace.");
        RejectLinks(full);
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }

    internal static void ProtectFile(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local recovery credentials require Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
