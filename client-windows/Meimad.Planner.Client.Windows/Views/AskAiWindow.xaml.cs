using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Views;

public partial class AskAiWindow : Window
{
    private readonly AskAiViewModel viewModel;

    internal AskAiWindow(AskAiViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        // The main window's selection may change while this window stays open.
        Activated += (_, _) => viewModel.RefreshSelection();
        viewModel.Messages.CollectionChanged += ScrollToEnd;
        Loaded += (_, _) => QuestionBox.Focus();
        Closed += (_, _) => { viewModel.Messages.CollectionChanged -= ScrollToEnd; DataContext = null; };
    }

    private void ScrollToEnd(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(ConversationScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);

    private void QuestionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (viewModel.SendCommand.CanExecute(null)) viewModel.SendCommand.Execute(null);
    }
}
