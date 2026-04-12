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

    private void BuildTrainerExe(object sender, RoutedEventArgs e)
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
            Filter = "Executable (*.exe)|*.exe",
            FileName = $"{vm.ProcessName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)}_Trainer.exe",
            Title = "Build Trainer Executable"
        };

        if (dialog.ShowDialog(this) != true) return;

        var result = RoslynTrainerCompiler.Compile(source, dialog.FileName, vm.Title);

        if (result.Success)
        {
            vm.StatusText = $"Built: {dialog.FileName}";
            MessageBox.Show($"Trainer compiled successfully!\n{dialog.FileName}",
                "Build Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            vm.StatusText = $"Build failed: {result.Errors.Count} error(s)";
            var errorText = string.Join("\n", result.Errors.Take(10));
            MessageBox.Show($"Compilation failed:\n\n{errorText}",
                "Build Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseDialog(object sender, RoutedEventArgs e) => Close();
}
