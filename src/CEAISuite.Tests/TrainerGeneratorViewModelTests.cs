using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class TrainerGeneratorViewModelTests
{
    private static AddressTableService CreateTableWithEntries(bool locked = true)
    {
        var engine = new StubEngineFacade();
        var service = new AddressTableService(engine);
        service.AddEntry("0x12345678", MemoryDataType.Float, "100", label: "Health");
        service.AddEntry("0x12345680", MemoryDataType.Int32, "30", label: "Ammo");
        if (locked)
        {
            foreach (var e in service.Entries)
                service.ToggleLock(e.Id);
        }
        return service;
    }

    [Fact]
    public void Constructor_PopulatesLockedEntries()
    {
        var table = CreateTableWithEntries(locked: true);
        var vm = new TrainerGeneratorViewModel(table);
        Assert.Equal(2, vm.Entries.Count);
        Assert.All(vm.Entries, e => Assert.True(e.IsSelected));
    }

    [Fact]
    public void Constructor_NoLockedEntries_ShowsAll()
    {
        var table = CreateTableWithEntries(locked: false);
        var vm = new TrainerGeneratorViewModel(table);
        Assert.Equal(2, vm.Entries.Count);
        Assert.All(vm.Entries, e => Assert.False(e.IsSelected));
    }

    [Fact]
    public void SelectNone_UnchecksAll()
    {
        var table = CreateTableWithEntries();
        var vm = new TrainerGeneratorViewModel(table);
        vm.SelectNoneCommand.Execute(null);
        Assert.All(vm.Entries, e => Assert.False(e.IsSelected));
    }

    [Fact]
    public void SelectAll_ChecksAll()
    {
        var table = CreateTableWithEntries();
        var vm = new TrainerGeneratorViewModel(table);
        vm.SelectNoneCommand.Execute(null);
        vm.SelectAllCommand.Execute(null);
        Assert.All(vm.Entries, e => Assert.True(e.IsSelected));
    }

    [Fact]
    public void GeneratePreview_WithSelected_PopulatesPreviewSource()
    {
        var table = CreateTableWithEntries();
        var vm = new TrainerGeneratorViewModel(table) { ProcessName = "TestGame.exe" };
        vm.GeneratePreviewCommand.Execute(null);
        Assert.NotNull(vm.PreviewSource);
        Assert.True(vm.IsPreviewVisible);
        Assert.Contains("TestGame", vm.PreviewSource);
    }

    [Fact]
    public void GeneratePreview_NoneSelected_SetsStatus()
    {
        var table = CreateTableWithEntries();
        var vm = new TrainerGeneratorViewModel(table);
        vm.SelectNoneCommand.Execute(null);
        vm.GeneratePreviewCommand.Execute(null);
        Assert.Null(vm.PreviewSource);
        Assert.Contains("No entries", vm.StatusText);
    }

    [Fact]
    public void GenerateSource_ReturnsValidCSharp()
    {
        var table = CreateTableWithEntries();
        var vm = new TrainerGeneratorViewModel(table) { ProcessName = "game.exe" };
        var source = vm.GenerateSource();
        Assert.NotNull(source);
        Assert.Contains("WriteProcessMemory", source);
        Assert.Contains("game", source);
    }

    [Fact]
    public void GenerateSource_NoneSelected_ReturnsNull()
    {
        var table = CreateTableWithEntries();
        var vm = new TrainerGeneratorViewModel(table);
        vm.SelectNoneCommand.Execute(null);
        Assert.Null(vm.GenerateSource());
    }
}
