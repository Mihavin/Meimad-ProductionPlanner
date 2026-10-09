using System.Windows.Controls;
using System.Windows;
namespace Meimad.Planner.Client.Windows.Views;
public partial class ShopOverviewView : UserControl
{
    public ShopOverviewView()
    {
        InitializeComponent();
        ShopShiftEvidence.Expanded += (_, _) => ShowEvidence(true);
        ShopShiftEvidence.Collapsed += (_, _) => ShowEvidence(false);
    }
    private void ShowEvidence(bool expanded)
    {
        Grid.SetRow(ShopShiftEvidence, expanded ? 1 : 4);
        Grid.SetRowSpan(ShopShiftEvidence, expanded ? 4 : 1);
        var visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        MachineList.Visibility = visibility; OutputHeading.Visibility = visibility; OutputList.Visibility = visibility;
    }
}
