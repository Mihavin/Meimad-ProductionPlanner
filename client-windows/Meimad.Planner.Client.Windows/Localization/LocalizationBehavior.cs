using System.Collections.Specialized;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace Meimad.Planner.Client.Windows.Localization;

// Applies the current language to the interface text of every WPF window.
//
// WPF raises Loaded only inside subtrees that declare a Loaded handler, so a plain TextBlock
// realized later (a tab page shown for the first time, an item template, a timeline block built
// in code, a context menu) never receives it. Text is therefore localized:
//   - in every open window after it loads and after each language change;
//   - for each element the first time layout gives it a size, because SizeChanged reaches every
//     element that layout sizes;
//   - in a tab page after it is shown, once layout has put the page into the visual tree, so a
//     page that was hidden during a language change catches up;
//   - in a context menu whenever it opens.
// A localized value is observed through a binding that the element itself holds, so observing
// it never keeps a closed window or a discarded item template alive.
internal static class LocalizationBehavior
{
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, LocalizedValue>> Values = new();
    private static readonly ConditionalWeakTable<object, object> ObservedColumnCollections = new();
    private static readonly ConditionalWeakTable<DependencyObject, DeferredLocalization> DeferredLocalizations = new();
    private static readonly ConditionalWeakTable<Window, object> InitializedWindows = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> RealizedElements = new();
    private static readonly object Marker = new();
    private static readonly Dictionary<DependencyProperty, DependencyProperty> ObserverByProperty = new();
    private static readonly Dictionary<DependencyProperty, DependencyProperty> PropertyByObserver = new();
    private static DispatcherOperation? pendingRelocalization;
    private static long applyCount;
    private static long fullTreePassCount;
    private static long visitedObjectCount;
    private static long languageChangeCount;
    private static bool initialized;

    static LocalizationBehavior()
    {
        DependencyProperty[] watched =
        [
            FrameworkElement.ToolTipProperty,
            AutomationProperties.NameProperty,
            AutomationProperties.HelpTextProperty,
            TextBlock.TextProperty,
            ContentControl.ContentProperty,
            ContentPresenter.ContentProperty,
            HeaderedContentControl.HeaderProperty,
            HeaderedItemsControl.HeaderProperty,
            Window.TitleProperty,
            DataGridColumn.HeaderProperty,
            GridViewColumn.HeaderProperty,
            Run.TextProperty
        ];
        foreach (var property in watched)
        {
            // Owners that share one property through AddOwner share its observer.
            if (ObserverByProperty.ContainsKey(property))
            {
                continue;
            }

            var observer = DependencyProperty.RegisterAttached(
                "ObservedText" + ObserverByProperty.Count.ToString(CultureInfo.InvariantCulture),
                typeof(object),
                typeof(LocalizationBehavior),
                new PropertyMetadata(null, OnObservedValueChanged));
            ObserverByProperty.Add(property, observer);
            PropertyByObserver.Add(observer, property);
        }
    }

    internal static LocalizationDiagnostics Diagnostics => new(
        Interlocked.Read(ref applyCount),
        Interlocked.Read(ref fullTreePassCount),
        Interlocked.Read(ref visitedObjectCount),
        Interlocked.Read(ref languageChangeCount),
        PollingEnabled: false);

    internal static void Initialize()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(FrameworkContentElement),
            FrameworkContentElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.SizeChangedEvent,
            new SizeChangedEventHandler(OnElementSizeChanged),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(TabControl),
            Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler(OnTabSelectionChanged),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(ContextMenu),
            ContextMenu.OpenedEvent,
            new RoutedEventHandler(OnContextMenuOpened),
            handledEventsToo: true);
        LocalizationService.Current.LanguageChanged += static (_, _) => QueueRelocalization();
    }

    internal static void ResetDiagnostics()
    {
        Interlocked.Exchange(ref applyCount, 0);
        Interlocked.Exchange(ref fullTreePassCount, 0);
        Interlocked.Exchange(ref visitedObjectCount, 0);
        Interlocked.Exchange(ref languageChangeCount, 0);
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement element)
        {
            LocalizeElement(element);
        }
        else if (e.OriginalSource is Run run)
        {
            Watch(run, Run.TextProperty);
        }
    }

    // Layout raises SizeChanged for every element it sizes, including the plain text that never
    // receives Loaded. The first one localizes an element realized after the last tree pass.
    private static void OnElementSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement element
            || !ReferenceEquals(e.OriginalSource, element)
            || RealizedElements.TryGetValue(element, out _))
        {
            return;
        }

        // English text is its own source. An element is watched once another language is
        // chosen, unless it still shows a translation that must return to English.
        if (LocalizationService.Current.IsSourceLanguage && !Values.TryGetValue(element, out _))
        {
            return;
        }

        RealizedElements.AddOrUpdate(element, Marker);
        LocalizeElement(element);
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window
            && ReferenceEquals(e.OriginalSource, window)
            && !InitializedWindows.TryGetValue(window, out _))
        {
            InitializedWindows.Add(window, new object());
            QueueTreeLocalization(window, DispatcherPriority.DataBind);
        }
    }

    private static void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is TabControl tabs
            && ReferenceEquals(e.OriginalSource, tabs)
            && tabs.IsLoaded)
        {
            // The selected page joins the visual tree during the next layout pass, so it is
            // localized after layout (Loaded priority) rather than before it.
            QueueTreeLocalization(tabs, DispatcherPriority.Loaded);
        }
    }

    private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && ReferenceEquals(e.OriginalSource, menu))
        {
            LocalizeMenuItems(menu);
            LocalizeTree(menu);
        }
    }

    // Submenu items live in their own popups, outside the menu's visual tree.
    private static void LocalizeMenuItems(ItemsControl menu)
    {
        foreach (var item in menu.Items)
        {
            if (item is FrameworkElement element)
            {
                LocalizeElement(element);
            }
            if (item is ItemsControl submenu)
            {
                LocalizeMenuItems(submenu);
            }
        }
    }

    private static void QueueTreeLocalization(DependencyObject root, DispatcherPriority priority)
    {
        var state = DeferredLocalizations.GetOrCreateValue(root);
        if (state.Operation is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        state.Operation = root.Dispatcher.BeginInvoke(
            () =>
            {
                state.Operation = null;
                if (root is Window window)
                {
                    LocalizeWindow(window);
                }
                else if (pendingRelocalization is not { Status: DispatcherOperationStatus.Pending })
                {
                    LocalizeTree(root);
                }
            },
            priority);
    }

    private static void LocalizeElement(FrameworkElement element)
    {
        Watch(element, FrameworkElement.ToolTipProperty);
        Watch(element, AutomationProperties.NameProperty);
        Watch(element, AutomationProperties.HelpTextProperty);

        if (element is TextBlock textBlock)
        {
            if (ComposedInlines(textBlock) is { } inlines)
            {
                LocalizeInlines(inlines);
            }
            else
            {
                Watch(textBlock, TextBlock.TextProperty);
            }
        }
        if (element is ContentControl contentControl)
        {
            Watch(contentControl, ContentControl.ContentProperty);
        }
        if (element is ContentPresenter contentPresenter)
        {
            Watch(contentPresenter, ContentPresenter.ContentProperty);
        }
        if (element is HeaderedContentControl headeredContentControl)
        {
            Watch(headeredContentControl, HeaderedContentControl.HeaderProperty);
        }
        if (element is HeaderedItemsControl headeredItemsControl)
        {
            Watch(headeredItemsControl, HeaderedItemsControl.HeaderProperty);
        }
        if (element is Window window)
        {
            Watch(window, Window.TitleProperty);
            ApplyDirection(window);
        }
        if (element is DataGrid dataGrid)
        {
            foreach (var column in dataGrid.Columns)
            {
                Watch(column, DataGridColumn.HeaderProperty);
            }
            ObserveColumns(dataGrid.Columns);
        }
        if (element is ListView { View: GridView gridView })
        {
            foreach (var column in gridView.Columns)
            {
                Watch(column, GridViewColumn.HeaderProperty);
            }
            ObserveColumns(gridView.Columns);
        }
    }

    // A TextBlock composed of several inlines, or of one bound run, keeps its runs and their
    // bindings: each run is localized on its own instead of replacing the whole text. Reading the
    // logical children leaves a plain TextBlock's simple text content intact.
    private static List<Inline>? ComposedInlines(TextBlock textBlock)
    {
        List<Inline>? inlines = null;
        foreach (var child in LogicalTreeHelper.GetChildren(textBlock))
        {
            if (child is Inline inline)
            {
                (inlines ??= []).Add(inline);
            }
        }

        if (inlines is null
            || (inlines.Count == 1
                && inlines[0] is Run run
                && !BindingOperations.IsDataBound(run, Run.TextProperty)))
        {
            return null;
        }

        return inlines;
    }

    private static void LocalizeInlines(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run)
            {
                Watch(run, Run.TextProperty);
            }
            else if (inline is Span span)
            {
                LocalizeInlines(span.Inlines);
            }
        }
    }

    private static void ObserveColumns(object columns)
    {
        if (columns is not INotifyCollectionChanged observable
            || ObservedColumnCollections.TryGetValue(columns, out _))
        {
            return;
        }

        ObservedColumnCollections.Add(columns, new object());
        observable.CollectionChanged += OnColumnsChanged;
    }

    private static void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
        {
            return;
        }

        foreach (var item in e.NewItems)
        {
            if (item is DataGridColumn dataGridColumn)
            {
                Watch(dataGridColumn, DataGridColumn.HeaderProperty);
            }
            else if (item is GridViewColumn gridViewColumn)
            {
                Watch(gridViewColumn, GridViewColumn.HeaderProperty);
            }
        }
    }

    private static void Watch(DependencyObject target, DependencyProperty property)
    {
        if (target.GetValue(property) is not string current)
        {
            return;
        }

        var hasValues = Values.TryGetValue(target, out var values);
        if (hasValues && values!.TryGetValue(property, out var existing))
        {
            Apply(target, property, existing);
            return;
        }

        // English text is its own source; it is watched once another language is chosen.
        if (LocalizationService.Current.IsSourceLanguage)
        {
            return;
        }

        values ??= Values.GetOrCreateValue(target);
        var localizedValue = new LocalizedValue(LocalizationService.Current.ResolveSource(current));
        values[property] = localizedValue;
        Apply(target, property, localizedValue);
        if (ObserverByProperty.TryGetValue(property, out var observer))
        {
            // The binding lives on the element, so the observation ends with the element.
            BindingOperations.SetBinding(target, observer, new Binding
            {
                Source = target,
                Path = new PropertyPath(property),
                Mode = BindingMode.OneWay
            });
        }
    }

    private static void OnObservedValueChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (PropertyByObserver.TryGetValue(e.Property, out var property)
            && Values.TryGetValue(target, out var values)
            && values.TryGetValue(property, out var localizedValue))
        {
            OnValueChanged(target, property, localizedValue);
        }
    }

    private static void OnValueChanged(
        DependencyObject target,
        DependencyProperty property,
        LocalizedValue localizedValue)
    {
        if (localizedValue.IsApplying || target.GetValue(property) is not string current
            || string.Equals(current, localizedValue.Applied, StringComparison.Ordinal))
        {
            return;
        }

        localizedValue.Source = LocalizationService.Current.ResolveSource(current);
        Apply(target, property, localizedValue);
    }

    private static void Apply(
        DependencyObject target,
        DependencyProperty property,
        LocalizedValue localizedValue)
    {
        var translated = LocalizationService.Current.Translate(localizedValue.Source);
        localizedValue.Applied = translated;
        if (string.Equals(target.GetValue(property) as string, translated, StringComparison.Ordinal))
        {
            return;
        }

        // Setting the text of a TextBlock that has since been composed from runs would discard
        // the runs and their bindings; its runs are localized instead.
        if (target is TextBlock textBlock
            && property == TextBlock.TextProperty
            && ComposedInlines(textBlock) is { } inlines)
        {
            localizedValue.IsApplying = true;
            try
            {
                LocalizeInlines(inlines);
            }
            finally
            {
                localizedValue.IsApplying = false;
            }
            localizedValue.Applied = textBlock.Text;
            return;
        }

        localizedValue.IsApplying = true;
        try
        {
            target.SetCurrentValue(property, translated);
            Interlocked.Increment(ref applyCount);
        }
        finally
        {
            localizedValue.IsApplying = false;
        }
    }

    private static void QueueRelocalization()
    {
        Interlocked.Increment(ref languageChangeCount);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(QueueRelocalization, DispatcherPriority.DataBind);
            return;
        }

        if (pendingRelocalization is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        pendingRelocalization = dispatcher.BeginInvoke(
            static () =>
            {
                pendingRelocalization = null;
                LocalizeApplicationWindows();
            },
            DispatcherPriority.DataBind);
    }

    private static void LocalizeApplicationWindows()
    {
        if (Application.Current is null)
        {
            return;
        }

        foreach (Window window in Application.Current.Windows)
        {
            LocalizeWindow(window);
        }
    }

    internal static void LocalizeWindow(Window window)
    {
        Interlocked.Increment(ref fullTreePassCount);
        ApplyDirection(window);
        LocalizeTree(window);
    }

    internal static void LocalizeTree(DependencyObject root)
    {
        var pending = new Queue<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            Interlocked.Increment(ref visitedObjectCount);
            if (current is FrameworkElement element)
            {
                LocalizeElement(element);
            }

            if (current is not Visual
                && current is not System.Windows.Media.Media3D.Visual3D)
            {
                continue;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < childCount; index++)
            {
                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
        }
    }

    private static void ApplyDirection(Window window)
    {
        var service = LocalizationService.Current;
        var direction = service.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (window.FlowDirection != direction)
        {
            window.FlowDirection = direction;
        }

        var language = XmlLanguage.GetLanguage(service.CurrentLanguage);
        if (window.Language != language)
        {
            window.Language = language;
        }
    }

    private sealed class LocalizedValue(string source)
    {
        internal string Source { get; set; } = source;
        internal string? Applied { get; set; }
        internal bool IsApplying { get; set; }
    }

    private sealed class DeferredLocalization
    {
        internal DispatcherOperation? Operation { get; set; }
    }
}

internal readonly record struct LocalizationDiagnostics(
    long ApplyCount,
    long FullTreePassCount,
    long VisitedObjectCount,
    long LanguageChangeCount,
    bool PollingEnabled);
