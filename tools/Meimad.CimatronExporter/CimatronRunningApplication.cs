using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Meimad.CimatronExporter;

/// <summary>
/// The running Cimatron's application object without <c>AppAccess.GetApplication</c>. At Meimad (Cimatron 2026.0,
/// 2026-10-12) GetApplication answered "Class not registered": Cimatron's own COM classes are not registered, so AppAccess
/// cannot reach Cimatron by class. A running Cimatron may still have put its application object in the running object
/// table; it is taken from there (by the Application class id, else any table entry that has the IApplication interface).
/// Windows keeps a separate table per logon session and elevation level: an exporter run as administrator does not see
/// a Cimatron started normally.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CimatronRunningApplication
{
    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid classId, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? unknown);

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx context);

    internal static bool Elevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal static bool CimatronRunning() =>
        System.Diagnostics.Process.GetProcesses().Any(process =>
        {
            using (process) return process.ProcessName.StartsWith("cimatron", StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>The application object of a running Cimatron, or null; <paramref name="trace"/> says what was tried.</summary>
    internal static object? Find(string? programFolder, ICollection<string> trace)
    {
        if (programFolder is null)
        {
            trace.Add("running object table: no Cimatron Program folder, class ids unknown");
            return null;
        }

        var applicationInterface = CimatronTypeLibrary.InterfaceId(Path.Combine(programFolder, "CimAppAccess.tlb"), "IApplication")
            ?? CimatronTypeLibrary.InterfaceId(Path.Combine(programFolder, "CimatronE.tlb"), "IApplication");
        var classIds = new[]
        {
            CimatronTypeLibrary.ClassId(Path.Combine(programFolder, "CimAppAccess.tlb"), "Application"),
            CimatronTypeLibrary.ClassId(Path.Combine(programFolder, "CimatronE.tlb"), "CimApplication")
        }.OfType<Guid>().Distinct();
        foreach (var classId in classIds)
        {
            var id = classId;
            var result = GetActiveObject(ref id, IntPtr.Zero, out var active);
            trace.Add($"running object table, class {classId:B}: {(result == 0 ? "found" : $"HRESULT 0x{result:X8}")}");
            if (result == 0 && active is not null && (applicationInterface is null || Implements(active, applicationInterface.Value))) return active;
        }

        if (applicationInterface is null)
        {
            trace.Add("running object table: IApplication not found in the type libraries");
            return null;
        }

        try
        {
            if (GetRunningObjectTable(0, out var table) != 0 || CreateBindCtx(0, out var context) != 0) return null;
            table.EnumRunning(out var monikers);
            var batch = new IMoniker[1];
            var entries = 0;
            while (monikers.Next(1, batch, IntPtr.Zero) == 0)
            {
                entries += 1;
                string name;
                try
                {
                    batch[0].GetDisplayName(context, null, out name);
                }
                catch (COMException)
                {
                    name = "(no name)";
                }

                if (table.GetObject(batch[0], out var entry) != 0 || entry is null) continue;
                if (Implements(entry, applicationInterface.Value))
                {
                    trace.Add($"running object table: {name} has IApplication");
                    return entry;
                }

                if (Marshal.IsComObject(entry)) Marshal.ReleaseComObject(entry);
            }

            trace.Add($"running object table: none of {entries} entries has IApplication {applicationInterface.Value:B}");
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            trace.Add($"running object table not readable: {exception.Message}");
        }

        return null;
    }

    /// <summary>What to do when no application object could be reached.</summary>
    internal static string Advice()
    {
        if (Elevated)
            return "The exporter runs as administrator and cannot reach a Cimatron started normally: run it from a normal PowerShell (not \"Run as administrator\").";
        if (!CimatronRunning())
            return "Cimatron is not running for this user: open Cimatron with the NC document and run the exporter again.";
        return "Cimatron is running but did not offer its application object, and its COM classes are not registered: repair the Cimatron installation as an administrator (or ask 3D Systems support how to register the Cimatron API).";
    }

    private static bool Implements(object target, Guid interfaceId)
    {
        var unknown = Marshal.GetIUnknownForObject(target);
        try
        {
            if (Marshal.QueryInterface(unknown, in interfaceId, out var pointer) != 0) return false;
            Marshal.Release(pointer);
            return true;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
