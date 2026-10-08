using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.CompilerServices;

namespace Meimad.Planner.Client.Windows.Themes;

/// <summary>Client-only appearance; never changes planning state or the operational palette.</summary>
internal static class WorkbenchTheme
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meimad Planner", "workbench-theme.txt");
    private static bool initialized;
    private static readonly ConditionalWeakTable<FrameworkElement, object> Adapted = new();
    internal static string Current { get; private set; } = "graphite";
    internal static event EventHandler? Changed;

    internal static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        // Programmatic dialogs use the same resources as XAML screens. Only literal neutral
        // control brushes are adapted; bindings, operational colors and drawing primitives stay intact.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(AdaptControl));
    }

    internal static void Load()
    {
        var theme = "graphite";
        try { if (File.Exists(PreferencePath)) theme = File.ReadAllText(PreferencePath).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Apply(theme, false);
    }

    internal static void Apply(string theme, bool persist = true)
    {
        if (Application.Current is not { } app) return;
        app.Dispatcher.VerifyAccess();
        Current = theme == "light" ? "light" : "graphite";
        string[] keys = ["CanvasBrush", "PanelBrush", "RaisedBrush", "BorderBrush", "PrimaryTextBrush", "MutedTextBrush",
            "AccentBrush", "AccentTextBrush", "SelectionBrush", "SuccessTextBrush", "WarningTextBrush", "ErrorTextBrush",
            "InfoTextBrush", "WarningSurfaceBrush", "InfoSurfaceBrush"];
        string[] values = Current == "light"
            ? ["#EDF0F1", "#FFFFFF", "#F3F5F5", "#D8DFE2", "#162631", "#566977", "#006C73", "#FFFFFF", "#D5EAEC",
               "#286D30", "#97600B", "#A73231", "#305F99", "#FFF4DC", "#E6EFF8"]
            : ["#0C1118", "#121A24", "#192330", "#2A3746", "#E8EFF6", "#9EAFC1", "#6ADEE1", "#0C1118", "#24434A",
               "#A5D99F", "#F4C575", "#F09996", "#9BBAFA", "#322A1E", "#192C3D"];
        for (var i = 0; i < keys.Length; i++)
        {
            var color = (Color)ColorConverter.ConvertFromString(values[i]);
            if (SystemParameters.HighContrast)
                color = keys[i] switch
                {
                    "AccentBrush" or "SelectionBrush" => SystemColors.HighlightColor,
                    "AccentTextBrush" => SystemColors.HighlightTextColor,
                    "CanvasBrush" or "PanelBrush" or "RaisedBrush" or "WarningSurfaceBrush" or "InfoSurfaceBrush" => SystemColors.WindowColor,
                    _ => SystemColors.WindowTextColor
                };
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app.Resources[keys[i]] = brush;
        }
        foreach (var (key, token) in new (ResourceKey, string)[]
        {
            (SystemColors.WindowBrushKey,"PanelBrush"), (SystemColors.WindowTextBrushKey,"PrimaryTextBrush"),
            (SystemColors.ControlBrushKey,"RaisedBrush"), (SystemColors.ControlTextBrushKey,"PrimaryTextBrush"),
            (SystemColors.ControlLightBrushKey,"RaisedBrush"), (SystemColors.ControlLightLightBrushKey,"PanelBrush"),
            (SystemColors.ControlDarkBrushKey,"BorderBrush"), (SystemColors.ControlDarkDarkBrushKey,"BorderBrush"),
            (SystemColors.HighlightBrushKey,"SelectionBrush"), (SystemColors.HighlightTextBrushKey,"PrimaryTextBrush"),
            (SystemColors.InactiveSelectionHighlightBrushKey,"SelectionBrush"),
            (SystemColors.InactiveSelectionHighlightTextBrushKey,"PrimaryTextBrush"),
            (SystemColors.GrayTextBrushKey,"MutedTextBrush")
        }) app.Resources[key] = app.Resources[token];
        if (persist)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!); File.WriteAllText(PreferencePath, Current); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Appearance remains usable for this session. */ }
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void AdaptControl(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement element || !ReferenceEquals(args.OriginalSource, element)) return;
        // Tab navigation reloads existing visual trees. Their dynamic resources already follow
        // the theme, so scan literal brushes once without retaining discarded dialogs.
        if (Adapted.TryGetValue(element, out _)) return;
        Adapted.Add(element, new object());
        if (element is Window && element.ReadLocalValue(FrameworkElement.StyleProperty) == DependencyProperty.UnsetValue)
            element.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
        // Dedicated technical canvases keep explicit ink/status colors, including printed reports.
        if (element is Control control)
        {
            Adapt(element, Control.BackgroundProperty, false);
            Adapt(element, Control.ForegroundProperty, true);
            Adapt(element, Control.BorderBrushProperty, false, true);
        }
        else if (element is TextBlock) Adapt(element, TextBlock.ForegroundProperty, true);
        else if (element is Border)
        {
            Adapt(element, Border.BackgroundProperty, false);
            Adapt(element, Border.BorderBrushProperty, false, true);
        }
    }

    private static void Adapt(FrameworkElement element, DependencyProperty property, bool foreground, bool border = false)
    {
        if (element.ReadLocalValue(property) is not SolidColorBrush brush) return;
        var c = brush.Color;
        if (c.A != 255) return;
        var neutral = Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) < 24;
        if (!neutral) return;
        // Graphical canvases are deliberately white and are not user-input surfaces.
        if (Window.GetWindow(element) is null || IsTechnicalCanvasChild(element)) return;
        var token = border ? "BorderBrush" : foreground
            ? (c.R > 90 && c.R < 210 ? "MutedTextBrush" : "PrimaryTextBrush")
            : (c.R >= 245 ? "PanelBrush" : "RaisedBrush");
        element.SetResourceReference(property, token);
    }

    private static bool IsTechnicalCanvasChild(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is Canvas or System.Windows.Media.Media3D.Viewport3DVisual) return true;
        return false;
    }
}
