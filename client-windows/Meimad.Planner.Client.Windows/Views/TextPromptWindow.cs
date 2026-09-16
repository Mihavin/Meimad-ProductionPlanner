using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// Minimal modal single-line text prompt. Replaces Microsoft.VisualBasic.Interaction.InputBox,
/// which throws PlatformNotSupportedException in a WPF-only application (it needs Windows Forms).
/// </summary>
internal sealed class TextPromptWindow : Window
{
    private readonly TextBox input;

    private TextPromptWindow(string prompt, string title, string defaultValue)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        input = new TextBox
        {
            Text = defaultValue,
            Margin = new Thickness(0, 0, 0, 16),
            Padding = new Thickness(4)
        };
        AutomationProperties.SetName(input, title);
        root.Children.Add(input);

        var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        okButton.Click += (_, _) => DialogResult = true;
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
    }

    /// <summary>Returns the entered text, or null when the user cancelled.</summary>
    public static string? Show(Window? owner, string prompt, string title, string defaultValue = "")
    {
        var window = new TextPromptWindow(prompt, title, defaultValue);
        if (owner is not null)
        {
            window.Owner = owner;
        }

        return window.ShowDialog() == true ? window.input.Text : null;
    }
}
