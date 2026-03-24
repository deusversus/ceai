using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace CEAISuite.Desktop.Controls;

/// <summary>
/// Attached behavior that adds scroll-left / scroll-right buttons and a dropdown
/// document list to AvalonDock's LayoutDocumentPaneControl when tabs overflow.
/// Applied via an implicit Style in SharedStyles.xaml.
/// </summary>
public static class DocumentTabScrollHelper
{
    public static readonly DependencyProperty EnableScrollProperty =
        DependencyProperty.RegisterAttached(
            "EnableScroll", typeof(bool), typeof(DocumentTabScrollHelper),
            new PropertyMetadata(false, OnEnableScrollChanged));

    public static bool GetEnableScroll(DependencyObject obj) => (bool)obj.GetValue(EnableScrollProperty);
    public static void SetEnableScroll(DependencyObject obj, bool value) => obj.SetValue(EnableScrollProperty, value);

    private static void OnEnableScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement ctrl) return;
        if (d is not LayoutDocumentPaneControl and not LayoutAnchorablePaneControl) return;

        if ((bool)e.NewValue)
        {
            ctrl.Loaded += OnPaneLoaded;
            ctrl.PreviewMouseWheel += OnMouseWheel;
        }
        else
        {
            ctrl.Loaded -= OnPaneLoaded;
            ctrl.PreviewMouseWheel -= OnMouseWheel;
        }
    }

    private static void OnPaneLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement paneCtrl) return;

        // Find the tab panel inside the control (DocumentPaneTabPanel or AnchorablePaneTabPanel)
        var tabPanel = FindChild<Panel>(paneCtrl, p =>
            p is DocumentPaneTabPanel or AnchorablePaneTabPanel);
        if (tabPanel is null) return;

        // Check if we already wrapped it
        if (tabPanel.Parent is ScrollViewer) return;

        // Get the existing parent (usually a Panel or Border)
        var parent = VisualTreeHelper.GetParent(tabPanel) as Panel;
        if (parent is null) return;

        var idx = parent.Children.IndexOf(tabPanel);
        parent.Children.Remove(tabPanel);

        // Create the scroll wrapper
        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = true,
            Content = tabPanel
        };

        // Scroll left button
        var leftBtn = CreateScrollButton("◀", -60, scrollViewer);

        // Scroll right button
        var rightBtn = CreateScrollButton("▶", 60, scrollViewer);

        // Dropdown button — get the layout model
        ILayoutContainer? layoutModel = paneCtrl switch
        {
            LayoutDocumentPaneControl docCtrl => docCtrl.Model as ILayoutContainer,
            LayoutAnchorablePaneControl ancCtrl => ancCtrl.Model as ILayoutContainer,
            _ => null
        };
        var dropdownBtn = CreateDropdownButton(layoutModel);

        // Assemble: [◀] [ScrollViewer with tabs] [▶] [▼]
        var wrapper = new DockPanel
        {
            LastChildFill = true,
            Tag = "TabScrollWrapper"
        };

        DockPanel.SetDock(leftBtn, Dock.Left);
        DockPanel.SetDock(rightBtn, Dock.Right);
        DockPanel.SetDock(dropdownBtn, Dock.Right);
        wrapper.Children.Add(leftBtn);
        wrapper.Children.Add(dropdownBtn);
        wrapper.Children.Add(rightBtn);
        wrapper.Children.Add(scrollViewer);

        parent.Children.Insert(idx, wrapper);
    }

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement paneCtrl) return;
        var sv = FindChild<ScrollViewer>(paneCtrl, _ => true);
        if (sv is null) return;

        // Scroll tabs horizontally on mouse wheel
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

    private static T? FindChild<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && predicate(typed)) return typed;
            var found = FindChild(child, predicate);
            if (found is not null) return found;
        }
        return null;
    }
}
