using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Meimad.CimatronExporter;

/// <summary>
/// Starts the exporter window. The three one-click entries end here: the Cimatron toolbar button and the desktop shortcut
/// run the exe with no argument (or <c>--ui</c>); the post-processor hook runs it with <c>--from-post</c>, which returns at
/// once after starting the window as a separate process, so Cimatron's post never waits for the export.
/// </summary>
internal static class LauncherHost
{
    public static int Run(LauncherStart start)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The exporter window needs Windows.");
            return 3;
        }

        return start.FromPost ? FromPost(start) : Window(start);
    }

    private static int FromPost(LauncherStart start)
    {
        // The exporter's own post (it posts the toolpath again) and a post made while the window is open start nothing.
        if (ExporterLocks.IsHeld(ExporterLocks.Export) || ExporterLocks.IsHeld(ExporterLocks.Window)) return 0;
        var info = new ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Meimad.CimatronExporter.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in LauncherPlan.RelaunchArguments(start)) info.ArgumentList.Add(argument);
        try
        {
            using var _ = Process.Start(info);
            return 0;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Console.Error.WriteLine($"The Meimad export window could not be started: {exception.Message}");
            return 3;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int Window(LauncherStart start)
    {
        // No console window behind the exporter window (a shortcut start would otherwise leave one open).
        FreeConsole();
        using var held = ExporterLocks.TryTake(ExporterLocks.Window);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        if (held is null)
        {
            MessageBox.Show("The Cimatron → Meimad export window is already open.", "Meimad export", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        using var window = new ExporterWindow(start, LauncherSettings.DefaultPath);
        Application.Run(window);
        return 0;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
}
