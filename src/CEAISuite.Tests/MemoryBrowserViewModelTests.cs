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

    // ══════════════════════════════════════════════════════════════════
    // COPY WITH POPULATED BUFFER
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void CopyHex_WithSelection_CopiesHexString()
    {
        var vm = CreateVm();
        vm.MemoryBuffer = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        vm.SelectionStart = 0;
        vm.SelectionLength = 4;

        vm.CopyHexCommand.Execute(null);

        Assert.Equal("DE AD BE EF", _clipboard.LastText);
    }

    [Fact]
    public void CopyAscii_WithSelection_CopiesAsciiWithDots()
    {
        var vm = CreateVm();
        vm.MemoryBuffer = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x01, 0xFF };
        vm.SelectionStart = 0;
        vm.SelectionLength = 8;

        vm.CopyAsciiCommand.Execute(null);

        Assert.Equal("Hello...", _clipboard.LastText);
    }

    // ══════════════════════════════════════════════════════════════════
    // SEARCH WITH POPULATED BUFFER
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Search_HexPattern_FindsMatches()
    {
        var vm = CreateVm();
        vm.MemoryBuffer = new byte[] { 0x90, 0x90, 0xCC, 0x90, 0x90, 0xCC };
        vm.SearchPattern = "CC";

        vm.SearchCommand.Execute(null);

        Assert.Equal(2, vm.SearchHits.Count);
        Assert.Equal(2, vm.SearchHits[0]);
        Assert.Equal(5, vm.SearchHits[1]);
    }

    [Fact]
    public void Search_WildcardPattern_FindsMatches()
    {
        var vm = CreateVm();
        vm.MemoryBuffer = new byte[] { 0x90, 0xAA, 0xCC, 0x90, 0xBB, 0xCC };
        vm.SearchPattern = "90 ?? CC";

        vm.SearchCommand.Execute(null);

        Assert.Equal(2, vm.SearchHits.Count);
    }

    [Fact]
    public void Search_AsciiString_FindsMatch()
    {
        var vm = CreateVm();
        vm.MemoryBuffer = System.Text.Encoding.ASCII.GetBytes("Hello World!");
        vm.SearchPattern = "World";

        vm.SearchCommand.Execute(null);

        Assert.Single(vm.SearchHits);
        Assert.Equal(6, vm.SearchHits[0]);
    }

    [Fact]
    public void Search_NoMatch_ShowsNoMatches()
    {
        var vm = CreateVm();
        vm.MemoryBuffer = new byte[] { 0x00, 0x00, 0x00 };
        vm.SearchPattern = "FF FF";

        vm.SearchCommand.Execute(null);

        Assert.Empty(vm.SearchHits);
        Assert.Contains("No matches", vm.SearchStatus);
    }

    [Fact]
    public void NextSearchHit_CyclesForward()
    {
        var vm = CreateVm();
        vm.MemoryBuffer = new byte[] { 0xCC, 0x00, 0xCC, 0x00, 0xCC };
        vm.SearchPattern = "CC";
        vm.SearchCommand.Execute(null);
        Assert.Equal(3, vm.SearchHits.Count);

        vm.NextSearchHitCommand.Execute(null);
        Assert.Contains("2 of 3", vm.SearchStatus);

        vm.NextSearchHitCommand.Execute(null);
        Assert.Contains("3 of 3", vm.SearchStatus);

        vm.NextSearchHitCommand.Execute(null); // wraps
        Assert.Contains("1 of 3", vm.SearchStatus);
    }

    [Fact]
    public void PrevSearchHit_CyclesBackward()
    {
        var vm = CreateVm();
        vm.MemoryBuffer = new byte[] { 0xCC, 0x00, 0xCC };
        vm.SearchPattern = "CC";
        vm.SearchCommand.Execute(null);
        Assert.Equal(2, vm.SearchHits.Count);

        vm.PrevSearchHitCommand.Execute(null); // wraps to last
        Assert.Contains("2 of 2", vm.SearchStatus);
    }

    // ══════════════════════════════════════════════════════════════════
    // FOLLOW POINTER
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void FollowPointer_NullPointer_SetsNullStatus()
    {
        var vm = CreateVm();
        vm.MemoryBuffer = new byte[8]; // all zeros
        vm.CursorOffset = 0;

        vm.FollowPointerCommand.Execute(null);

        Assert.Contains("Null pointer", vm.StatusText);
    }

    [Fact]
    public void FollowPointer_EmptyBuffer_DoesNothing()
    {
        var vm = CreateVm();
        vm.MemoryBuffer = [];

        vm.FollowPointerCommand.Execute(null);
        // No crash
    }

    // ══════════════════════════════════════════════════════════════════
    // PAGING
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PagePrev_DecrementsAddress()
    {
        var vm = CreateVm();
        vm.BaseAddress = 0x1000;
        vm.ByteCount = 256;

        await vm.PagePrevCommand.ExecuteAsync(null);

        Assert.Equal(0x1000UL - 256, vm.BaseAddress);
    }

    [Fact]
    public async Task PagePrev_AtZero_StaysAtZero()
    {
        var vm = CreateVm();
        vm.BaseAddress = 100;
        vm.ByteCount = 256;

        await vm.PagePrevCommand.ExecuteAsync(null);

        Assert.Equal(0UL, vm.BaseAddress);
    }

    [Fact]
    public async Task PageNext_IncrementsAddress()
    {
        var vm = CreateVm();
        vm.BaseAddress = 0x1000;
        vm.ByteCount = 256;

        await vm.PageNextCommand.ExecuteAsync(null);

        Assert.Equal(0x1000UL + 256, vm.BaseAddress);
    }

    // ══════════════════════════════════════════════════════════════════
    // GOTO
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GoToAsync_ValidHexAddress_SetsBaseAddress()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        vm.GoToAddress = "0x400000";

        await vm.GoToCommand.ExecuteAsync(null);

        Assert.Equal(0x400000UL, vm.BaseAddress);
    }

    // ══════════════════════════════════════════════════════════════════
    // ATTACH / DETACH
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void AttachProcess_SetsIsAttachedAndBaseAddress()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;
        _processContext.AttachedProcessName = "game.exe";

        vm.AttachProcess(0x7FF000000);

        Assert.True(vm.IsAttached);
        Assert.False(vm.IsReadOnly);
        Assert.Equal(0x7FF000000UL, vm.BaseAddress);
    }

    [Fact]
    public void AttachProcess_DefaultAddress_UsesFallback()
    {
        var vm = CreateVm();
        _processContext.AttachedProcessId = 1234;

        vm.AttachProcess(); // no initial address, no modules

        Assert.Equal(0x00400000UL, vm.BaseAddress); // fallback
    }

    // ══════════════════════════════════════════════════════════════════
    // NOP SELECTION
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NopSelection_NoSelection_SetsErrorStatus()
    {
        var vm = CreateVm();
        vm.SelectionLength = 0;

        await vm.NopSelectionCommand.ExecuteAsync(null);

        Assert.Contains("Select bytes", vm.StatusText);
    }

    // ══════════════════════════════════════════════════════════════════
    // ADD TO ADDRESS TABLE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void AddToAddressTable_SetsStatus()
    {
        var vm = CreateVm();
        vm.BaseAddress = 0xDEAD;
        vm.CursorOffset = 0;

        vm.AddToAddressTableCommand.Execute(null);

        Assert.Contains("DEAD", vm.StatusText);
    }

    // ══════════════════════════════════════════════════════════════════
    // UNDO / REDO
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void CanUndo_InitiallyFalse()
    {
        var vm = CreateVm();
        Assert.False(vm.CanUndo);
        Assert.False(vm.CanRedo);
    }

    // ══════════════════════════════════════════════════════════════════
    // SNAPSHOT
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CaptureSnapshot_NoProcess_DoesNothing()
    {
        var vm = CreateVm(); // no process
        await vm.CaptureSnapshotCommand.ExecuteAsync(null);
        Assert.False(vm.IsCapturingSnapshot);
    }

    // ══════════════════════════════════════════════════════════════════
    // ALLOCATE / FREE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllocateMemory_NoProcess_DoesNothing()
    {
        var vm = CreateVm();
        await vm.AllocateMemoryCommand.ExecuteAsync(null);
        // No crash, no status change beyond default
    }

    [Fact]
    public async Task FreeMemory_NoProcess_DoesNothing()
    {
        var vm = CreateVm();
        await vm.FreeMemoryCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task ChangeProtection_NoProcess_DoesNothing()
    {
        var vm = CreateVm();
        await vm.ChangeProtectionCommand.ExecuteAsync(null);
    }

    // ══════════════════════════════════════════════════════════════════
    // SPIDER
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Spider_NoProcess_ClearsNodes()
    {
        var vm = CreateVm();
        await vm.SpiderCommand.ExecuteAsync(null);
        Assert.Empty(vm.SpiderNodes);
    }

    // ══════════════════════════════════════════════════════════════════
    // BOOKMARK ADDRESSES
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void BookmarkAddresses_ProjectsFromBookmarks()
    {
        var vm = CreateVm();
        vm.Bookmarks.Add(new Desktop.Models.MemoryBookmark { Address = 0x1000 });
        vm.Bookmarks.Add(new Desktop.Models.MemoryBookmark { Address = 0x2000 });

        var addresses = vm.BookmarkAddresses.ToList();
        Assert.Equal(2, addresses.Count);
        Assert.Contains(0x1000UL, addresses);
    }

    // ══════════════════════════════════════════════════════════════════
    // DISPOSE
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var vm = CreateVm();
        vm.Dispose();
        vm.Dispose(); // double dispose safe
    }
}
