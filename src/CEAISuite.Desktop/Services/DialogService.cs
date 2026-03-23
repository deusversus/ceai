using System.Windows;
using Microsoft.Win32;

namespace CEAISuite.Desktop.Services;

public sealed class DialogService : IDialogService
{
    public string? ShowInput(string title, string prompt, string defaultValue = "")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = System.Windows.Application.Current.MainWindow,
            ResizeMode = ResizeMode.NoResize
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
        var textBox = new System.Windows.Controls.TextBox { Text = defaultValue };
        panel.Children.Add(textBox);

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var okButton = new System.Windows.Controls.Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancelButton = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, IsCancel = true };
        okButton.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;
        textBox.SelectAll();
        textBox.Focus();

        return dialog.ShowDialog() == true ? textBox.Text : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public string? ShowSaveFileDialog(string filter, string defaultName = "")
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = defaultName };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ShowOpenFileDialog(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
