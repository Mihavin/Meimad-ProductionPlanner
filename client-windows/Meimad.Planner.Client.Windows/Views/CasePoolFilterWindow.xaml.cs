using System.Windows;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>Non-modal window with the Case pool filter chips; it edits the pool's view model live.</summary>
public partial class CasePoolFilterWindow : Window
{
    public CasePoolFilterWindow() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
