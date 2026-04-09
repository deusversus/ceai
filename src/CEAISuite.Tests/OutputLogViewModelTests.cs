using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class OutputLogViewModelTests
{
    private readonly StubOutputLog _outputLog = new();
    private readonly StubClipboardService _clipboard = new();

    private OutputLogViewModel CreateVm() => new(_outputLog, _clipboard);

    [Fact]
    public void Clear_ClearsOutputLogEntries()
    {
        _outputLog.Append("Test", "Info", "hello");
        _outputLog.Append("Test", "Warn", "world");
        var vm = CreateVm();

        vm.ClearCommand.Execute(null);

        Assert.Empty(_outputLog.LoggedMessages);
    }

    [Fact]
    public void CopyLine_WithSelectedEntry_CopiesToClipboard()
    {
        var vm = CreateVm();
        vm.SelectedEntry = new Desktop.Models.OutputLogEntry
        {
            Timestamp = "12:00:00",
            Source = "Scanner",
            Level = "Info",
            Message = "Scan complete"
        };

        vm.CopyLineCommand.Execute(null);

        Assert.NotNull(_clipboard.LastText);
        Assert.Contains("Scanner", _clipboard.LastText);
        Assert.Contains("Scan complete", _clipboard.LastText);
    }

    [Fact]
    public void CopyLine_NoSelection_DoesNotCopy()
    {
        var vm = CreateVm();
        vm.SelectedEntry = null;

        vm.CopyLineCommand.Execute(null);

        Assert.Null(_clipboard.LastText);
    }

    [Fact]
    public void CopyAll_CopiesAllEntriesToClipboard()
    {
        var vm = CreateVm();
        // Add entries to the Entries collection (the observable one)
        _outputLog.Entries.Add(new Desktop.Models.OutputLogEntry
        {
            Timestamp = "12:00:00", Source = "A", Level = "Info", Message = "first"
        });
        _outputLog.Entries.Add(new Desktop.Models.OutputLogEntry
        {
            Timestamp = "12:00:01", Source = "B", Level = "Warn", Message = "second"
        });

        vm.CopyAllCommand.Execute(null);

        Assert.NotNull(_clipboard.LastText);
        Assert.Contains("first", _clipboard.LastText);
        Assert.Contains("second", _clipboard.LastText);
    }

    [Fact]
    public void Entries_ReturnsSameCollectionAsOutputLog()
    {
        var vm = CreateVm();
        Assert.Same(_outputLog.Entries, vm.Entries);
    }
}
