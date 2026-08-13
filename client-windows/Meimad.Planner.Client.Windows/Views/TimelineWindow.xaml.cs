using System.Windows;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Views;

public partial class TimelineWindow : Window
{
    internal TimelineWindow(TimelineViewModel timeline)
    {
        InitializeComponent();
        Title = "Meimad Planner \u2014 Timeline";
        DataContext = timeline;
        Closed += (_, _) => DataContext = null;
    }
}
