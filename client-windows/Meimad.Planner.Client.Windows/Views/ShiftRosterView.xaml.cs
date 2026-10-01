using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Views;

public partial class ShiftRosterView : UserControl
{
    public ShiftRosterView() => InitializeComponent();

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShiftRosterViewModel viewModel) return;
        var value = TextPromptWindow.Show(
            Window.GetWindow(this),
            "How many weeks to print, starting with the displayed week? Each week is printed on its own landscape page.",
            "Print Shift Roster",
            "1");
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var weeks)
            || weeks is < 1 or > 53)
        {
            LocalizedMessageBox.Show("The number of weeks must be a whole number from 1 to 53.",
                "Print Shift Roster", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pages = await viewModel.ReadWeeksForPrintAsync(weeks);
        if (pages is null) return;
        ShiftRosterPrinter.Print(pages);
    }
}
