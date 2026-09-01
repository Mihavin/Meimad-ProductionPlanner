using System.Windows;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Views;

public partial class TimelineWindow : Window
{
    internal event EventHandler<TimelineOperationActionRequest>? OperationActionRequested;

    internal TimelineWindow(TimelineViewModel timeline)
    {
        InitializeComponent();
        ExternalTimelineView.ShowGraphAndLegendOnly();
        Title = "Meimad Planner \u2014 Timeline";
        DataContext = timeline;
        ExternalTimelineView.OperationActionRequested += (_, request) => OperationActionRequested?.Invoke(this, request);
        Closed += (_, _) => DataContext = null;
    }
}
