using System.Windows;
using System.IO;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows;

public partial class App : Application
{
    public App()
    {
        LocalizationBehavior.Initialize();
        _ = LocalizationService.Current;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception exception)
        {
            var logPath = WriteLog("client-startup-error.log", exception, append: false);
            LocalizedMessageBox.Show(
                $"Meimad Planner could not start. Diagnostic details were saved to:{Environment.NewLine}{logPath}",
                "Meimad Planner startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    // An exception escaping a UI event handler (typically an async void click handler) would
    // otherwise terminate the whole client. Log it, tell the user, and keep the session alive.
    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        string? logPath = null;
        try
        {
            logPath = WriteLog("client-unhandled-error.log", e.Exception, append: true);
        }
        catch
        {
            // Logging must never turn a recoverable error into a crash.
        }

        e.Handled = true;
        LocalizedMessageBox.Show(
            $"An unexpected error occurred. The last action may not have been applied.{Environment.NewLine}{Environment.NewLine}"
            + $"{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}"
            + (logPath is null ? string.Empty : $"Details were saved to:{Environment.NewLine}{logPath}"),
            "Meimad Planner error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string WriteLog(string fileName, Exception exception, bool append)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeimadPlanner", "logs");
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, fileName);
        var entry = $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
        if (append)
        {
            File.AppendAllText(logPath, entry);
        }
        else
        {
            File.WriteAllText(logPath, entry);
        }

        return logPath;
    }
}
