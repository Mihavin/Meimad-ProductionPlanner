using System.Diagnostics;

namespace Meimad.Planner.Client.Windows.Presentation;

internal interface IWorkingFolderLauncher
{
    void Open(string path);
}

internal sealed class WorkingFolderLauncher : IWorkingFolderLauncher
{
    public void Open(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}
