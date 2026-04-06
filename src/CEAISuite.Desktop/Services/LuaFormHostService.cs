using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Desktop.Services;

/// <summary>
/// WPF implementation of <see cref="ILuaFormHost"/>. Renders Lua form descriptors
/// as WPF Windows with Canvas layout. Click events propagate back through the interface.
/// </summary>
public sealed class LuaFormHostService : ILuaFormHost
{
    private readonly Dictionary<string, Window> _windows = new();
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

            window.Closed += (_, _) => _windows.Remove(form.Id);
            _windows[form.Id] = window;
            window.Show();
        });
    }

    public void CloseForm(string formId)
    {
        _dispatcher.BeginInvoke(() =>
        {
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
            if (!_windows.TryGetValue(formId, out var window))
                return;

            if (window.Content is not Canvas canvas)
                return;

            foreach (UIElement child in canvas.Children)
            {
                if (child is FrameworkElement fe && fe.Tag is string id && id == element.Id)
                {
                    if (fe is Button btn && element.Caption is not null)
                        btn.Content = element.Caption;
                    else if (fe is TextBlock lbl && element.Caption is not null)
                        lbl.Text = element.Caption;
                    else if (fe is CheckBox chk && element.Caption is not null)
                        chk.Content = element.Caption;
                    break;
                }
            }
        });
    }

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
}
