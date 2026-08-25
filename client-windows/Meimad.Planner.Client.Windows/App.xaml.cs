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
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeimadPlanner", "logs");
            Directory.CreateDirectory(directory);
            var logPath = Path.Combine(directory, "client-startup-error.log");
            File.WriteAllText(logPath, exception.ToString());
            MessageBox.Show(
                $"Meimad Planner could not start. Diagnostic details were saved to:{Environment.NewLine}{logPath}",
                "Meimad Planner startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
