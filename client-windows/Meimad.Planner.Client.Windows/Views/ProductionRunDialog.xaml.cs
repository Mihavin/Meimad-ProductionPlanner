using System.Windows;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Views;
public partial class ProductionRunDialog:Window
{
    internal ProductionRunDialog(ProductionRunDialogViewModel viewModel){InitializeComponent();DataContext=viewModel;}
    internal ProductionRunDialogViewModel ViewModel => (ProductionRunDialogViewModel)DataContext;
    private void Save_Click(object sender,RoutedEventArgs e){if(ViewModel.CanSave)DialogResult=true;}
}
