using System.Windows.Controls;

namespace Meimad.Planner.Client.Windows.Views;

public partial class MachineUsageView : UserControl
{
    public MachineUsageView()
    {
        InitializeComponent();
        NoDataSwatch.Background = MachineUsageHistoryChart.KindBrush("No data");
    }
}
