using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Threading;
using CEAISuite.Application;
using CEAISuite.Desktop.Controls;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

public partial class MemoryBrowserViewModel : ObservableObject, IDisposable
{
    private readonly IEngineFacade _engine;
    private readonly IProcessContext _processContext;
    private readonly IOutputLog _outputLog;
    private readonly IClipboardService _clipboard;
    private readonly IMemoryProtectionEngine _protectionEngine;
    private readonly INavigationService _navigation;
    private readonly IDisassemblyEngine _disassemblyEngine;
    private readonly AddressTableService _addressTableService;
    private readonly MemorySnapshotService _snapshotService;
    private readonly StructureDissectorService _dissectorService;
    private readonly CodeInjectionTemplateService _codeInjection;
    private readonly HexEditUndoService _undoService = new();
    private readonly EventSubscriptions _subs = new();

    private DispatcherTimer? _autoTimer;
    private DispatcherTimer? _editResumeTimer;
    private byte[]? _previousBytes;
    private bool _isEditingPaused; // auto-refresh paused during edits

    public DataInspectorViewModel DataInspector { get; } = new();

    public MemoryBrowserViewModel(
        IEngineFacade engine,
        IProcessContext processContext,
        IOutputLog outputLog,
        IClipboardService clipboard,
        IMemoryProtectionEngine protectionEngine,
        INavigationService navigation,
        IDisassemblyEngine disassemblyEngine,
        AddressTableService addressTableService,
        MemorySnapshotService snapshotService,
        StructureDissectorService dissectorService,
        CodeInjectionTemplateService codeInjection)
    {
        _engine = engine;
        _processContext = processContext;
        _outputLog = outputLog;
        _clipboard = clipboard;
        _protectionEngine = protectionEngine;
        _navigation = navigation;
        _disassemblyEngine = disassemblyEngine;
        _addressTableService = addressTableService;
        _snapshotService = snapshotService;
        _dissectorService = dissectorService;
        _codeInjection = codeInjection;

        _subs.Subscribe(h => _processContext.ProcessChanged += h, h => _processContext.ProcessChanged -= h, OnProcessChanged);
    }

    // ── Observable Properties ──

    [ObservableProperty]
    private string _goToAddress = "";

    [ObservableProperty]
    private ulong _baseAddress;

    [ObservableProperty]
    private byte[] _memoryBuffer = [];

    [ObservableProperty]
    private byte[]? _previousBuffer;

    [ObservableProperty]
    private int _byteCount = 256;

    [ObservableProperty]
    private int _bytesPerLine = 16;

    [ObservableProperty]
    private string _statusText = "Attach to a process to browse memory.";

    [ObservableProperty]
    private bool _autoRefreshEnabled;

    [ObservableProperty]
    private bool _isAttached;

    [ObservableProperty]
    private int _selectionStart;

    [ObservableProperty]
    private int _selectionLength;

    [ObservableProperty]
    private int _cursorOffset;

    [ObservableProperty]
    private bool _isReadOnly = true;

    [ObservableProperty]
    private string _protectionText = "";

    /// <summary>Available byte count options for the dropdown.</summary>
    public int[] ByteCountOptions { get; } = [64, 128, 256, 512, 1024];

    /// <summary>Available bytes-per-line options for the dropdown.</summary>
    public int[] BytesPerLineOptions { get; } = [8, 16, 32];

    [ObservableProperty]
    private string _searchPattern = "";

    [ObservableProperty]
    private string _searchStatus = "";

    public ObservableCollection<int> SearchHits { get; } = [];

    private int _currentSearchHitIndex = -1;

    public bool CanUndo => _undoService.CanUndo;
    public bool CanRedo => _undoService.CanRedo;

    // ── 5G: Inline disassembly ──

    [ObservableProperty]
    private bool _showDisassembly;

    [ObservableProperty]
    private string _disassemblyAnnotation = "";

    // ── 5I: Bookmarks ──

    public ObservableCollection<MemoryBookmark> Bookmarks { get; } = [];

    /// <summary>Bookmark addresses projected for the HexEditorControl binding.</summary>
    public IEnumerable<ulong> BookmarkAddresses => Bookmarks.Select(b => b.Address);

    // ── 5H: Structure spider ──

    public ObservableCollection<StructureSpiderNode> SpiderNodes { get; } = [];

    // ── Commands ──

    // ── 5D: Copy/Paste ──

    [RelayCommand]
    private void CopyHex()
    {
        var selected = GetSelectedBytes();
        if (selected.Length == 0) return;
        var hex = string.Join(" ", selected.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        _clipboard.SetText(hex);
        StatusText = $"Copied {selected.Length} bytes as hex.";
    }

    [RelayCommand]
    private void CopyAscii()
    {
        var selected = GetSelectedBytes();
        if (selected.Length == 0) return;
        var sb = new StringBuilder(selected.Length);
        foreach (var b in selected)
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
        _clipboard.SetText(sb.ToString());
        StatusText = $"Copied {selected.Length} bytes as ASCII.";
    }

    [RelayCommand]
    private void CopyAddress()
    {
        var addr = BaseAddress + (ulong)CursorOffset;
        _clipboard.SetText($"0x{addr:X}");
        StatusText = $"Copied address 0x{addr:X}.";
    }

    [RelayCommand]
    private async Task PasteHexAsync()
    {
        var text = _clipboard.GetText();
        if (string.IsNullOrWhiteSpace(text)) return;

        var bytes = ParseHexString(text);
        if (bytes.Length == 0) { StatusText = "Invalid hex string."; return; }

        var addr = BaseAddress + (ulong)CursorOffset;
        var oldBytes = MemoryBuffer.AsSpan(CursorOffset, Math.Min(bytes.Length, MemoryBuffer.Length - CursorOffset)).ToArray();

        var success = await WriteBytesToProcessAsync(addr, bytes);
        if (!success) return;

        ApplyLocalEdit(CursorOffset, bytes);
        _undoService.Push(new HexEditOperation(addr, oldBytes, bytes));
        NotifyUndoRedoChanged();
        StatusText = $"Pasted {bytes.Length} bytes at 0x{addr:X}.";
    }

    // ── 5D: Search ──

    [RelayCommand]
    private void Search()
    {
        SearchHits.Clear();
        _currentSearchHitIndex = -1;

        if (string.IsNullOrWhiteSpace(SearchPattern) || MemoryBuffer.Length == 0)
        {
            SearchStatus = "";
            return;
        }

        var pattern = ParseSearchPattern(SearchPattern);
        if (pattern.Length == 0) { SearchStatus = "Invalid search pattern."; return; }

        for (var i = 0; i <= MemoryBuffer.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (pattern[j] is { } expected && MemoryBuffer[i + j] != expected)
                { match = false; break; }
            }
            if (match) SearchHits.Add(i);
        }

        SearchStatus = SearchHits.Count == 0
            ? "No matches."
            : $"{SearchHits.Count} match(es) found.";

        if (SearchHits.Count > 0)
        {
            _currentSearchHitIndex = 0;
            JumpToSearchHit(0);
        }
    }

    [RelayCommand]
    private void NextSearchHit()
    {
        if (SearchHits.Count == 0) return;
        _currentSearchHitIndex = (_currentSearchHitIndex + 1) % SearchHits.Count;
        JumpToSearchHit(_currentSearchHitIndex);
    }

    [RelayCommand]
    private void PrevSearchHit()
    {
        if (SearchHits.Count == 0) return;
        _currentSearchHitIndex = (_currentSearchHitIndex - 1 + SearchHits.Count) % SearchHits.Count;
        JumpToSearchHit(_currentSearchHitIndex);
    }

    private void JumpToSearchHit(int index)
    {
        var offset = SearchHits[index];
        CursorOffset = offset;
        SelectionStart = offset;
        SelectionLength = 0;
        SearchStatus = $"Match {index + 1} of {SearchHits.Count}";
    }

    // ── 5F: Context Menu / Navigation ──

    [RelayCommand]
    private void FollowPointer()
    {
        var is32Bit = Is32BitProcess;
        var ptrSize = is32Bit ? 4 : 8;
        if (MemoryBuffer.Length == 0 || CursorOffset + ptrSize > MemoryBuffer.Length) return;
        var ptr = is32Bit
            ? BitConverter.ToUInt32(MemoryBuffer, CursorOffset)
            : BitConverter.ToUInt64(MemoryBuffer, CursorOffset);
        if (ptr == 0) { StatusText = "Null pointer."; return; }
        _ = NavigateToAddressAsync(ptr);
    }

    private bool Is32BitProcess =>
        _processContext.CurrentInspection?.Architecture?.Equals("x86", StringComparison.OrdinalIgnoreCase) == true;

    [RelayCommand]
    private void DisassembleAtCursor()
    {
        var addr = BaseAddress + (ulong)CursorOffset;
        _navigation.ShowDocument("disassembler", $"0x{addr:X}");
    }

    [RelayCommand]
    private void DissectAtCursor()
    {
        var addr = BaseAddress + (ulong)CursorOffset;
        _navigation.ShowDocument("structureDissector", $"0x{addr:X}");
    }

    // ── 5G: Inline Disassembly ──

    private async Task RefreshDisassemblyAsync()
    {
        if (!ShowDisassembly) return;
        if (_processContext.AttachedProcessId is not { } pid) return;

        try
        {
            var addr = BaseAddress + (ulong)CursorOffset;
            var result = await _disassemblyEngine.DisassembleAsync(pid, (nuint)addr, 8);
            var sb = new StringBuilder();
            foreach (var instr in result.Instructions)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  0x{(ulong)instr.Address:X}: {instr.Mnemonic} {instr.Operands}");
            DisassemblyAnnotation = sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            DisassemblyAnnotation = $"  Disassembly failed: {ex.Message}";
        }
    }

    // ── 5I: Bookmarks ──

    [RelayCommand]
    private void AddBookmark()
    {
        var addr = BaseAddress + (ulong)CursorOffset;
        if (Bookmarks.Any(b => b.Address == addr))
        {
            StatusText = $"Bookmark already exists at 0x{addr:X}.";
            return;
        }
        Bookmarks.Add(new MemoryBookmark { Address = addr, Label = $"Bookmark @ 0x{addr:X}" });
        StatusText = $"Bookmark added at 0x{addr:X}.";
    }

    [RelayCommand]
    private void RemoveBookmark()
    {
        var addr = BaseAddress + (ulong)CursorOffset;
        var bm = Bookmarks.FirstOrDefault(b => b.Address == addr);
        if (bm is not null)
        {
            Bookmarks.Remove(bm);
            StatusText = $"Bookmark removed at 0x{addr:X}.";
        }
    }

    [RelayCommand]
    private void GoToBookmark(MemoryBookmark? bookmark)
    {
        if (bookmark is null) return;
        _ = NavigateToAddressAsync(bookmark.Address);
    }

    [RelayCommand]
    private void DeleteBookmark(MemoryBookmark? bookmark)
    {
        if (bookmark is null) return;
        Bookmarks.Remove(bookmark);
        StatusText = $"Bookmark removed at 0x{bookmark.Address:X}.";
    }

    [RelayCommand]
    private void CopyBookmarkAddress(MemoryBookmark? bookmark)
    {
        if (bookmark is null) return;
        _clipboard.SetText($"0x{bookmark.Address:X}");
        StatusText = $"Copied address 0x{bookmark.Address:X}.";
    }

    // ── 5H: Structure Spider ──

    [RelayCommand]
    private async Task SpiderAsync()
    {
        SpiderNodes.Clear();
        if (_processContext.AttachedProcessId is not { } pid) return;

        try
        {
            var visited = new HashSet<ulong>();
            await SpiderScanAsync(pid, MemoryBuffer, BaseAddress, SpiderNodes, visited, depth: 0);
            StatusText = $"Spider found {SpiderNodes.Count} top-level pointer(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Spider failed: {ex.Message}";
        }
    }

    private async Task SpiderScanAsync(int pid, byte[] buffer, ulong bufferBase,
        ObservableCollection<StructureSpiderNode> target, HashSet<ulong> visited, int depth)
    {
        const int maxDepth = 3;
        var is32Bit = Is32BitProcess;
        var ptrSize = is32Bit ? 4 : 8;
        var maxPtr = is32Bit ? 0x7FFFFFFFUL : 0x7FFFFFFFFFFFUL;

        for (var offset = 0; offset + ptrSize <= buffer.Length; offset += ptrSize)
        {
            var ptr = is32Bit
                ? BitConverter.ToUInt32(buffer, offset)
                : BitConverter.ToUInt64(buffer, offset);
            if (ptr == 0 || ptr < 0x10000 || ptr > maxPtr) continue;
            if (!visited.Add(ptr)) continue; // circular detection

            // Verify the pointer is readable
            try
            {
                var probe = await _engine.ReadMemoryAsync(pid, (nuint)ptr, 1);
                if (probe.Bytes.Count == 0) continue;

                var label = $"+0x{offset:X}: -> 0x{ptr:X}";

                // Annotate with field type from dissector if available
                string fieldType = "";
                try
                {
                    var (fields, _) = await _dissectorService.DissectAsync(pid, (nuint)ptr, 64, "auto");
                    if (fields.Count > 0)
                    {
                        fieldType = string.Join(", ", fields.Take(3).Select(f => $"{f.Offset:X}:{f.ProbableType}"));
                        label += $" [{fieldType}]";
                    }
                }
                catch (Exception ex) { _outputLog.Append("MemoryBrowser", "Debug", $"Dissection failed: {ex.Message}"); }

                var node = new StructureSpiderNode(bufferBase + (ulong)offset, offset, ptr, label, depth)
                {
                    FieldType = fieldType
                };
                target.Add(node);

                // Recurse into the pointed-to region
                if (depth < maxDepth)
                {
                    try
                    {
                        var childResult = await _engine.ReadMemoryAsync(pid, (nuint)ptr, 64);
                        var childBuffer = childResult.Bytes.ToArray();
                        if (childBuffer.Length > 0)
                            await SpiderScanAsync(pid, childBuffer, ptr, node.Children, visited, depth + 1);
                    }
                    catch (Exception ex) { _outputLog.Append("MemoryBrowser", "Debug", $"Child read failed during spider scan: {ex.Message}"); }
                }
            }
            catch (Exception ex) { _outputLog.Append("MemoryBrowser", "Debug", $"Spider scan pointer not readable: {ex.Message}"); }
        }
    }

    [RelayCommand]
    private void FollowSpiderNode(StructureSpiderNode? node)
    {
        if (node is null) return;
        _ = NavigateToAddressAsync(node.PointsTo);
    }

    // ── Gap 13: Add to Address Table ──

    [RelayCommand]
    private void AddToAddressTable()
    {
        var addr = $"0x{BaseAddress + (ulong)CursorOffset:X}";
        _addressTableService.AddEntry(addr, MemoryDataType.ByteArray, "", $"MemBrowser @ {addr}");
        StatusText = $"Added {addr} to address table.";
    }

    // ── Gap 14: Capture Snapshot ──

    [RelayCommand]
    private async Task CaptureSnapshotAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        try
        {
            var snap = await _snapshotService.CaptureAsync(pid, (nuint)BaseAddress, MemoryBuffer.Length,
                $"Snapshot @ 0x{BaseAddress:X}");
            StatusText = $"Snapshot captured: {snap.Label}";
        }
        catch (Exception ex)
        {
            StatusText = $"Snapshot failed: {ex.Message}";
        }
    }

    // ── Gap 15: Allocation Commands ──

    [RelayCommand]
    private async Task AllocateMemoryAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        try
        {
            var alloc = await _protectionEngine.AllocateAsync(pid, 4096, MemoryProtection.ExecuteReadWrite);
            await NavigateToAddressAsync((ulong)alloc.BaseAddress);
            StatusText = $"Allocated 4096 bytes at 0x{(ulong)alloc.BaseAddress:X} (RWX).";
        }
        catch (Exception ex)
        {
            StatusText = $"Allocate failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task FreeMemoryAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        try
        {
            await _protectionEngine.FreeAsync(pid, (nuint)BaseAddress);
            StatusText = $"Freed memory at 0x{BaseAddress:X}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Free failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ChangeProtectionAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;
        try
        {
            var result = await _protectionEngine.ChangeProtectionAsync(pid, (nuint)BaseAddress, ByteCount, MemoryProtection.ExecuteReadWrite);
            StatusText = $"Protection changed to RWX at 0x{BaseAddress:X}.";
            _ = QueryProtectionAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Change protection failed: {ex.Message}";
        }
    }

    // ── Gap 17: Code Injection Templates ──

    [RelayCommand]
    private async Task NopSelectionAsync()
    {
        if (SelectionLength <= 0) { StatusText = "Select bytes to NOP first."; return; }
        var addr = BaseAddress + (ulong)SelectionStart;
        var oldBytes = GetSelectedBytes();
        var nops = CodeInjectionTemplateService.NopSelection(oldBytes.Length);

        PauseAutoRefreshForEdit();
        var success = await WriteBytesToProcessAsync(addr, nops);
        if (!success) return;

        ApplyLocalEdit(SelectionStart, nops);
        _undoService.Push(new HexEditOperation(addr, oldBytes, nops));
        NotifyUndoRedoChanged();
        StatusText = $"NOP'd {nops.Length} bytes at 0x{addr:X}.";
    }

    [RelayCommand]
    private async Task InsertJmpHookAsync()
    {
        // Use a simple input approach — write a JMP to address 0 as placeholder (user fills in target)
        if (_processContext.AttachedProcessId is not { } pid) return;
        var is64 = !Is32BitProcess;
        var source = BaseAddress + (ulong)CursorOffset;

        // Allocate a code cave for the hook target
        try
        {
            var cave = await _protectionEngine.AllocateAsync(pid, 256, MemoryProtection.ExecuteReadWrite, (nuint)source);
            var hookBytes = CodeInjectionTemplateService.InsertJmpHook(source, (ulong)cave.BaseAddress, is64);
            var oldBytes = MemoryBuffer.AsSpan(CursorOffset, Math.Min(hookBytes.Length, MemoryBuffer.Length - CursorOffset)).ToArray();

            PauseAutoRefreshForEdit();
            var success = await WriteBytesToProcessAsync(source, hookBytes);
            if (!success) return;

            ApplyLocalEdit(CursorOffset, hookBytes);
            _undoService.Push(new HexEditOperation(source, oldBytes, hookBytes));
            NotifyUndoRedoChanged();
            StatusText = $"JMP hook inserted at 0x{source:X} -> cave at 0x{(ulong)cave.BaseAddress:X}.";
        }
        catch (Exception ex)
        {
            StatusText = $"JMP hook failed: {ex.Message}";
        }
    }

    // ── Helpers ──

    private byte[] GetSelectedBytes()
    {
        if (MemoryBuffer.Length == 0 || SelectionLength <= 0) return [];
        var start = Math.Max(0, SelectionStart);
        var len = Math.Min(SelectionLength, MemoryBuffer.Length - start);
        if (len <= 0) return [];
        return MemoryBuffer.AsSpan(start, len).ToArray();
    }

    private static byte[] ParseHexString(string text)
    {
        // Accept "AA BB CC" or "AABBCC" or "AA-BB-CC"
        var cleaned = text.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Replace("0x", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Trim();
        if (cleaned.Length % 2 != 0) return [];
        var bytes = new byte[cleaned.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(cleaned.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return [];
        }
        return bytes;
    }

    /// <summary>Parse search pattern: hex bytes ("90 90 90"), wildcards ("90 ?? CC"), or ASCII ("Health").</summary>
    private static byte?[] ParseSearchPattern(string pattern)
    {
        var trimmed = pattern.Trim();

        // If it looks like hex bytes (contains spaces or all hex chars)
        if (trimmed.Contains(' ', StringComparison.Ordinal) || trimmed.Contains("??", StringComparison.Ordinal))
        {
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var result = new byte?[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "??" || parts[i] == "?")
                    result[i] = null; // wildcard
                else if (byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    result[i] = b;
                else
                    return [];
            }
            return result;
        }

        // Try as pure hex string (no spaces)
        if (trimmed.All(c => "0123456789abcdefABCDEF".Contains(c, StringComparison.Ordinal)) && trimmed.Length >= 2 && trimmed.Length % 2 == 0)
        {
            var result = new byte?[trimmed.Length / 2];
            for (var i = 0; i < result.Length; i++)
            {
                if (byte.TryParse(trimmed.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    result[i] = b;
                else
                    return [];
            }
            return result;
        }

        // Treat as ASCII string
        return trimmed.Select(c => (byte?)((byte)c)).ToArray();
    }

    [RelayCommand]
    private async Task GoToAsync()
    {
        var modules = _processContext.CurrentInspection?.Modules;
        if (!AddressExpressionParser.TryParse(GoToAddress, modules, out var addr))
        {
            StatusText = "Invalid address format. Use hex (0x1234) or module+offset (game.exe+0x1A3F0).";
            return;
        }

        BaseAddress = addr;
        SelectionStart = 0;
        SelectionLength = 0;
        CursorOffset = 0;
        await ReadMemoryAsync();
    }

    [RelayCommand]
    private async Task PagePrevAsync()
    {
        var size = (ulong)ByteCount;
        BaseAddress = BaseAddress > size ? BaseAddress - size : 0;
        GoToAddress = $"0x{BaseAddress:X}";
        await ReadMemoryAsync();
    }

    [RelayCommand]
    private async Task PageNextAsync()
    {
        BaseAddress += (ulong)ByteCount;
        GoToAddress = $"0x{BaseAddress:X}";
        await ReadMemoryAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReadMemoryAsync();

    [RelayCommand]
    private async Task UndoAsync()
    {
        var op = _undoService.Undo();
        if (op is null) return;

        await WriteBytesToProcessAsync(op.Address, op.OldBytes);
        ApplyLocalEdit((int)(op.Address - BaseAddress), op.OldBytes);
        NotifyUndoRedoChanged();
        StatusText = $"Undo: restored {op.OldBytes.Length} byte(s) at 0x{op.Address:X}";
    }

    [RelayCommand]
    private async Task RedoAsync()
    {
        var op = _undoService.Redo();
        if (op is null) return;

        await WriteBytesToProcessAsync(op.Address, op.NewBytes);
        ApplyLocalEdit((int)(op.Address - BaseAddress), op.NewBytes);
        NotifyUndoRedoChanged();
        StatusText = $"Redo: wrote {op.NewBytes.Length} byte(s) at 0x{op.Address:X}";
    }

    /// <summary>Handle a byte edit event from the HexEditorControl.</summary>
    public async Task HandleByteEditedAsync(ByteEditedEventArgs e)
    {
        if (e.IsUndo) { await UndoAsync(); return; }
        if (e.IsRedo) { await RedoAsync(); return; }

        var absoluteAddr = BaseAddress + (ulong)e.BufferOffset;
        var oldBytes = new[] { e.OldValue };
        var newBytes = new[] { e.NewValue };

        // Pause auto-refresh briefly
        PauseAutoRefreshForEdit();

        // Write to process memory
        var success = await WriteBytesToProcessAsync(absoluteAddr, newBytes);
        if (!success) return;

        // Update local buffer
        ApplyLocalEdit(e.BufferOffset, newBytes);

        // Record for undo
        _undoService.Push(new HexEditOperation(absoluteAddr, oldBytes, newBytes));
        NotifyUndoRedoChanged();

        StatusText = $"Wrote 0x{e.NewValue:X2} at 0x{absoluteAddr:X}";
    }

    /// <summary>Navigate to a specific address programmatically (from other panels).</summary>
    public async Task NavigateToAddressAsync(ulong address)
    {
        BaseAddress = address;
        GoToAddress = $"0x{BaseAddress:X}";
        SelectionStart = 0;
        SelectionLength = 0;
        CursorOffset = 0;
        await ReadMemoryAsync();
    }

    /// <summary>Called when the attached process changes.</summary>
    public void AttachProcess(ulong initialAddress = 0)
    {
        StopAutoRefresh();
        _previousBytes = null;
        _undoService.Clear();
        NotifyUndoRedoChanged();

        // Resolve initial address: explicit > main module base > fallback
        if (initialAddress == 0)
            initialAddress = ResolveMainModuleBase() ?? 0x00400000UL;

        BaseAddress = initialAddress;
        GoToAddress = $"0x{BaseAddress:X}";
        IsAttached = _processContext.AttachedProcessId is not null;
        IsReadOnly = !IsAttached;
        StatusText = IsAttached
            ? $"Attached to {_processContext.AttachedProcessName}."
            : "No process attached.";

        // Auto-read on attach so the user sees data immediately
        if (IsAttached)
            _ = ReadMemoryAsync();
    }

    /// <summary>Resolve the base address of the main module (first module in the inspection).</summary>
    private ulong? ResolveMainModuleBase()
    {
        var modules = _processContext.CurrentInspection?.Modules;
        if (modules is null || modules.Count == 0) return null;

        var mainModule = modules[0];
        if (AddressExpressionParser.TryParse(mainModule.BaseAddress, null, out var addr))
            return addr;

        return null;
    }

    /// <summary>Clear state on detach.</summary>
    public void Clear()
    {
        StopAutoRefresh();
        _previousBytes = null;
        _undoService.Clear();
        NotifyUndoRedoChanged();
        MemoryBuffer = [];
        PreviousBuffer = null;
        BaseAddress = 0;
        GoToAddress = "";
        IsAttached = false;
        IsReadOnly = true;
        SelectionStart = 0;
        SelectionLength = 0;
        CursorOffset = 0;
        StatusText = "No process attached.";
    }

    // ── Auto-refresh ──

    partial void OnAutoRefreshEnabledChanged(bool value)
    {
        if (value)
            StartAutoRefresh();
        else
            StopAutoRefresh();
    }

    private void StartAutoRefresh()
    {
        StopAutoRefresh();
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _autoTimer.Tick += async (_, _) =>
        {
            if (_isEditingPaused) return;
            _autoTimer.Stop();
            await ReadMemoryAsync();
            _autoTimer?.Start();
        };
        _autoTimer.Start();
    }

    private void StopAutoRefresh()
    {
        _autoTimer?.Stop();
        _autoTimer = null;
        _editResumeTimer?.Stop();
        _editResumeTimer = null;
        _isEditingPaused = false;
    }

    private void PauseAutoRefreshForEdit()
    {
        if (_autoTimer is null) return;
        _isEditingPaused = true;

        // Cancel any existing resume timer before creating a new one
        _editResumeTimer?.Stop();
        _editResumeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _editResumeTimer.Tick += (_, _) =>
        {
            _editResumeTimer?.Stop();
            _editResumeTimer = null;
            _isEditingPaused = false;
        };
        _editResumeTimer.Start();
    }

    // ── Core read ──

    private async Task ReadMemoryAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid)
        {
            StatusText = "No process attached.";
            return;
        }

        try
        {
            var result = await _engine.ReadMemoryAsync(pid, (nuint)BaseAddress, ByteCount);
            var bytes = result.Bytes.ToArray();

            PreviousBuffer = _previousBytes;
            MemoryBuffer = bytes;
            _previousBytes = bytes;

            StatusText = $"Read {bytes.Length} bytes at 0x{BaseAddress:X} from {_processContext.AttachedProcessName} ({pid})";
            _ = QueryProtectionAsync();
        }
        catch (Exception ex)
        {
            MemoryBuffer = [];
            PreviousBuffer = null;
            StatusText = $"Read failed: {ex.Message}";
            _outputLog.Append("MemoryBrowser", "Error", $"Read at 0x{BaseAddress:X} failed: {ex.Message}");
        }
    }

    private async Task<bool> WriteBytesToProcessAsync(ulong address, byte[] data)
    {
        if (_processContext.AttachedProcessId is not { } pid)
        {
            StatusText = "No process attached.";
            return false;
        }

        try
        {
            await _engine.WriteBytesAsync(pid, (nuint)address, data);
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Write failed: {ex.Message}";
            _outputLog.Append("MemoryBrowser", "Error", $"Write at 0x{address:X} failed: {ex.Message}");
            return false;
        }
    }

    private void ApplyLocalEdit(int bufferOffset, byte[] data)
    {
        if (bufferOffset < 0 || bufferOffset >= MemoryBuffer.Length) return;

        // Clone + modify to trigger change notification
        var updated = (byte[])MemoryBuffer.Clone();
        for (var i = 0; i < data.Length && bufferOffset + i < updated.Length; i++)
            updated[bufferOffset + i] = data[i];

        MemoryBuffer = updated;
    }

    private void NotifyUndoRedoChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private void UpdateDataInspector()
    {
        if (MemoryBuffer.Length > 0 && CursorOffset >= 0 && CursorOffset < MemoryBuffer.Length)
            DataInspector.Update(MemoryBuffer, CursorOffset);
        else
            DataInspector.Entries.Clear();
    }

    private async Task QueryProtectionAsync()
    {
        if (_processContext.AttachedProcessId is not { } pid) return;

        try
        {
            var region = await _protectionEngine.QueryProtectionAsync(pid, (nuint)BaseAddress);
            var flags = new System.Text.StringBuilder();
            if (region.IsReadable) flags.Append('R');
            if (region.IsWritable) flags.Append('W');
            if (region.IsExecutable) flags.Append('X');
            ProtectionText = $"[{flags}] Region: 0x{(ulong)region.BaseAddress:X}-0x{(ulong)region.BaseAddress + (ulong)region.RegionSize:X} ({region.RegionSize:N0} bytes)";
        }
        catch (Exception ex)
        {
            _outputLog.Append("MemoryBrowser", "WARN", $"Protection query failed: {ex.Message}");
            ProtectionText = "";
        }
    }

    // ── Lifecycle ──

    partial void OnShowDisassemblyChanged(bool value)
    {
        if (value)
            _ = RefreshDisassemblyAsync();
        else
            DisassemblyAnnotation = "";
    }

    partial void OnCursorOffsetChanged(int value)
    {
        UpdateDataInspector();
        _ = RefreshDisassemblyAsync();
    }

    partial void OnMemoryBufferChanged(byte[] value)
    {
        UpdateDataInspector();
    }

    private void OnProcessChanged()
    {
        IsAttached = _processContext.AttachedProcessId is not null;
        if (!IsAttached)
            Clear();
    }

    partial void OnByteCountChanged(int value)
    {
        if (IsAttached)
            _ = ReadMemoryAsync();
    }

    public void Dispose()
    {
        StopAutoRefresh();
        _subs.Dispose();
    }
}
