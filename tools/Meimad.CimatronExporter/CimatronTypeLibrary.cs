using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Meimad.CimatronExporter;

/// <summary>
/// Cimatron automation without a COM registration. On some installations (Cimatron 2026.0 SP3P1 at Meimad, probe of
/// 2026-10-12) the API files are in the Program folder (<c>CimAppAccess.dll</c>, <c>CimAppAccess.tlb</c>,
/// <c>CimatronE.tlb</c>, …) but <c>CimAppAccess.AppAccess</c> is not registered. The AppAccess object is then made directly
/// from the DLL: its class id comes from the type library, the DLL is loaded from the Program folder (its dependencies
/// beside it) and its class factory creates the object. Nothing is written to the registry.
/// It also lists the type libraries' classes, interfaces and members for <c>--probe</c>, so the late-bound member names
/// can be matched to the installed version.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CimatronTypeLibrary
{
    internal const string AppAccessDll = "CimAppAccess.dll";
    internal const string AppAccessTypeLibrary = "CimAppAccess.tlb";
    internal const string AppAccessClass = "AppAccess";

    /// <summary>The type libraries whose members the probe lists (those an NC export needs).</summary>
    internal static readonly string[] ProbeLibraries = ["CimAppAccess.tlb", "CimatronE.tlb", "CimNcAPI.tlb", "CimBaseAPI.tlb", "CimServicesAPI.tlb", "CimMdlrAPI.tlb"];

    private const int RegKindNone = 2;
    private const uint LoadWithAlteredSearchPath = 0x00000008;
    private static readonly Guid IidClassFactory = new("00000001-0000-0000-C000-000000000046");
    private static readonly Guid IidDispatch = new("00020400-0000-0000-C000-000000000046");

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int LoadTypeLibEx(string file, int regKind, out ITypeLib library);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetClassObject(ref Guid clsid, ref Guid iid, out IntPtr factory);

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
    }

    /// <summary>
    /// The Cimatron Program folder: <paramref name="explicitFolder"/>, else the newest installed Cimatron (uninstall list),
    /// else the newest <c>Program Files\Cimatron\Cimatron\&lt;version&gt;\Program</c> holding <see cref="AppAccessDll"/>.
    /// </summary>
    internal static string? FindProgramFolder(string? explicitFolder)
    {
        static string? Program(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var trimmed = folder.Trim().Trim('"').TrimEnd('\\', '/');
            foreach (var candidate in new[] { trimmed, Path.Combine(trimmed, "Program") })
                if (File.Exists(Path.Combine(candidate, AppAccessDll))) return candidate;
            return null;
        }

        if (!string.IsNullOrWhiteSpace(explicitFolder)) return Program(explicitFolder);
        var candidates = new List<(Version Version, string Folder)>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var name in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var entry = uninstall!.OpenSubKey(name);
                    var display = entry?.GetValue("DisplayName") as string;
                    if (display is null || !display.StartsWith("Cimatron", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Program(entry?.GetValue("InstallLocation") as string) is { } folder)
                        candidates.Add((VersionOf(folder), folder));
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // Fall back to the folder scan.
            }
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var cimatron = Path.Combine(programFiles, "Cimatron", "Cimatron");
        if (Directory.Exists(cimatron))
        {
            foreach (var version in Directory.EnumerateDirectories(cimatron))
                if (Program(version) is { } folder) candidates.Add((VersionOf(folder), folder));
        }
        return candidates.OrderByDescending(candidate => candidate.Version).Select(candidate => candidate.Folder).FirstOrDefault();
    }

    private static Version VersionOf(string folder)
    {
        foreach (var part in folder.Split('\\', '/').Reverse())
            if (Version.TryParse(part, out var version)) return version;
        return new Version(0, 0);
    }

    /// <summary>Creates the AppAccess object from the DLL in <paramref name="programFolder"/> (no registration).</summary>
    internal static object CreateAppAccess(string programFolder) =>
        CreateFromDll(Path.Combine(programFolder, AppAccessDll), Path.Combine(programFolder, AppAccessTypeLibrary), AppAccessClass);

    /// <summary>
    /// Creates coclass <paramref name="className"/> of <paramref name="typeLibrary"/> from the in-process COM server
    /// <paramref name="dll"/> without looking at the registry; returns its IDispatch object.
    /// </summary>
    internal static object CreateFromDll(string dll, string typeLibrary, string className)
    {
        var clsid = ClassId(typeLibrary, className)
            ?? throw new CimatronApiException("cimatron_not_installed", $"{typeLibrary} has no {className} class.");
        var module = LoadLibraryEx(dll, IntPtr.Zero, LoadWithAlteredSearchPath);
        if (module == IntPtr.Zero)
            throw new CimatronApiException("cimatron_not_available", $"{dll} could not be loaded (Windows error {Marshal.GetLastWin32Error()}).");
        var entry = GetProcAddress(module, "DllGetClassObject");
        if (entry == IntPtr.Zero)
            throw new CimatronApiException("cimatron_not_available", $"{dll} is not a COM server (no DllGetClassObject).");
        var getClassObject = Marshal.GetDelegateForFunctionPointer<DllGetClassObject>(entry);
        var iid = IidClassFactory;
        var result = getClassObject(ref clsid, ref iid, out var factoryPointer);
        if (result != 0 || factoryPointer == IntPtr.Zero)
            throw new CimatronApiException("cimatron_not_available", $"{dll} refused to make {className} (HRESULT 0x{result:X8}).");
        try
        {
            var factory = (IClassFactory)Marshal.GetObjectForIUnknown(factoryPointer);
            var dispatch = IidDispatch;
            result = factory.CreateInstance(IntPtr.Zero, ref dispatch, out var instance);
            if (result != 0 || instance == IntPtr.Zero)
                throw new CimatronApiException("cimatron_not_available", $"{className} could not be created (HRESULT 0x{result:X8}).");
            try
            {
                return Marshal.GetObjectForIUnknown(instance);
            }
            finally
            {
                Marshal.Release(instance);
            }
        }
        finally
        {
            Marshal.Release(factoryPointer);
        }
    }

    /// <summary>The class id of coclass <paramref name="name"/> in a type library file, or null.</summary>
    internal static Guid? ClassId(string library, string name)
    {
        if (!File.Exists(library) || LoadTypeLibEx(library, RegKindNone, out var typeLibrary) != 0) return null;
        try
        {
            for (var index = 0; index < typeLibrary.GetTypeInfoCount(); index += 1)
            {
                typeLibrary.GetDocumentation(index, out var typeName, out _, out _, out _);
                typeLibrary.GetTypeInfoType(index, out var kind);
                if (kind == TYPEKIND.TKIND_COCLASS && string.Equals(typeName, name, StringComparison.OrdinalIgnoreCase))
                {
                    typeLibrary.GetTypeInfo(index, out var info);
                    return WithAttributes(info, attributes => attributes.guid);
                }
            }
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(typeLibrary);
        }
    }

    /// <summary>Every class and interface of a type library with its methods and properties (for the probe).</summary>
    internal static IEnumerable<string> Describe(string library)
    {
        var lines = new List<string>();
        if (!File.Exists(library))
        {
            lines.Add($"{Path.GetFileName(library)}: not found");
            return lines;
        }
        if (LoadTypeLibEx(library, RegKindNone, out var typeLibrary) != 0)
        {
            lines.Add($"{Path.GetFileName(library)}: could not be read");
            return lines;
        }
        try
        {
            lines.Add($"== {Path.GetFileName(library)}");
            for (var index = 0; index < typeLibrary.GetTypeInfoCount(); index += 1)
            {
                typeLibrary.GetDocumentation(index, out var typeName, out _, out _, out _);
                typeLibrary.GetTypeInfoType(index, out var kind);
                typeLibrary.GetTypeInfo(index, out var info);
                switch (kind)
                {
                    case TYPEKIND.TKIND_COCLASS:
                        lines.Add($"coclass {typeName} {WithAttributes(info, attributes => attributes.guid):B}");
                        break;
                    case TYPEKIND.TKIND_INTERFACE:
                    case TYPEKIND.TKIND_DISPATCH:
                        lines.Add($"interface {typeName}");
                        foreach (var member in Members(info)) lines.Add("  " + member);
                        break;
                    case TYPEKIND.TKIND_ENUM:
                        lines.Add($"enum {typeName}");
                        break;
                }
                Marshal.ReleaseComObject(info);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(typeLibrary);
        }
        return lines;
    }

    private static T WithAttributes<T>(ITypeInfo info, Func<TYPEATTR, T> read)
    {
        info.GetTypeAttr(out var pointer);
        try
        {
            return read(Marshal.PtrToStructure<TYPEATTR>(pointer));
        }
        finally
        {
            info.ReleaseTypeAttr(pointer);
        }
    }

    private static IEnumerable<string> Members(ITypeInfo info)
    {
        var lines = new List<string>();
        var functions = WithAttributes(info, attributes => (int)attributes.cFuncs);
        for (var index = 0; index < functions; index += 1)
        {
            info.GetFuncDesc(index, out var pointer);
            try
            {
                var description = Marshal.PtrToStructure<FUNCDESC>(pointer);
                var names = new string[description.cParams + 1];
                info.GetNames(description.memid, names, names.Length, out var count);
                var kind = description.invkind switch
                {
                    INVOKEKIND.INVOKE_PROPERTYGET => "get ",
                    INVOKEKIND.INVOKE_PROPERTYPUT or INVOKEKIND.INVOKE_PROPERTYPUTREF => "set ",
                    _ => ""
                };
                var parameters = string.Join(", ", names.Skip(1).Take(Math.Max(0, count - 1)));
                lines.Add($"{kind}{names[0]}({parameters})");
            }
            finally
            {
                info.ReleaseFuncDesc(pointer);
            }
        }
        return lines;
    }
}
