using System.Collections.ObjectModel;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class HotkeysViewModel : ObservableObject
{
    private readonly GlobalHotkeyService _hotkeyService;

    public HotkeysViewModel(GlobalHotkeyService hotkeyService)
    {
        _hotkeyService = hotkeyService;
    }

    [ObservableProperty]
    private ObservableCollection<HotkeyDisplayItem> _hotkeys = new();

    [ObservableProperty]
    private HotkeyDisplayItem? _selectedHotkey;

    [RelayCommand]
    private void Refresh()
    {
        Hotkeys = new ObservableCollection<HotkeyDisplayItem>(
            _hotkeyService.Bindings.Select(b => new HotkeyDisplayItem
            {
                Id = b.Id,
                KeyCombo = b.Description,
                Description = b.Description
            }));
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedHotkey is not { } item) return;
        _hotkeyService.Unregister(item.Id);
        Refresh();
    }
}
