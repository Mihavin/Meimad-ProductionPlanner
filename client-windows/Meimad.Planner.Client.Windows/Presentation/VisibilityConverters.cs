using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>Collapses an element when the bound boolean is true; the mirror of the WPF converter.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
