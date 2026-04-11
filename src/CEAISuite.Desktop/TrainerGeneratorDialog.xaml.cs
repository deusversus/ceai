using System.IO;
using System.Windows;
using CEAISuite.Application;
using CEAISuite.Desktop.Services;
using CEAISuite.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace CEAISuite.Desktop;

public partial class TrainerGeneratorDialog : Window
{
    public TrainerGeneratorDialog()
    {
        InitializeComponent();
        DataContext = new TrainerGeneratorViewModel(
            App.Services.GetRequiredService<AddressTableService>(),
            App.Services.GetService<IProcessContext>());
    }

    private void SaveTrainerFile(object sender, RoutedEventArgs e)
    {
        var vm = (TrainerGeneratorViewModel)DataContext;
        var source = vm.GenerateSource();
        if (source is null)
        {
            MessageBox.Show("No entries selected.", "Trainer Generator", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "C# Source Files (*.cs)|*.cs",
            FileName = $"{vm.ProcessName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)}_Trainer.cs",
            Title = "Save Trainer Source"
        };

        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, source);
            vm.StatusText = $"Saved to {dialog.FileName}";
        }
    }

    private void CloseDialog(object sender, RoutedEventArgs e) => Close();
}
