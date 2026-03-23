using System.Collections.ObjectModel;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class OutputLogViewModel : ObservableObject
{
    private readonly IOutputLog _outputLog;

    public OutputLogViewModel(IOutputLog outputLog)
    {
        _outputLog = outputLog;
    }

    public ObservableCollection<OutputLogEntry> Entries => _outputLog.Entries;

    [RelayCommand]
    private void Clear() => _outputLog.Clear();
}
