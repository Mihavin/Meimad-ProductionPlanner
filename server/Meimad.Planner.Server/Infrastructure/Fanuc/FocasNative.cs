using System.Reflection;
using System.Runtime.InteropServices;

namespace Meimad.Planner.Server.Infrastructure.Fanuc;

/// <summary>
/// P/Invoke surface of the FANUC FOCAS 2 Ethernet library. The library is licensed by FANUC
/// and is not redistributed with the Server: <c>Fwlib64.dll</c> (plus <c>fwlibe64.dll</c>) must
/// be copied next to the Server executable, into its <c>focas</c> subfolder, or into the folder
/// named by <c>MEIMAD_FOCAS_LIBRARY_DIR</c>. Only read functions are declared.
/// </summary>
internal static class FocasNative
{
    internal const string LibraryName = "Fwlib";
    internal const string LibraryDirectoryVariable = "MEIMAD_FOCAS_LIBRARY_DIR";
    internal const short EW_OK = 0;
    internal const short EW_FUNC = 1;
    internal const short EW_BUFFER = 10;
    internal const short EW_SOCKET = -16;
    internal const short EW_NODLL = -15;
    internal const short EW_HANDLE = -8;
    private static readonly object StartupGate = new();
    private static bool started;

    static FocasNative() => NativeLibrary.SetDllImportResolver(typeof(FocasNative).Assembly, Resolve);

    internal static string LibraryFileName => Environment.Is64BitProcess ? "Fwlib64.dll" : "Fwlib32.dll";

    internal static IReadOnlyList<string> CandidateDirectories()
    {
        var directories = new List<string>();
        var configured = Environment.GetEnvironmentVariable(LibraryDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(configured)) directories.Add(configured.Trim());
        directories.Add(AppContext.BaseDirectory);
        directories.Add(Path.Combine(AppContext.BaseDirectory, "focas"));
        return directories;
    }

    /// <summary>Loads the library once so a missing DLL is reported as configuration, not as a crash inside a poll.</summary>
    internal static bool TryEnsureLoaded(out string message)
    {
        foreach (var directory in CandidateDirectories())
        {
            var path = Path.Combine(directory, LibraryFileName);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out _))
            {
                PreloadCompanions(directory);
                message = path;
                return true;
            }
        }
        if (NativeLibrary.TryLoad(LibraryFileName, out _))
        {
            message = LibraryFileName;
            return true;
        }
        message = $"FOCAS library {LibraryFileName} was not found. Copy the FANUC FOCAS 2 library next to the Server "
            + $"({string.Join("; ", CandidateDirectories())}) or set {LibraryDirectoryVariable}.";
        return false;
    }

    /// <summary>
    /// The 32-bit Windows library requires one process-wide start-up before the first call;
    /// Fwlib64.dll does not export it, so its absence is not an error.
    /// </summary>
    internal static void EnsureStarted()
    {
        lock (StartupGate)
        {
            if (started) return;
            started = true;
            try
            {
                var log = Path.Combine(Path.GetTempPath(), "meimad-focas.log");
                _ = cnc_startupprocess(0, log);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _ = cnc_exitprocess(); } catch { } };
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
            {
                // Not part of this library build.
            }
        }
    }

    /// <summary>
    /// Fwlib64.dll loads its Ethernet part (fwlibe64.dll) and the series-specific fwlib*64.dll by
    /// name at run time, which Windows resolves against the process directory, not against the
    /// folder the main library came from. Loading them from the same folder first makes a
    /// <c>focas</c> subfolder or an external directory work; anything that fails to load is
    /// simply left to FOCAS, which then reports EW_NODLL.
    /// </summary>
    private static void PreloadCompanions(string directory)
    {
        IEnumerable<string> companions;
        try { companions = Directory.EnumerateFiles(directory, "fwlib*.dll"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
        foreach (var companion in companions)
        {
            if (string.Equals(Path.GetFileName(companion), LibraryFileName, StringComparison.OrdinalIgnoreCase)) continue;
            _ = NativeLibrary.TryLoad(companion, out _);
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal)) return IntPtr.Zero;
        foreach (var directory in CandidateDirectories())
        {
            var path = Path.Combine(directory, LibraryFileName);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
            {
                PreloadCompanions(directory);
                return handle;
            }
        }
        return NativeLibrary.TryLoad(LibraryFileName, out var fallback) ? fallback : IntPtr.Zero;
    }

    [DllImport(LibraryName, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern short cnc_startupprocess(int level, string filename);

    [DllImport(LibraryName)]
    internal static extern short cnc_exitprocess();

    [DllImport(LibraryName, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern short cnc_allclibhndl3(string ip, ushort port, int timeoutSeconds, out ushort handle);

    [DllImport(LibraryName)]
    internal static extern short cnc_freelibhndl(ushort handle);

    [DllImport(LibraryName)]
    internal static extern short cnc_statinfo(ushort handle, out ODBST status);

    [DllImport(LibraryName)]
    internal static extern short cnc_exeprgname(ushort handle, out ODBEXEPRG program);

    [DllImport(LibraryName)]
    internal static extern short cnc_rdprgnum(ushort handle, out ODBPRO program);

    [DllImport(LibraryName)]
    internal static extern short cnc_rdparam(ushort handle, short number, short axis, short length, ref IODBPSD parameter);

    [DllImport(LibraryName)]
    internal static extern short cnc_acts(ushort handle, out ODBACT actual);

    [DllImport(LibraryName)]
    internal static extern short cnc_actf(ushort handle, out ODBACT actual);

    [DllImport(LibraryName)]
    internal static extern short cnc_rdalmmsg2(ushort handle, short type, ref short count, byte[] messages);

    [DllImport(LibraryName, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern short cnc_upstart4(ushort handle, short type, string path);

    [DllImport(LibraryName)]
    internal static extern short cnc_upload4(ushort handle, ref int length, byte[] data);

    [DllImport(LibraryName)]
    internal static extern short cnc_upend4(ushort handle);

    /// <summary>cnc_statinfo: running/automatic/motion/alarm/emergency/edit status (Series 16i/18i/21i/0i/30i layout).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ODBST
    {
        public short dummy;
        public short tmmode;
        public short aut;
        public short run;
        public short motion;
        public short mstb;
        public short emergency;
        public short alarm;
        public short edit;
    }

    /// <summary>cnc_exeprgname: executing program name and O-number.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ODBEXEPRG
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)]
        public byte[] name;
        public int o_num;
    }

    /// <summary>cnc_rdprgnum with 4-digit program numbers (older series fallback).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ODBPRO
    {
        public short dummy0;
        public short dummy1;
        public short data;
        public short mdata;
    }

    /// <summary>cnc_rdparam for a non-axis parameter read with length 8 (datano, type, long value).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IODBPSD
    {
        public short datano;
        public short type;
        public int ldata;
        public int reserved0;
        public int reserved1;
        public int reserved2;
        public int reserved3;
    }

    /// <summary>cnc_acts / cnc_actf actual value.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ODBACT
    {
        public short dummy0;
        public short dummy1;
        public int data;
    }

    /// <summary>Byte size of one ODBALMMSG2 record (alm_no, type, axis, dummy, msg_len, alm_msg[64]).</summary>
    internal const int AlarmMessageSize = 76;
}
