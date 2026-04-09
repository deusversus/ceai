using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class MemoryBrowserViewModelTests : IDisposable
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubClipboardService _clipboard = new();
    private readonly StubNavigationService _navigation = new();
    private readonly StubDisassemblyEngine _disassemblyEngine = new();
    private readonly StubMemoryProtectionEngine _protectionEngine = new();

    private MemoryBrowserViewModel CreateVm()
    {
        var addressTableService = new AddressTableService(_engineFacade);
        var snapshotService = new MemorySnapshotService(_engineFacade);
        var dissectorService = new StructureDissectorService(_engineFacade);
        var codeInjection = new Desktop.Services.CodeInjectionTemplateService();
        return new MemoryBrowserViewModel(
            _engineFacade, _processContext, _outputLog, _clipboard,
            _protectionEngine, _navigation, _disassemblyEngine,
            addressTableService, snapshotService, dissectorService, codeInjection);
    }

    public void Dispose()
    {
        // Cleanup
    }

    [Fact]
    public void Constructor_InitializesDefaults()
    {
        var vm = CreateVm();

        Assert.Equal("", vm.GoToAddress);
        Assert.Equal(0UL, vm.BaseAddress);
        Assert.Equal(256, vm.ByteCount);
        Assert.Equal(16, vm.BytesPerLine);
        Assert.False(vm.AutoRefreshEnabled);
        Assert.False(vm.IsAttached);
        Assert.True(vm.IsReadOnly);
        Assert.Equal(0, vm.SelectionStart);
        Assert.Equal(0, vm.SelectionLength);
        Assert.Equal(0, vm.CursorOffset);
    }

    [Fact]
    public void CopyHex_NoSelection_DoesNotCopy()
    {
        var vm = CreateVm();
        vm.SelectionLength = 0;

        vm.CopyHexCommand.Execute(null);

        Assert.Null(_clipboard.LastText);
    }

    [Fact]
    public void CopyAscii_NoSelection_DoesNotCopy()
    {
        var vm = CreateVm();
        vm.SelectionLength = 0;

        vm.CopyAsciiCommand.Execute(null);

        Assert.Null(_clipboard.LastText);
    }

    [Fact]
    public void CopyAddress_CopiesToClipboard()
    {
        var vm = CreateVm();
        vm.BaseAddress = 0x10000;
        vm.CursorOffset = 0x10;

        vm.CopyAddressCommand.Execute(null);

        Assert.NotNull(_clipboard.LastText);
        Assert.Contains("0x10010", _clipboard.LastText);
    }

    [Fact]
    public void AddBookmark_AddsToBookmarksList()
    {
        var vm = CreateVm();
        vm.BaseAddress = 0x10000;
        vm.CursorOffset = 0;

        vm.AddBookmarkCommand.Execute(null);

        Assert.Single(vm.Bookmarks);
        Assert.Equal(0x10000UL, vm.Bookmarks[0].Address);
    }

    [Fact]
    public void AddBookmark_DuplicateAddress_LogsMessage()
    {
        var vm = CreateVm();
        vm.BaseAddress = 0x10000;
        vm.CursorOffset = 0;

        vm.AddBookmarkCommand.Execute(null);
        vm.AddBookmarkCommand.Execute(null);

        Assert.Single(vm.Bookmarks);
        Assert.Contains("already exists", vm.StatusText);
    }

    [Fact]
    public void RemoveBookmark_RemovesMatchingBookmark()
    {
        var vm = CreateVm();
        vm.BaseAddress = 0x10000;
        vm.CursorOffset = 0;
        vm.AddBookmarkCommand.Execute(null);
        Assert.Single(vm.Bookmarks);

        vm.RemoveBookmarkCommand.Execute(null);

        Assert.Empty(vm.Bookmarks);
    }

    [Fact]
    public void DeleteBookmark_RemovesSpecificBookmark()
    {
        var vm = CreateVm();
        vm.BaseAddress = 0x10000;
        vm.CursorOffset = 0;
        vm.AddBookmarkCommand.Execute(null);
        var bookmark = vm.Bookmarks[0];

        vm.DeleteBookmarkCommand.Execute(bookmark);

        Assert.Empty(vm.Bookmarks);
    }

    [Fact]
    public void CopyBookmarkAddress_CopiesToClipboard()
    {
        var vm = CreateVm();
        vm.BaseAddress = 0x20000;
        vm.CursorOffset = 0;
        vm.AddBookmarkCommand.Execute(null);
        var bookmark = vm.Bookmarks[0];

        vm.CopyBookmarkAddressCommand.Execute(bookmark);

        Assert.NotNull(_clipboard.LastText);
        Assert.Contains("0x20000", _clipboard.LastText);
    }

    [Fact]
    public void Clear_ResetsAllState()
    {
        var vm = CreateVm();
        vm.BaseAddress = 0x10000;
        vm.GoToAddress = "0x10000";

        vm.Clear();

        Assert.Equal(0UL, vm.BaseAddress);
        Assert.Equal("", vm.GoToAddress);
        Assert.False(vm.IsAttached);
        Assert.True(vm.IsReadOnly);
        Assert.Equal("No process attached.", vm.StatusText);
    }

    [Fact]
    public void DisassembleAtCursor_NavigatesToDisassemblerDocument()
    {
        var vm = CreateVm();
        vm.BaseAddress = 0x7FF00100;
        vm.CursorOffset = 0;

        vm.DisassembleAtCursorCommand.Execute(null);

        Assert.Single(_navigation.DocumentsShown);
        Assert.Equal("disassembler", _navigation.DocumentsShown[0].ContentId);
    }

    [Fact]
    public void DissectAtCursor_NavigatesToStructureDissector()
    {
        var vm = CreateVm();
        vm.BaseAddress = 0x7FF00100;
        vm.CursorOffset = 0;

        vm.DissectAtCursorCommand.Execute(null);

        Assert.Single(_navigation.DocumentsShown);
        Assert.Equal("structureDissector", _navigation.DocumentsShown[0].ContentId);
    }

    [Fact]
    public void ByteCountOptions_HasExpectedValues()
    {
        var vm = CreateVm();
        Assert.Contains(64, vm.ByteCountOptions);
        Assert.Contains(256, vm.ByteCountOptions);
        Assert.Contains(1024, vm.ByteCountOptions);
    }

    [Fact]
    public void BytesPerLineOptions_HasExpectedValues()
    {
        var vm = CreateVm();
        Assert.Contains(8, vm.BytesPerLineOptions);
        Assert.Contains(16, vm.BytesPerLineOptions);
        Assert.Contains(32, vm.BytesPerLineOptions);
    }

    [Fact]
    public void AddToAddressTable_AddsEntryWithCurrentAddress()
    {
        var vm = CreateVm();
        vm.BaseAddress = 0x10000;
        vm.CursorOffset = 0x20;

        vm.AddToAddressTableCommand.Execute(null);

        Assert.Contains("0x10020", vm.StatusText);
    }

    [Fact]
    public async Task GoToAsync_InvalidAddress_SetsErrorStatus()
    {
        var vm = CreateVm();
        vm.GoToAddress = "not_an_address";

        await vm.GoToCommand.ExecuteAsync(null);

        Assert.Contains("Invalid address", vm.StatusText);
    }

    [Fact]
    public void ShowDisassembly_DefaultsFalse()
    {
        var vm = CreateVm();
        Assert.False(vm.ShowDisassembly);
    }

    [Fact]
    public void Search_EmptyPattern_ClearsSearchHits()
    {
        var vm = CreateVm();
        vm.SearchPattern = "";

        vm.SearchCommand.Execute(null);

        Assert.Empty(vm.SearchHits);
        Assert.Equal("", vm.SearchStatus);
    }
}
