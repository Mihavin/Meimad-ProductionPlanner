using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class ViewStartupTests
{
    [Fact]
    public void Case_workspace_read_only_bindings_attach_without_a_startup_exception()
    {
        Exception? startupException = null;
        var thread = new Thread(() =>
        {
            App? application = null;
            Window? window = null;
            try
            {
                application = new App
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                application.InitializeComponent();
                window = new Window
                {
                    Content = new CaseWorkspaceView
                    {
                        DataContext = new ReadOnlyCaseTotals()
                    }
                };

                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    DispatcherPriority.ApplicationIdle);
            }
            catch (Exception exception)
            {
                startupException = exception;
            }
            finally
            {
                window?.Close();
                application?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The WPF startup check timed out.");
        Assert.Null(startupException);
    }

    private sealed class ReadOnlyCaseTotals
    {
        public string CurrentSetupTime => "00:00:00";

        public string CurrentCycleTimePerPart => "00:00:00";
    }
}
