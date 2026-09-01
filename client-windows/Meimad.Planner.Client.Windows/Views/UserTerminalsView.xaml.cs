using System.Windows.Controls;
namespace Meimad.Planner.Client.Windows.Views;
public partial class UserTerminalsView : UserControl
{
    public UserTerminalsView() => InitializeComponent();

    private async void DeleteTerminal_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not Presentation.UserTerminalsViewModel { Selected: { } terminal } viewModel) return;
        if (System.Windows.MessageBox.Show($"Delete terminal {terminal.DeviceName}? Referenced terminal history is protected by the Server.",
            "Delete terminal",System.Windows.MessageBoxButton.YesNo,System.Windows.MessageBoxImage.Warning,System.Windows.MessageBoxResult.No)
            == System.Windows.MessageBoxResult.Yes)
            await viewModel.DeleteAsync();
    }
}
