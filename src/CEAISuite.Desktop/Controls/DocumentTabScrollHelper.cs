using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace CEAISuite.Desktop.Controls;

/// <summary>
/// Injects scroll buttons + a dropdown document list into AvalonDock tab strips
/// that would otherwise silently hide overflow tabs.
///
/// Usage: call <see cref="Attach"/> on the DockingManager after theme + layout load.
/// </summary>
public static class DocumentTabScrollHelper
{
    private static readonly HashSet<int> _processed = new();

    /// <summary>
    /// Hook into the DockingManager so that every document/anchorable pane
    /// gets scroll arrows + dropdown when its tab strip overflows.
    /// </summary>
    public static void Attach(AvalonDock.DockingManager dockManager)
    {
        _processed.Clear();

        // Process existing panes
        dockManager.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScanAndPatch(dockManager));

        // Re-process after layout changes (tabs dragged, panels docked/undocked)
        dockManager.LayoutChanged += (_, _) =>
            dockManager.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScanAndPatch(dockManager));
        dockManager.LayoutUpdated += (_, _) =>
            dockManager.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScanAndPatch(dockManager));
    }

    private static void ScanAndPatch(DependencyObject root)
    {
        // Find all DocumentPaneTabPanel and AnchorablePaneTabPanel instances
        foreach (var tabPanel in FindAll<Panel>(root, p => p is DocumentPaneTabPanel or AnchorablePaneTabPanel))
        {
            var hash = tabPanel.GetHashCode();
            if (_processed.Contains(hash)) continue;

            if (TryWrap(tabPanel))
                _processed.Add(hash);
        }
    }

    private static bool TryWrap(Panel tabPanel)
    {
        // Already wrapped?
        var ancestor = VisualTreeHelper.GetParent(tabPanel);
        while (ancestor is not null)
        {
            if (ancestor is DockPanel dp && dp.Tag is "TabScrollWrapper") return true;
            if (ancestor is LayoutDocumentPaneControl or LayoutAnchorablePaneControl) break;
            ancestor = VisualTreeHelper.GetParent(ancestor);
        }

        // Get the pane control that owns this tab panel
        var paneCtrl = FindAncestor<FrameworkElement>(tabPanel,
            fe => fe is LayoutDocumentPaneControl or LayoutAnchorablePaneControl);
        if (paneCtrl is null) return false;

        // Find the immediate parent of the tab panel
        var parent = VisualTreeHelper.GetParent(tabPanel);
        if (parent is null) return false;

        // Determine the layout model for the dropdown
        ILayoutContainer? layoutModel = paneCtrl switch
        {
            LayoutDocumentPaneControl docCtrl => docCtrl.Model as ILayoutContainer,
            LayoutAnchorablePaneControl ancCtrl => ancCtrl.Model as ILayoutContainer,
            _ => null
        };

        // Remove the tab panel from its parent — handle different container types
        switch (parent)
        {
            case Panel parentPanel:
            {
                var idx = parentPanel.Children.IndexOf(tabPanel);
                if (idx < 0) return false;
                parentPanel.Children.RemoveAt(idx);
                var wrapper = BuildWrapper(tabPanel, paneCtrl, layoutModel);
                parentPanel.Children.Insert(idx, wrapper);
                return true;
            }
            case Border parentBorder:
            {
                parentBorder.Child = null;
                var wrapper = BuildWrapper(tabPanel, paneCtrl, layoutModel);
                parentBorder.Child = wrapper;
                return true;
            }
            case ContentControl parentCC:
            {
                parentCC.Content = null;
                var wrapper = BuildWrapper(tabPanel, paneCtrl, layoutModel);
                parentCC.Content = wrapper;
                return true;
            }
            case Decorator parentDec:
            {
                parentDec.Child = null;
                var wrapper = BuildWrapper(tabPanel, paneCtrl, layoutModel);
                parentDec.Child = wrapper;
                return true;
            }
            default:
                return false;
        }
    }

    private static DockPanel BuildWrapper(Panel tabPanel, FrameworkElement paneCtrl, ILayoutContainer? layoutModel)
    {
        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false,
            Content = tabPanel
        };

        var leftBtn = CreateScrollButton("◀", -80, scrollViewer);
        var rightBtn = CreateScrollButton("▶", 80, scrollViewer);
        var dropdownBtn = CreateDropdownButton(layoutModel);

        var wrapper = new DockPanel
        {
            LastChildFill = true,
            Tag = "TabScrollWrapper"
        };

        DockPanel.SetDock(leftBtn, Dock.Left);
        DockPanel.SetDock(dropdownBtn, Dock.Right);
        DockPanel.SetDock(rightBtn, Dock.Right);
        wrapper.Children.Add(leftBtn);
        wrapper.Children.Add(dropdownBtn);
        wrapper.Children.Add(rightBtn);
        wrapper.Children.Add(scrollViewer);

        // Mouse wheel scrolling on the pane control header area
        paneCtrl.PreviewMouseWheel -= OnMouseWheel;
        paneCtrl.PreviewMouseWheel += OnMouseWheel;

        return wrapper;
    }

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        // Only scroll if the mouse is over the tab strip area (top ~28px)
        var pos = e.GetPosition(fe);
        if (pos.Y > 30) return;

        var sv = FindFirst<ScrollViewer>(fe, s => s.Tag is null); // not any nested content ScrollViewer
        if (sv is null) return;

        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta * 0.5);
        e.Handled = true;
    }

    private static RepeatButton CreateScrollButton(string content, double scrollDelta, ScrollViewer sv)
    {
        var btn = new RepeatButton
        {
            Content = content,
            Width = 18,
            FontSize = 9,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Focusable = false,
            Interval = 50,
            Delay = 200
        };
        btn.SetResourceReference(Control.BackgroundProperty, "MenuBarBackground");
        btn.SetResourceReference(Control.ForegroundProperty, "SecondaryForeground");
        btn.Click += (_, _) => sv.ScrollToHorizontalOffset(sv.HorizontalOffset + scrollDelta);
        return btn;
    }

    private static ToggleButton CreateDropdownButton(ILayoutContainer? layoutModel)
    {
        var btn = new ToggleButton
        {
            Content = "▾",
            Width = 20,
            FontSize = 12,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Focusable = false,
            ToolTip = "Show all tabs"
        };
        btn.SetResourceReference(Control.BackgroundProperty, "MenuBarBackground");
        btn.SetResourceReference(Control.ForegroundProperty, "SecondaryForeground");

        var contextMenu = new ContextMenu();
        contextMenu.SetResourceReference(Control.BackgroundProperty, "MenuBarBackground");
        contextMenu.SetResourceReference(Control.ForegroundProperty, "PrimaryForeground");
        contextMenu.Closed += (_, _) => btn.IsChecked = false;

        btn.Click += (_, _) =>
        {
            contextMenu.Items.Clear();
            if (layoutModel is null) return;

            foreach (var child in layoutModel.Children)
            {
                var title = child switch
                {
                    LayoutDocument doc => doc.Title,
                    LayoutAnchorable anc => anc.Title,
                    _ => null
                };
                if (title is null) continue;

                var isSelected = child switch
                {
                    LayoutDocument doc => doc.IsSelected,
                    LayoutAnchorable anc => anc.IsSelected,
                    _ => false
                };

                var item = new MenuItem { Header = title, Tag = child };
                item.SetResourceReference(Control.ForegroundProperty, "PrimaryForeground");
                if (isSelected) item.FontWeight = FontWeights.Bold;

                item.Click += (s, _) =>
                {
                    switch (((MenuItem)s).Tag)
                    {
                        case LayoutDocument doc: doc.IsActive = true; break;
                        case LayoutAnchorable anc: anc.IsActive = true; break;
                    }
                };
                contextMenu.Items.Add(item);
            }

            contextMenu.PlacementTarget = btn;
            contextMenu.Placement = PlacementMode.Bottom;
            contextMenu.IsOpen = true;
        };

        return btn;
    }

    // ── Visual tree helpers ──

    private static T? FindAncestor<T>(DependencyObject child, Func<T, bool> predicate) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is T typed && predicate(typed)) return typed;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindFirst<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && predicate(typed)) return typed;
            var found = FindFirst(child, predicate);
            if (found is not null) return found;
        }
        return null;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && predicate(typed))
                yield return typed;
            foreach (var found in FindAll(child, predicate))
                yield return found;
        }
    }
}
