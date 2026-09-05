using System.Windows.Controls;
using System.Windows.Input;

namespace Meimad.Planner.Client.Windows.Views;

public partial class PreparationQueueView : UserControl
{
    public PreparationQueueView() => InitializeComponent();

    private void DataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row) row.IsSelected = true;
    }
}
