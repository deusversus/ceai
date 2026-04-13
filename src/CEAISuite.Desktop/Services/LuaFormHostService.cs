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
    private readonly Dispatcher _dispatcher;

    public event Action<string, string>? ElementClicked;
    public event Action<string, string>? TimerFired;
    public event Action<string, string, string>? ElementTextChanged;

    public LuaFormHostService()
    {
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public void ShowForm(LuaFormDescriptor form)
    {
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
        });
    }

    public void UpdateElement(string formId, LuaFormElement element)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_windows.TryGetValue(formId, out var window)) return;
            if (window.Content is not Canvas canvas) return;

            foreach (UIElement child in canvas.Children)
            {
                if (child is not FrameworkElement fe || fe.Tag is not string id || id != element.Id)
                    continue;

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
                }

                // Apply common styling
                fe.Visibility = element.Visible ? Visibility.Visible : Visibility.Collapsed;
                fe.IsEnabled = element.Enabled;
                if (element.FontName is not null || element.FontSize is not null)
                {
                    if (element.FontName is not null && fe is Control ctrl)
                        ctrl.FontFamily = new FontFamily(element.FontName);
                    if (element.FontSize is not null && fe is Control ctrl2)
                        ctrl2.FontSize = element.FontSize.Value;
                }
                if (element.FontColor is not null && fe is Control fCtrl)
                {
                    try { fCtrl.Foreground = new BrushConverter().ConvertFromString(element.FontColor) as Brush ?? fCtrl.Foreground; }
                    catch { /* invalid color; ignore */ }
                }
                if (element.BackColor is not null && fe is Control bCtrl)
                {
                    try { bCtrl.Background = new BrushConverter().ConvertFromString(element.BackColor) as Brush ?? bCtrl.Background; }
                    catch { /* invalid color; ignore */ }
                }

                break;
            }
        });
    }

    // ── Timer Lifecycle ──

    public void StartTimer(string formId, string timerId, int intervalMs)
    {
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
        return _dispatcher.Invoke(() =>
        {
            var fe = FindElement(formId, elementId);
            return fe is TextBox tb ? tb.Text : null;
        });
    }

    public bool? GetElementChecked(string formId, string elementId)
    {
        return _dispatcher.Invoke(() =>
        {
            var fe = FindElement(formId, elementId);
            return fe is CheckBox chk ? chk.IsChecked : null;
        });
    }

    public int? GetSelectedIndex(string formId, string elementId)
    {
        return _dispatcher.Invoke(() =>
        {
            var fe = FindElement(formId, elementId);
            if (fe is ListBox lb) return lb.SelectedIndex;
            if (fe is ComboBox cb) return cb.SelectedIndex;
            return (int?)null;
        });
    }

    public int? GetTrackBarPosition(string formId, string elementId)
    {
        return _dispatcher.Invoke(() =>
        {
            var fe = FindElement(formId, elementId);
            return fe is Slider sl ? (int?)sl.Value : null;
        });
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

    // ── Helpers ──

    private Canvas BuildCanvas(LuaFormDescriptor form)
    {
        var canvas = new Canvas();

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
                LuaPanelElement => new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) },
                LuaGroupBoxElement grp => new GroupBox { Header = grp.Caption ?? "Group" },
                LuaTimerElement => null, // Timers are non-visual; started separately
                _ => null
            };

            if (control is null) continue;

            control.Tag = element.Id;
            Canvas.SetLeft(control, element.X);
            Canvas.SetTop(control, element.Y);
            control.Width = element.Width;
            control.Height = element.Height;
            canvas.Children.Add(control);
        }

        return canvas;
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
        cb.SelectionChanged += (_, _) => ElementClicked?.Invoke(formId, element.Id);
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

        foreach (UIElement child in canvas.Children)
        {
            if (child is FrameworkElement fe && fe.Tag is string id && id == elementId)
                return fe;
        }
        return null;
    }
}
