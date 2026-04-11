using System.Collections.ObjectModel;
using System.Globalization;
using CEAISuite.Application;
using CEAISuite.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public sealed partial class TrainerEntrySelection : ObservableObject
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Address { get; init; } = "";
    public string DataType { get; init; } = "";
    public string Value { get; init; } = "";
    [ObservableProperty] private bool _isSelected = true;
}

public sealed partial class TrainerGeneratorViewModel : ObservableObject
{
    private readonly AddressTableService _addressTableService;

    [ObservableProperty] private string _processName = "";
    [ObservableProperty] private string _title = "Trainer";
    [ObservableProperty] private int _refreshIntervalMs = 100;
    [ObservableProperty] private ObservableCollection<TrainerEntrySelection> _entries = [];
    [ObservableProperty] private string? _previewSource;
    [ObservableProperty] private bool _isPreviewVisible;
    [ObservableProperty] private string _statusText = "";

    public TrainerGeneratorViewModel(AddressTableService addressTableService, IProcessContext? processContext = null)
    {
        _addressTableService = addressTableService;

        // Default process name from attached process
        if (processContext?.AttachedProcessName is { } name)
            ProcessName = name;

        // Populate entries from locked address table entries
        var locked = addressTableService.Entries.Where(e => e.IsLocked).ToList();
        var all = addressTableService.Entries;

        // If there are locked entries, pre-select those; otherwise show all
        var source = locked.Count > 0 ? locked : all.ToList();
        Entries = new ObservableCollection<TrainerEntrySelection>(
            source.Select(e => new TrainerEntrySelection
            {
                Id = e.Id,
                Label = e.Label,
                Address = e.Address,
                DataType = e.DataType.ToString(),
                Value = e.LockedValue ?? e.CurrentValue,
                IsSelected = e.IsLocked
            }));

        StatusText = locked.Count > 0
            ? $"{locked.Count} locked entries pre-selected"
            : $"{all.Count} entries (none locked \u2014 select entries to include)";
    }

    [RelayCommand]
    private void GeneratePreview()
    {
        var selected = GetSelectedEntries();
        if (selected.Count == 0)
        {
            StatusText = "No entries selected.";
            return;
        }
        PreviewSource = ScriptGenerationService.GenerateTrainerScript(selected, ProcessName);
        IsPreviewVisible = true;
        StatusText = string.Create(CultureInfo.InvariantCulture, $"Preview generated: {selected.Count} entries, {PreviewSource.Length:#,0} chars");
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var e in Entries) e.IsSelected = true;
        StatusText = $"{Entries.Count} entries selected";
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var e in Entries) e.IsSelected = false;
        StatusText = "No entries selected";
    }

    public string? GenerateSource()
    {
        var selected = GetSelectedEntries();
        if (selected.Count == 0) return null;
        return ScriptGenerationService.GenerateTrainerScript(selected, ProcessName);
    }

    private List<AddressTableEntry> GetSelectedEntries()
    {
        var selectedIds = Entries.Where(e => e.IsSelected).Select(e => e.Id).ToHashSet();
        return _addressTableService.Entries
            .Where(e => selectedIds.Contains(e.Id))
            .ToList();
    }
}
