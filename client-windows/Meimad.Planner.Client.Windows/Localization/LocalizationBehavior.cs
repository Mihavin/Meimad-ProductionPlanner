using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace Meimad.Planner.Client.Windows.Localization;

internal static class LocalizationBehavior
{
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, LocalizedValue>> Values = new();
    private static readonly ConditionalWeakTable<object, object> ObservedColumnCollections = new();
    private static readonly ConditionalWeakTable<DependencyObject, DeferredLocalization> DeferredLocalizations = new();
    private static readonly ConditionalWeakTable<Window, object> InitializedWindows = new();
    private static DispatcherOperation? pendingRelocalization;
    private static long applyCount;
    private static long fullTreePassCount;
    private static long visitedObjectCount;
    private static long languageChangeCount;
    private static bool initialized;

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
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(TabControl),
            Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler(OnTabSelectionChanged),
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

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window
            && ReferenceEquals(e.OriginalSource, window)
            && !InitializedWindows.TryGetValue(window, out _))
        {
            InitializedWindows.Add(window, new object());
            QueueTreeLocalization(window, isWholeWindow: true);
        }
    }

    private static void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is TabControl tabs
            && ReferenceEquals(e.OriginalSource, tabs)
            && tabs.IsLoaded)
        {
            QueueTreeLocalization(tabs, isWholeWindow: false);
        }
    }

    private static void QueueTreeLocalization(DependencyObject root, bool isWholeWindow)
    {
        var dispatcher = root.Dispatcher;
        var state = DeferredLocalizations.GetOrCreateValue(root);
        if (state.Operation is { Status: DispatcherOperationStatus.Pending })
        {
            return;
        }

        state.Operation = dispatcher.BeginInvoke(
            () =>
            {
                state.Operation = null;
                if (!isWholeWindow
                    && pendingRelocalization is { Status: DispatcherOperationStatus.Pending })
                {
                    return;
                }

                if (root is Window window)
                {
                    LocalizeWindow(window);
                }
                else
                {
                    LocalizeTree(root);
                }
            },
            DispatcherPriority.DataBind);
    }

    private static void LocalizeElement(FrameworkElement element)
    {
        Watch(element, FrameworkElement.ToolTipProperty);
        Watch(element, AutomationProperties.NameProperty);
        Watch(element, AutomationProperties.HelpTextProperty);

        if (element is TextBlock textBlock)
        {
            Watch(textBlock, TextBlock.TextProperty);
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
        var current = target.GetValue(property) as string;
        if (current is null)
        {
            return;
        }

        var values = Values.GetOrCreateValue(target);
        if (!values.TryGetValue(property, out var localizedValue))
        {
            localizedValue = new LocalizedValue(LocalizationService.Current.ResolveSource(current));
            values[property] = localizedValue;
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, target.GetType());
            if (descriptor is not null)
            {
                var weakTarget = new WeakReference<DependencyObject>(target);
                descriptor.AddValueChanged(target, (_, _) =>
                {
                    if (weakTarget.TryGetTarget(out var liveTarget))
                    {
                        OnValueChanged(liveTarget, property, localizedValue);
                    }
                });
            }
        }

        Apply(target, property, localizedValue);
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

    private static void LocalizeTree(DependencyObject root)
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
            if (current is Run run)
            {
                Watch(run, Run.TextProperty);
            }
            if (current is TextBlock textBlock)
            {
                foreach (var inline in textBlock.Inlines)
                {
                    pending.Enqueue(inline);
                }
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
