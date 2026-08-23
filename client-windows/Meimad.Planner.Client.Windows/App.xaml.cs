using System.Windows;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows;

public partial class App : Application
{
    public App()
    {
        LocalizationBehavior.Initialize();
        _ = LocalizationService.Current;
    }
}
