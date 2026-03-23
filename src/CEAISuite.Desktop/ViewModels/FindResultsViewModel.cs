using System.Collections.ObjectModel;
using CEAISuite.Desktop.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class FindResultsViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<FindResultDisplayItem>? _results;

    [ObservableProperty]
    private string _statusText = "";

    [RelayCommand]
    private void Clear()
    {
        Results = null;
        StatusText = "";
    }

    /// <summary>
    /// Called by MainWindow (or later via messenger) to populate find results.
    /// </summary>
    public void Populate(IReadOnlyList<FindResultDisplayItem> items, string description)
    {
        Results = new ObservableCollection<FindResultDisplayItem>(items);
        StatusText = $"{items.Count} results — {description}";
    }
}
