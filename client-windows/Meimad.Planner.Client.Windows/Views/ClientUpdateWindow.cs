using System.IO;
using System.Windows;
using System.Windows.Controls;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// "New version available, the client will be installed": downloads the client MSI the Server
/// distributes, starts the installer, and closes the client so the installer can replace it.
/// </summary>
internal sealed class ClientUpdateWindow : Window
{
    private readonly ClientUpdateAvailableEventArgs update;
    private readonly ProgressBar progress;
    private readonly TextBlock status;
    private readonly Button cancelButton;
    private readonly CancellationTokenSource cancellation = new();
    private bool finished;

    private ClientUpdateWindow(ClientUpdateAvailableEventArgs update)
    {
        this.update = update;
        Title = "Meimad Planner update";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = "New version available",
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 8)
        });
        root.Children.Add(new TextBlock
        {
            Text = update.Decision.Message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        progress = new ProgressBar
        {
            Height = 18,
            Minimum = 0,
            Maximum = Math.Max(update.Manifest.ByteLength ?? 0, 1),
            IsIndeterminate = update.Manifest.ByteLength is null or <= 0,
            Margin = new Thickness(0, 0, 0, 8)
        };
        root.Children.Add(progress);
        status = new TextBlock
        {
            Text = "Preparing the download…",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        root.Children.Add(status);

        cancelButton = new Button { Content = "Cancel", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        cancelButton.Click += (_, _) =>
        {
            if (finished)
            {
                DialogResult = false;
                return;
            }

            cancellation.Cancel();
        };
        root.Children.Add(cancelButton);
        Content = root;

        Loaded += async (_, _) => await RunAsync();
        Closing += (_, _) =>
        {
            if (!finished)
            {
                cancellation.Cancel();
            }
        };
    }

    internal static void Show(Window owner, ClientUpdateAvailableEventArgs update)
    {
        var window = new ClientUpdateWindow(update) { Owner = owner };
        window.ShowDialog();
    }

    private async Task RunAsync()
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeimadPlanner", "updates");
            Directory.CreateDirectory(folder);
            foreach (var stale in Directory.GetFiles(folder))
            {
                try
                {
                    File.Delete(stale);
                }
                catch (IOException)
                {
                    // A previous installer may still be open; the download picks a unique name.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            status.Text = "Downloading the installer from the Server…";
            var reporter = new Progress<long>(bytes =>
            {
                progress.IsIndeterminate = false;
                progress.Value = Math.Min(bytes, progress.Maximum);
            });
            var download = await update.DownloadAsync(folder, reporter, cancellation.Token);

            status.Text = "Starting the installer. The client closes now and starts again when the installation has finished.";
            cancelButton.IsEnabled = false;
            finished = true;
            ClientInstallerLauncher.Launch(download.LocalPath, Environment.ProcessPath);
            await Task.Delay(TimeSpan.FromSeconds(1));
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            finished = true;
            DialogResult = false;
        }
        catch (Exception exception)
        {
            finished = true;
            progress.IsIndeterminate = false;
            status.Text = $"The update could not be installed: {exception.Message} "
                + "Close this window to continue with the current client.";
            cancelButton.Content = "Close";
        }
    }
}
