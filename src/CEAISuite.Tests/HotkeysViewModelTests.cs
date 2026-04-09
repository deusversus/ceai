using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;

namespace CEAISuite.Tests;

public sealed class HotkeysViewModelTests : IDisposable
{
    private readonly GlobalHotkeyService _hotkeyService = new();

    public void Dispose() => _hotkeyService.Dispose();

    private HotkeysViewModel CreateVm() => new(_hotkeyService);

    [Fact]
    public void Refresh_EmptyBindings_SetsEmptyCollection()
    {
        var vm = CreateVm();

        vm.RefreshCommand.Execute(null);

        Assert.NotNull(vm.Hotkeys);
        Assert.Empty(vm.Hotkeys);
    }

    [Fact]
    public void RemoveSelected_NoSelection_DoesNotThrow()
    {
        var vm = CreateVm();
        vm.SelectedHotkey = null;

        vm.RemoveSelectedCommand.Execute(null);

        // No exception = success
    }

    [Fact]
    public void Constructor_InitializesHotkeysCollection()
    {
        var vm = CreateVm();
        Assert.NotNull(vm.Hotkeys);
    }

    [Fact]
    public void SelectedHotkey_DefaultsToNull()
    {
        var vm = CreateVm();
        Assert.Null(vm.SelectedHotkey);
    }
}
