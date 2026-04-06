using System.Windows;
using System.Windows.Controls;
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

                // Update content
                if (fe is Button btn) btn.Content = element.Caption ?? "";
                else if (fe is TextBlock lbl) lbl.Text = element.Caption ?? "";
                else if (fe is TextBox tb && element is LuaEditElement edit) tb.Text = edit.Text ?? "";
                else if (fe is CheckBox chk)
                {
                    chk.Content = element.Caption ?? "";
                    if (element is LuaCheckBoxElement chkElem) chk.IsChecked = chkElem.IsChecked;
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
