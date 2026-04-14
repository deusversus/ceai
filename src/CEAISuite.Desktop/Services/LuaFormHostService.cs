using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Desktop.Services;

/// <summary>
/// WPF implementation of <see cref="ILuaFormHost"/>. Renders Lua form descriptors
/// as WPF Windows with Canvas layout. Click/timer events propagate back through the interface.
/// </summary>
public sealed class LuaFormHostService : ILuaFormHost
{
    private readonly Dictionary<string, Window> _windows = new();
    private readonly Dictionary<string, DispatcherTimer> _timers = new();
    private readonly Dictionary<string, Canvas> _canvases = new();
    private readonly Dispatcher _dispatcher;

    /// <summary>Re-entrancy guard: suppresses ElementChanged events during programmatic UpdateElement calls.</summary>
    private bool _isUpdatingFromScript;

    public event Action<string, string>? ElementClicked;
    public event Action<string, string>? TimerFired;
    public event Action<string, string, string>? ElementTextChanged;
    public event Action<string, string, string>? ElementChanged;

    public LuaFormHostService()
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public void ShowForm(LuaFormDescriptor form)
    {
        if (_windows.Count >= 50)
            throw new InvalidOperationException("Maximum form limit (50) reached");

        _dispatcher.BeginInvoke(() =>
        {
            if (_windows.TryGetValue(form.Id, out var existing))
            {
                existing.Activate();
                return;
            }

            var window = new Window
            {
                Title = form.Title,
                Width = form.Width,
                Height = form.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = BuildCanvas(form)
            };

            window.Closed += (_, _) =>
            {
                _windows.Remove(form.Id);
                StopAllTimersForForm(form.Id);
            };
            _windows[form.Id] = window;
            window.Show();
        });
    }

    public void CloseForm(string formId)
    {
        _dispatcher.BeginInvoke(() =>
        {
            StopAllTimersForForm(formId);
            if (_windows.TryGetValue(formId, out var window))
            {
                window.Close();
                _windows.Remove(formId);
            }
            _canvases.Remove(formId);
        });
    }

    public void CloseAllForms()
    {
        // Window.Close() and DispatcherTimer.Stop() must be called on the UI thread.
        // Use synchronous Invoke (not BeginInvoke) so callers like Reset/Dispose
        // can rely on forms being closed when this method returns.
        _dispatcher.Invoke(() =>
        {
            foreach (var (_, window) in _windows.ToList())
            {
                try { window.Close(); }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
            }
            _windows.Clear();
            foreach (var (_, timer) in _timers.ToList())
            {
                timer.Stop();
            }
            _timers.Clear();
            _canvases.Clear();
        });
    }

    public void UpdateElement(string formId, LuaFormElement element)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_windows.TryGetValue(formId, out var window)) return;
            if (window.Content is not Canvas canvas) return;

            // Suppress ElementChanged events during programmatic updates to prevent re-entrancy
            _isUpdatingFromScript = true;
            try
            {
                UpdateElementCore(canvas, formId, element);
            }
            finally
            {
                _isUpdatingFromScript = false;
            }
        });
    }

    private void UpdateElementCore(Canvas canvas, string formId, LuaFormElement element)
    {
        // Also search inside container controls (GroupBox, Panel) for parented elements
        var fe = FindElementInCanvas(canvas, element.Id);
        if (fe is null) return;

        // Update position
        Canvas.SetLeft(fe, element.X);
        Canvas.SetTop(fe, element.Y);
        fe.Width = element.Width;
        fe.Height = element.Height;

        // Update content by type
        switch (fe)
        {
            case Button btn:
                btn.Content = element.Caption ?? "";
                break;
            case TextBlock lbl:
                lbl.Text = element.Caption ?? "";
                break;
            case TextBox tb when element is LuaEditElement edit:
                tb.Text = edit.Text ?? "";
                break;
            case TextBox tb when element is LuaMemoElement memo:
                tb.Text = memo.Text ?? "";
                tb.IsReadOnly = memo.ReadOnly;
                break;
            case CheckBox chk when element is LuaCheckBoxElement chkElem:
                chk.Content = element.Caption ?? "";
                chk.IsChecked = chkElem.IsChecked;
                break;
            case ListBox lb when element is LuaListBoxElement lstElem:
                lb.Items.Clear();
                foreach (var item in lstElem.Items) lb.Items.Add(item);
                break;
            case ComboBox cb when element is LuaComboBoxElement cmbElem:
                cb.Items.Clear();
                foreach (var item in cmbElem.Items) cb.Items.Add(item);
                if (cmbElem.SelectedIndex >= 0) cb.SelectedIndex = cmbElem.SelectedIndex;
                break;
            case Slider sl when element is LuaTrackBarElement trkElem:
                sl.Minimum = trkElem.Min;
                sl.Maximum = trkElem.Max;
                sl.Value = trkElem.Position;
                break;
            case ProgressBar pb when element is LuaProgressBarElement prgElem:
                pb.Minimum = prgElem.Min;
                pb.Maximum = prgElem.Max;
                pb.Value = prgElem.Position;
                break;
            case Image img when element is LuaImageElement imgElem && imgElem.ImagePath is not null:
                try { img.Source = new BitmapImage(new Uri(imgElem.ImagePath, UriKind.RelativeOrAbsolute)); }
                catch { /* invalid path; ignore */ }
                break;
            case GroupBox gb:
                gb.Header = element.Caption ?? "";
                break;
            case StackPanel sp when element is LuaRadioGroupElement rdgElem:
                sp.Children.Clear();
                for (int idx = 0; idx < rdgElem.Items.Count; idx++)
                {
                    var rb = new RadioButton
                    {
                        Content = rdgElem.Items[idx],
                        IsChecked = idx == rdgElem.SelectedIndex,
                        GroupName = element.Id
                    };
                    rb.Checked += (_, _) => ElementClicked?.Invoke(formId, element.Id);
                    sp.Children.Add(rb);
                }
                break;
            case TabControl tc when element is LuaTabControlElement tabElem:
                while (tc.Items.Count < tabElem.TabNames.Count)
                    tc.Items.Add(new TabItem { Header = tabElem.TabNames[tc.Items.Count] });
                if (tabElem.SelectedIndex >= 0 && tabElem.SelectedIndex < tc.Items.Count)
                    tc.SelectedIndex = tabElem.SelectedIndex;
                break;
        }

        // Apply common styling
        fe.Visibility = element.Visible ? Visibility.Visible : Visibility.Collapsed;
        fe.IsEnabled = element.Enabled;

        // Font — applies to both Control (buttons, textboxes) and TextBlock (labels)
        if (element.FontName is not null)
        {
            var family = new FontFamily(element.FontName);
            if (fe is Control c1) c1.FontFamily = family;
            else if (fe is TextBlock tb1) tb1.FontFamily = family;
        }
        if (element.FontSize is not null)
        {
            if (fe is Control c2) c2.FontSize = element.FontSize.Value;
            else if (fe is TextBlock tb2) tb2.FontSize = element.FontSize.Value;
        }

        // Foreground color
        if (element.FontColor is not null)
        {
            try
            {
                var brush = new BrushConverter().ConvertFromString(element.FontColor) as Brush;
                if (brush is not null)
                {
                    if (fe is Control fCtrl) fCtrl.Foreground = brush;
                    else if (fe is TextBlock fTb) fTb.Foreground = brush;
                }
            }
            catch { /* invalid color; ignore */ }
        }

        // Background color (Control only — TextBlock has no Background)
        if (element.BackColor is not null && fe is Control bCtrl)
        {
            try { bCtrl.Background = new BrushConverter().ConvertFromString(element.BackColor) as Brush ?? bCtrl.Background; }
            catch { /* invalid color; ignore */ }
        }
    }

    // ── S5: Form-Level Property Access ──

    public void BringToFront(string formId)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_windows.TryGetValue(formId, out var window)) return;
            window.Activate();
            // Standard WPF bring-to-front pattern
            window.Topmost = true;
            window.Topmost = false;
        });
    }

    public void SetFormProperty(string formId, string property, object? value)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_windows.TryGetValue(formId, out var window)) return;
            switch (property.ToLowerInvariant())
            {
                case "caption" or "title":
                    window.Title = value?.ToString() ?? "";
                    break;
                case "width":
                    if (value is double or int) window.Width = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "height":
                    if (value is double or int) window.Height = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "left":
                    if (value is double or int)
                    {
                        window.WindowStartupLocation = WindowStartupLocation.Manual;
                        window.Left = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    break;
                case "top":
                    if (value is double or int)
                    {
                        window.WindowStartupLocation = WindowStartupLocation.Manual;
                        window.Top = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    break;
                case "visible":
                    if (value is true) window.Show();
                    else if (value is false) window.Hide();
                    break;
                case "color" or "backcolor":
                    if (value is string colorStr)
                    {
                        try { window.Background = new BrushConverter().ConvertFromString(colorStr) as Brush ?? window.Background; }
                        catch { /* invalid color */ }
                    }
                    break;
            }
        });
    }

    public object? GetFormProperty(string formId, string property)
    {
        // If already on the UI thread (e.g. called from a Lua callback dispatched on the UI thread),
        // execute directly to avoid deadlock from synchronous Dispatcher.Invoke.
        object? ReadProperty()
        {
            if (!_windows.TryGetValue(formId, out var window)) return null;
            return property.ToLowerInvariant() switch
            {
                "caption" or "title" => (object?)window.Title,
                "width" => window.Width,
                "height" => window.Height,
                "left" => window.Left,
                "top" => window.Top,
                "visible" => window.IsVisible,
                _ => null
            };
        }

        return _dispatcher.CheckAccess() ? ReadProperty() : _dispatcher.Invoke(ReadProperty);
    }

    public void SetFormTopMost(string formId, bool topMost)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_windows.TryGetValue(formId, out var window))
                window.Topmost = topMost;
        });
    }

    // ── S5: Element Search (supports nested containers) ──

    /// <summary>Find an element by its Tag ID, searching both the root canvas and nested containers.</summary>
    private static FrameworkElement? FindElementInCanvas(Canvas canvas, string elementId)
    {
        foreach (UIElement child in canvas.Children)
        {
            if (child is FrameworkElement fe && fe.Tag is string id && id == elementId)
                return fe;

            // Search inside GroupBox content (Canvas child)
            if (child is GroupBox gb && gb.Content is Canvas groupCanvas)
            {
                var found = FindElementInCanvas(groupCanvas, elementId);
                if (found is not null) return found;
            }

            // Search inside Panel/Border content (Canvas child)
            if (child is Border border && border.Child is Canvas panelCanvas)
            {
                var found = FindElementInCanvas(panelCanvas, elementId);
                if (found is not null) return found;
            }
        }
        return null;
    }

    // ── Timer Lifecycle ──

    public void StartTimer(string formId, string timerId, int intervalMs)
    {
        if (_timers.Count >= 100)
            throw new InvalidOperationException("Maximum timer limit (100) reached");

        _dispatcher.BeginInvoke(() =>
        {
            var key = $"{formId}:{timerId}";
            StopTimerCore(key);

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(intervalMs, 10))
            };
            timer.Tick += (_, _) => TimerFired?.Invoke(formId, timerId);
            _timers[key] = timer;
            timer.Start();
        });
    }

    public void StopTimer(string formId, string timerId)
    {
        _dispatcher.BeginInvoke(() => StopTimerCore($"{formId}:{timerId}"));
    }

    private void StopTimerCore(string key)
    {
        if (_timers.TryGetValue(key, out var existing))
        {
            existing.Stop();
            _timers.Remove(key);
        }
    }

    private void StopAllTimersForForm(string formId)
    {
        var prefix = $"{formId}:";
        var keys = _timers.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in keys)
            StopTimerCore(key);
    }

    // ── Element Value Accessors ──

    public string? GetElementText(string formId, string elementId)
    {
        string? Read() { var fe = FindElement(formId, elementId); return fe is TextBox tb ? tb.Text : null; }
        return _dispatcher.CheckAccess() ? Read() : _dispatcher.Invoke(Read);
    }

    public bool? GetElementChecked(string formId, string elementId)
    {
        bool? Read() { var fe = FindElement(formId, elementId); return fe is CheckBox chk ? chk.IsChecked : null; }
        return _dispatcher.CheckAccess() ? Read() : _dispatcher.Invoke(Read);
    }

    public int? GetSelectedIndex(string formId, string elementId)
    {
        int? Read()
        {
            var fe = FindElement(formId, elementId);
            if (fe is ListBox lb) return lb.SelectedIndex;
            if (fe is ComboBox cb) return cb.SelectedIndex;
            return null;
        }
        return _dispatcher.CheckAccess() ? Read() : _dispatcher.Invoke(Read);
    }

    public int? GetTrackBarPosition(string formId, string elementId)
    {
        int? Read() { var fe = FindElement(formId, elementId); return fe is Slider sl ? (int?)sl.Value : null; }
        return _dispatcher.CheckAccess() ? Read() : _dispatcher.Invoke(Read);
    }

    // ── Dialog Functions ──

    public void ShowMessageDialog(string text, string title)
    {
        _dispatcher.Invoke(() =>
            MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Information));
    }

    public string? ShowInputDialog(string title, string prompt, string defaultValue)
    {
        return _dispatcher.Invoke(() =>
        {
            var dialog = new Window
            {
                Title = title,
                Width = 350,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
            var textBox = new TextBox { Text = defaultValue, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(textBox);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "OK", Width = 75, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelBtn = new Button { Content = "Cancel", Width = 75, IsCancel = true };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            string? result = null;
            okBtn.Click += (_, _) => { result = textBox.Text; dialog.DialogResult = true; };
            dialog.Content = panel;

            return dialog.ShowDialog() == true ? result : null;
        });
    }

    // ── Canvas Drawing ──

    public void DrawLine(string formId, int x1, int y1, int x2, int y2, string color, int width)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_canvases.TryGetValue(formId, out var canvas)) return;
            var line = new System.Windows.Shapes.Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = ParseBrush(color),
                StrokeThickness = width
            };
            canvas.Children.Add(line);
        });
    }

    public void DrawRect(string formId, int x1, int y1, int x2, int y2, string color, bool fill)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_canvases.TryGetValue(formId, out var canvas)) return;
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Abs(x2 - x1),
                Height = Math.Abs(y2 - y1)
            };
            var brush = ParseBrush(color);
            if (fill) rect.Fill = brush;
            else { rect.Stroke = brush; rect.StrokeThickness = 1; }
            Canvas.SetLeft(rect, Math.Min(x1, x2));
            Canvas.SetTop(rect, Math.Min(y1, y2));
            canvas.Children.Add(rect);
        });
    }

    public void DrawEllipse(string formId, int x1, int y1, int x2, int y2, string color, bool fill)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_canvases.TryGetValue(formId, out var canvas)) return;
            var ellipse = new System.Windows.Shapes.Ellipse
            {
                Width = Math.Abs(x2 - x1),
                Height = Math.Abs(y2 - y1)
            };
            var brush = ParseBrush(color);
            if (fill) ellipse.Fill = brush;
            else { ellipse.Stroke = brush; ellipse.StrokeThickness = 1; }
            Canvas.SetLeft(ellipse, Math.Min(x1, x2));
            Canvas.SetTop(ellipse, Math.Min(y1, y2));
            canvas.Children.Add(ellipse);
        });
    }

    public void DrawText(string formId, int x, int y, string text, string color, string? fontName, int? fontSize)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_canvases.TryGetValue(formId, out var canvas)) return;
            var tb = new TextBlock
            {
                Text = text,
                Foreground = ParseBrush(color)
            };
            if (fontName is not null) tb.FontFamily = new FontFamily(fontName);
            if (fontSize is not null) tb.FontSize = fontSize.Value;
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            canvas.Children.Add(tb);
        });
    }

    public void ClearCanvas(string formId)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_canvases.TryGetValue(formId, out var canvas)) return;
            // Remove only drawing shapes (no Tag), keep form elements (they have Tags)
            var toRemove = canvas.Children.OfType<UIElement>()
                .Where(e => (e is System.Windows.Shapes.Shape || e is TextBlock) && (e as FrameworkElement)?.Tag is null)
                .ToList();
            foreach (var item in toRemove)
                canvas.Children.Remove(item);
        });
    }

    private static Brush ParseBrush(string color)
    {
        try { return new BrushConverter().ConvertFromString(color) as Brush ?? Brushes.Black; }
        catch { return Brushes.Black; }
    }

    // ── Helpers ──

    private Canvas BuildCanvas(LuaFormDescriptor form)
    {
        var canvas = new Canvas();
        _canvases[form.Id] = canvas;

        // Two-pass rendering for element parenting:
        // Pass 1: Create all controls and register them by element ID
        var controlMap = new Dictionary<string, FrameworkElement>();

        foreach (var element in form.Elements)
        {
            FrameworkElement? control = element switch
            {
                LuaButtonElement btn => CreateButton(form.Id, btn),
                LuaLabelElement lbl => CreateLabel(lbl),
                LuaEditElement edit => CreateEdit(form.Id, edit),
                LuaCheckBoxElement chk => CreateCheckBox(form.Id, chk),
                LuaMemoElement memo => CreateMemo(form.Id, memo),
                LuaListBoxElement lst => CreateListBox(form.Id, lst),
                LuaComboBoxElement cmb => CreateComboBox(form.Id, cmb),
                LuaTrackBarElement trk => CreateTrackBar(form.Id, trk),
                LuaProgressBarElement prg => CreateProgressBar(prg),
                LuaImageElement img => CreateImage(img),
                LuaRadioGroupElement rdg => CreateRadioGroup(form.Id, rdg),
                LuaTabControlElement tab => CreateTabControl(tab),
                LuaMenuItemElement => null,
                LuaPopupMenuElement => null,
                LuaSplitterElement spl => new GridSplitter
                {
                    Width = spl.IsVertical ? 5 : double.NaN,
                    Height = spl.IsVertical ? double.NaN : 5,
                    Background = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                },
                LuaPanelElement => new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
                LuaGroupBoxElement grp => new GroupBox { Header = grp.Caption ?? "Group" },
                LuaTimerElement => null,
                _ => null
            };

            if (control is null) continue;

            control.Tag = element.Id;
            control.Width = element.Width;
            control.Height = element.Height;
            controlMap[element.Id] = control;
        }

        // Pass 2: Place each control into its parent container or the root canvas
        foreach (var element in form.Elements)
        {
            if (!controlMap.TryGetValue(element.Id, out var control)) continue;

            if (element.ParentElementId is not null
                && controlMap.TryGetValue(element.ParentElementId, out var parentControl))
            {
                // Parent element to a container (GroupBox, Panel/Border)
                var containerCanvas = GetOrCreateContainerCanvas(parentControl);
                Canvas.SetLeft(control, element.X);
                Canvas.SetTop(control, element.Y);
                containerCanvas.Children.Add(control);
            }
            else
            {
                // Add to root canvas
                Canvas.SetLeft(control, element.X);
                Canvas.SetTop(control, element.Y);
                canvas.Children.Add(control);
            }
        }

        return canvas;
    }

    /// <summary>Get or create a Canvas inside a container control (GroupBox, Border/Panel).</summary>
    private static Canvas GetOrCreateContainerCanvas(FrameworkElement container)
    {
        switch (container)
        {
            case GroupBox gb:
                if (gb.Content is Canvas existingGb) return existingGb;
                var gbCanvas = new Canvas();
                gb.Content = gbCanvas;
                return gbCanvas;
            case Border border:
                if (border.Child is Canvas existingBorder) return existingBorder;
                var borderCanvas = new Canvas();
                border.Child = borderCanvas;
                return borderCanvas;
            default:
                // Fallback: wrap in a Canvas inside a ContentControl
                if (container is ContentControl cc)
                {
                    if (cc.Content is Canvas existingCc) return existingCc;
                    var ccCanvas = new Canvas();
                    cc.Content = ccCanvas;
                    return ccCanvas;
                }
                // Can't parent into this control type — detached canvas (children won't render)
                System.Diagnostics.Debug.WriteLine(
                    $"[LuaFormHost] Cannot parent elements into {container.GetType().Name} (Tag={container.Tag}). Children will not be visible.");
                return new Canvas();
        }
    }

    private Button CreateButton(string formId, LuaButtonElement element)
    {
        var btn = new Button { Content = element.Caption ?? "Button" };
        btn.Click += (_, _) => ElementClicked?.Invoke(formId, element.Id);
        return btn;
    }

    private static TextBlock CreateLabel(LuaLabelElement element)
    {
        return new TextBlock
        {
            Text = element.Caption ?? "",
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private TextBox CreateEdit(string formId, LuaEditElement element)
    {
        var textBox = new TextBox { Text = element.Text ?? "" };
        textBox.TextChanged += (_, _) =>
            ElementTextChanged?.Invoke(formId, element.Id, textBox.Text);
        return textBox;
    }

    private CheckBox CreateCheckBox(string formId, LuaCheckBoxElement element)
    {
        var chk = new CheckBox
        {
            Content = element.Caption ?? "CheckBox",
            IsChecked = element.IsChecked
        };
        chk.Click += (_, _) => ElementClicked?.Invoke(formId, element.Id);
        // Fire ElementChanged for OnChange callbacks (guarded against programmatic re-entrancy)
        chk.Checked += (_, _) => { if (!_isUpdatingFromScript) ElementChanged?.Invoke(formId, element.Id, "Checked"); };
        chk.Unchecked += (_, _) => { if (!_isUpdatingFromScript) ElementChanged?.Invoke(formId, element.Id, "Checked"); };
        return chk;
    }

    // ── S2: New control factories ──

    private TextBox CreateMemo(string formId, LuaMemoElement element)
    {
        var memo = new TextBox
        {
            Text = element.Text ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsReadOnly = element.ReadOnly
        };
        memo.TextChanged += (_, _) =>
            ElementTextChanged?.Invoke(formId, element.Id, memo.Text);
        return memo;
    }

    private ListBox CreateListBox(string formId, LuaListBoxElement element)
    {
        var lb = new ListBox();
        foreach (var item in element.Items) lb.Items.Add(item);
        lb.SelectionChanged += (_, _) => ElementClicked?.Invoke(formId, element.Id);
        return lb;
    }

    private ComboBox CreateComboBox(string formId, LuaComboBoxElement element)
    {
        var cb = new ComboBox();
        foreach (var item in element.Items) cb.Items.Add(item);
        if (element.SelectedIndex >= 0) cb.SelectedIndex = element.SelectedIndex;
        cb.SelectionChanged += (_, _) =>
        {
            ElementClicked?.Invoke(formId, element.Id);
            if (!_isUpdatingFromScript) ElementChanged?.Invoke(formId, element.Id, "SelectedIndex");
        };
        return cb;
    }

    private Slider CreateTrackBar(string formId, LuaTrackBarElement element)
    {
        var sl = new Slider
        {
            Minimum = element.Min,
            Maximum = element.Max,
            Value = element.Position,
            IsSnapToTickEnabled = true,
            TickFrequency = 1
        };
        sl.ValueChanged += (_, _) => ElementClicked?.Invoke(formId, element.Id);
        return sl;
    }

    private static ProgressBar CreateProgressBar(LuaProgressBarElement element)
    {
        return new ProgressBar
        {
            Minimum = element.Min,
            Maximum = element.Max,
            Value = element.Position
        };
    }

    private StackPanel CreateRadioGroup(string formId, LuaRadioGroupElement element)
    {
        var panel = new StackPanel();
        for (int i = 0; i < element.Items.Count; i++)
        {
            var rb = new RadioButton
            {
                Content = element.Items[i],
                IsChecked = i == element.SelectedIndex,
                GroupName = element.Id
            };
            rb.Checked += (_, _) => ElementClicked?.Invoke(formId, element.Id);
            panel.Children.Add(rb);
        }
        return panel;
    }

    private static TabControl CreateTabControl(LuaTabControlElement element)
    {
        var tc = new TabControl();
        foreach (var name in element.TabNames)
            tc.Items.Add(new TabItem { Header = name });
        if (element.SelectedIndex >= 0 && element.SelectedIndex < tc.Items.Count)
            tc.SelectedIndex = element.SelectedIndex;
        return tc;
    }

    private static Image CreateImage(LuaImageElement element)
    {
        var img = new Image { Stretch = Stretch.Uniform };
        if (element.ImagePath is not null)
        {
            try { img.Source = new BitmapImage(new Uri(element.ImagePath, UriKind.RelativeOrAbsolute)); }
            catch { /* invalid path; leave blank */ }
        }
        return img;
    }

    private FrameworkElement? FindElement(string formId, string elementId)
    {
        if (!_windows.TryGetValue(formId, out var window)) return null;
        if (window.Content is not Canvas canvas) return null;

        // Use FindElementInCanvas to search nested containers (GroupBox, Panel)
        return FindElementInCanvas(canvas, elementId);
    }
}
