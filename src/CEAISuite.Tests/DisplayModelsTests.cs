using System.Windows.Media;
using CEAISuite.Desktop.Controls;
using CEAISuite.Desktop.Models;

namespace CEAISuite.Tests;

public sealed class DisplayModelsTests
{
    // ── AiChatDisplayItem ──

    [Fact]
    public void AiChatDisplayItem_DefaultProperties_AreEmpty()
    {
        var item = new AiChatDisplayItem();
        Assert.Equal("", item.RoleLabel);
        Assert.Equal("", item.Content);
        Assert.Equal("", item.Timestamp);
        Assert.Equal(Brushes.Transparent, item.Background);
        Assert.Null(item.ImageData);
        Assert.False(item.HasImage);
        Assert.Null(item.ImageSource);
    }

    [Fact]
    public void AiChatDisplayItem_WithImageData_HasImageIsTrue()
    {
        var item = new AiChatDisplayItem { ImageData = new byte[] { 1, 2, 3 } };
        Assert.True(item.HasImage);
    }

    [Fact]
    public void AiChatDisplayItem_PropertyInit_SetsValues()
    {
        var item = new AiChatDisplayItem
        {
            RoleLabel = "User",
            Content = "Hello",
            Timestamp = "12:00",
        };
        Assert.Equal("User", item.RoleLabel);
        Assert.Equal("Hello", item.Content);
        Assert.Equal("12:00", item.Timestamp);
    }

    // ── ProcessComboItem ──

    [Fact]
    public void ProcessComboItem_Label_FormatsPidAndName()
    {
        var item = new ProcessComboItem { Pid = 1234, Name = "game.exe" };
        Assert.Equal("game.exe (PID 1234)", item.Label);
        Assert.Equal("game.exe (PID 1234)", item.ToString());
    }

    // ── ChatHistoryDisplayItem ──

    [Fact]
    public void ChatHistoryDisplayItem_DefaultProperties_AreEmpty()
    {
        var item = new ChatHistoryDisplayItem();
        Assert.Equal("", item.Id);
        Assert.Equal("", item.Title);
        Assert.Equal("", item.TimeAgo);
        Assert.Equal("", item.Preview);
        Assert.False(item.IsCurrent);
    }

    // ── ToolCallBlock ──

    [Fact]
    public void ToolCallBlock_Icon_MapsToolNames()
    {
        Assert.Equal("\U0001f4d6", new ToolCallBlock { ToolName = "ReadMemory" }.Icon);
        Assert.Equal("\U0001f4d6", new ToolCallBlock { ToolName = "BrowseMemory" }.Icon);
        Assert.Equal("\U0001f4d6", new ToolCallBlock { ToolName = "HexDump" }.Icon);
        Assert.Equal("\u270f\ufe0f", new ToolCallBlock { ToolName = "WriteMemory" }.Icon);
        Assert.Equal("\U0001f50d", new ToolCallBlock { ToolName = "StartScan" }.Icon);
        Assert.Equal("\U0001f50d", new ToolCallBlock { ToolName = "RefineScan" }.Icon);
        Assert.Equal("\U0001f50d", new ToolCallBlock { ToolName = "GetScanResults" }.Icon);
        Assert.Equal("\U0001f52c", new ToolCallBlock { ToolName = "Disassemble" }.Icon);
        Assert.Equal("\U0001f52c", new ToolCallBlock { ToolName = "FindWritersToOffset" }.Icon);
        Assert.Equal("\U0001f6d1", new ToolCallBlock { ToolName = "SetBreakpoint" }.Icon);
        Assert.Equal("\U0001f6d1", new ToolCallBlock { ToolName = "RemoveBreakpoint" }.Icon);
        var hookIcon = new ToolCallBlock { ToolName = "InstallCodeCaveHook" }.Icon;
        Assert.NotEqual("\U0001f527", hookIcon); // Should not be the default wrench
        Assert.Equal("\U0001f4cb", new ToolCallBlock { ToolName = "ListProcesses" }.Icon);
        Assert.Equal("\U0001f4cb", new ToolCallBlock { ToolName = "AttachProcess" }.Icon);
        Assert.Equal("\U0001f4cb", new ToolCallBlock { ToolName = "InspectProcess" }.Icon);
        Assert.Equal("\U0001f527", new ToolCallBlock { ToolName = "UnknownTool" }.Icon);
    }

    [Fact]
    public void ToolCallBlock_DefaultStatus_IsRunning()
    {
        var block = new ToolCallBlock();
        Assert.Equal("running", block.Status);
        Assert.False(block.IsExpanded);
        Assert.Null(block.Result);
    }

    // ── ApprovalBlock ──

    [Fact]
    public void ApprovalBlock_DefaultStatus_IsPending()
    {
        var block = new ApprovalBlock();
        Assert.Equal("pending", block.Status);
        Assert.Null(block.Resolve);
    }

    // ── TextContentBlock ──

    [Fact]
    public void TextContentBlock_DefaultContent_IsEmpty()
    {
        var block = new TextContentBlock();
        Assert.Equal("", block.Content);
        Assert.Equal("", block.RoleLabel);
    }

    // ── AttachmentChip ──

    [Fact]
    public void AttachmentChip_TextAttachment_IsImageFalse()
    {
        var chip = new AttachmentChip { FullText = "some text" };
        Assert.False(chip.IsImage);
        Assert.Null(chip.ImageData);
        Assert.Null(chip.Thumbnail);
    }

    [Fact]
    public void AttachmentChip_ImageAttachment_IsImageTrue()
    {
        var chip = new AttachmentChip { ImageData = new byte[] { 1, 2 }, MediaType = "image/png" };
        Assert.True(chip.IsImage);
    }

    [Fact]
    public void AttachmentChip_DefaultLabel_IsPasted()
    {
        var chip = new AttachmentChip();
        Assert.Equal("Pasted", chip.Label);
        Assert.NotEmpty(chip.Id);
    }

    // ── ModelOption record ──

    [Fact]
    public void ModelOption_RecordEquality_Works()
    {
        var a = new ModelOption("OpenAI", "gpt-4", "GPT-4");
        var b = new ModelOption("OpenAI", "gpt-4", "GPT-4");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ModelOption_IsHeader_DefaultsFalse()
    {
        var opt = new ModelOption("Anthropic", "claude-3", "Claude 3");
        Assert.False(opt.IsHeader);
    }

    [Fact]
    public void ModelOption_IsHeader_CanBeTrue()
    {
        var opt = new ModelOption("Anthropic", "claude-3", "Claude 3", IsHeader: true);
        Assert.True(opt.IsHeader);
    }

    // ── StructureFieldDisplayItem ──

    [Fact]
    public void StructureFieldDisplayItem_OffsetHex_FormatsCorrectly()
    {
        var item = new StructureFieldDisplayItem { Offset = 255 };
        Assert.Equal("0x0FF", item.OffsetHex);
    }

    [Fact]
    public void StructureFieldDisplayItem_ConfidencePercent_FormatsCorrectly()
    {
        var item = new StructureFieldDisplayItem { Confidence = 0.85 };
        Assert.Equal("85%", item.ConfidencePercent);
    }

    // ── BreakpointDisplayItem ──

    [Fact]
    public void BreakpointDisplayItem_DefaultProperties()
    {
        var item = new BreakpointDisplayItem();
        Assert.Equal("", item.Id);
        Assert.Equal("", item.Address);
        Assert.Equal("", item.Type);
        Assert.Equal(0, item.HitCount);
    }

    // ── OutputLogEntry ──

    [Fact]
    public void OutputLogEntry_DefaultProperties()
    {
        var entry = new OutputLogEntry();
        Assert.Equal("", entry.Timestamp);
        Assert.Equal("", entry.Source);
        Assert.Equal("", entry.Level);
        Assert.Equal("", entry.Message);
    }

    // ── ScriptDisplayItem ──

    [Fact]
    public void ScriptDisplayItem_DefaultProperties()
    {
        var item = new ScriptDisplayItem();
        Assert.Equal("", item.Id);
        Assert.Equal("", item.Label);
        Assert.False(item.IsEnabled);
    }

    // ── SessionDisplayItem ──

    [Fact]
    public void SessionDisplayItem_DefaultProperties()
    {
        var item = new SessionDisplayItem();
        Assert.Equal("", item.Id);
        Assert.Equal("", item.ProcessName);
        Assert.Null(item.ProcessId);
        Assert.Equal(0, item.AddressCount);
    }

    // ── AiChatDisplayItem extended ──

    [Fact]
    public void AiChatDisplayItem_ContentSetable()
    {
        var item = new AiChatDisplayItem { Content = "Hello" };
        Assert.Equal("Hello", item.Content);
        item.Content = "Updated";
        Assert.Equal("Updated", item.Content);
    }

    [Fact]
    public void AiChatDisplayItem_WithCorruptImageData_ImageSourceReturnsNull()
    {
        // Invalid bytes should not crash; ImageSource stays null
        var item = new AiChatDisplayItem { ImageData = new byte[] { 0xFF, 0xFE, 0x00 } };
        Assert.True(item.HasImage);
        // Accessing ImageSource with corrupt data returns null (logs warning internally)
        var source = item.ImageSource;
        Assert.Null(source);
    }

    [Fact]
    public void AiChatDisplayItem_NullImageData_ImageSourceReturnsNull()
    {
        var item = new AiChatDisplayItem();
        Assert.Null(item.ImageSource);
        Assert.False(item.HasImage);
    }

    // ── AttachmentChip extended ──

    [Fact]
    public void AttachmentChip_IdIsUnique()
    {
        var a = new AttachmentChip();
        var b = new AttachmentChip();
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void AttachmentChip_PropertiesInit()
    {
        var chip = new AttachmentChip
        {
            Label = "file.txt",
            Preview = "first line...",
            FullText = "full content here",
        };
        Assert.Equal("file.txt", chip.Label);
        Assert.Equal("first line...", chip.Preview);
        Assert.Equal("full content here", chip.FullText);
        Assert.False(chip.IsImage);
        Assert.Null(chip.MediaType);
    }

    [Fact]
    public void AttachmentChip_WithCorruptImageData_ThumbnailReturnsNull()
    {
        var chip = new AttachmentChip
        {
            ImageData = new byte[] { 0x00, 0x01, 0x02 },
            MediaType = "image/png"
        };
        Assert.True(chip.IsImage);
        // Corrupt image data returns null thumbnail (does not throw)
        var thumb = chip.Thumbnail;
        Assert.Null(thumb);
    }

    // ── StructureSpiderNode (if exists) — skip if not present, test other models ──

    // ── BreakpointHitDetailItem ──

    [Fact]
    public void BreakpointHitDetailItem_DefaultProperties()
    {
        var item = new BreakpointHitDetailItem();
        Assert.Equal("", item.BreakpointId);
        Assert.Equal("", item.Address);
        Assert.Equal(0, item.ThreadId);
        Assert.Equal("", item.Timestamp);
        Assert.Empty(item.Registers);
    }

    [Fact]
    public void BreakpointHitDetailItem_WithRegisters()
    {
        var regs = new[] { new RegisterDisplayItem { Name = "RAX", Value = "0x1234" } };
        var item = new BreakpointHitDetailItem { Registers = regs };
        Assert.Single(item.Registers);
        Assert.Equal("RAX", item.Registers[0].Name);
    }

    // ── RegisterDisplayItem ──

    [Fact]
    public void RegisterDisplayItem_DefaultProperties()
    {
        var reg = new RegisterDisplayItem();
        Assert.Equal("", reg.Name);
        Assert.Equal("", reg.Value);
        Assert.False(reg.IsChanged);
    }

    [Fact]
    public void RegisterDisplayItem_IsChanged_CanBeSet()
    {
        var reg = new RegisterDisplayItem { Name = "RIP", Value = "0xABC" };
        reg.IsChanged = true;
        Assert.True(reg.IsChanged);
    }

    // ── DisassemblyLineDisplayItem ──

    [Fact]
    public void DisassemblyLineDisplayItem_DefaultProperties()
    {
        var line = new DisassemblyLineDisplayItem();
        Assert.Equal("", line.Address);
        Assert.Equal("", line.HexBytes);
        Assert.Equal("", line.Mnemonic);
        Assert.Equal("", line.Operands);
        Assert.False(line.IsFunctionStart);
        Assert.False(line.IsCallOrJump);
        Assert.Null(line.XrefLabel);
        Assert.Null(line.ModuleOffset);
        Assert.Null(line.SymbolName);
        Assert.Equal("", line.Comment);
        Assert.Equal("", line.Label);
    }

    [Fact]
    public void DisassemblyLineDisplayItem_MutableProperties()
    {
        var line = new DisassemblyLineDisplayItem
        {
            Address = "0x1000",
            Mnemonic = "mov",
            Operands = "eax, [rbx+0x10]",
            IsFunctionStart = true,
            IsCallOrJump = true,
        };
        line.Comment = "important instruction";
        line.Label = "main+0x10";
        Assert.Equal("important instruction", line.Comment);
        Assert.Equal("main+0x10", line.Label);
    }

    // ── PointerPathDisplayItem ──

    [Fact]
    public void PointerPathDisplayItem_DefaultProperties()
    {
        var item = new PointerPathDisplayItem();
        Assert.Equal("", item.Chain);
        Assert.Equal("", item.ResolvedAddress);
        Assert.Equal("", item.ModuleName);
        Assert.Null(item.Source);
        Assert.Equal("Found", item.Status);
    }

    [Fact]
    public void PointerPathDisplayItem_StatusCanBeChanged()
    {
        var item = new PointerPathDisplayItem { Chain = "base+0x10->0x20" };
        item.Status = "Broken";
        Assert.Equal("Broken", item.Status);
    }

    // ── StructureCompareDisplayItem ──

    [Fact]
    public void StructureCompareDisplayItem_DefaultAndInit()
    {
        var item = new StructureCompareDisplayItem
        {
            OffsetHex = "0x000",
            Type = "Int32",
            ValueA = "100",
            ValueB = "200",
            IsDifferent = true
        };
        Assert.True(item.IsDifferent);
        Assert.Equal("0x000", item.OffsetHex);
    }

    // ── CallStackFrameDisplayItem ──

    [Fact]
    public void CallStackFrameDisplayItem_DefaultProperties()
    {
        var frame = new CallStackFrameDisplayItem();
        Assert.Equal(0, frame.FrameIndex);
        Assert.Equal("", frame.InstructionPointer);
        Assert.Equal("", frame.ModuleOffset);
        Assert.Equal("", frame.ReturnAddress);
    }

    // ── TraceEntryDisplayItem ──

    [Fact]
    public void TraceEntryDisplayItem_DefaultProperties()
    {
        var entry = new TraceEntryDisplayItem();
        Assert.Equal("", entry.Address);
        Assert.Equal("", entry.Disassembly);
        Assert.Equal(0, entry.ThreadId);
        Assert.False(entry.IsCallInstruction);
        Assert.False(entry.IsRetInstruction);
    }

    // ── ModuleDisplayItem ──

    [Fact]
    public void ModuleDisplayItem_DefaultProperties()
    {
        var mod = new ModuleDisplayItem();
        Assert.Equal("", mod.Name);
        Assert.Equal("", mod.BaseAddress);
        Assert.Equal("", mod.Size);
        Assert.Equal("", mod.Path);
    }

    // ── ThreadDisplayItem ──

    [Fact]
    public void ThreadDisplayItem_DefaultProperties()
    {
        var thread = new ThreadDisplayItem();
        Assert.Equal(0, thread.ThreadId);
        Assert.Equal("Running", thread.State);
        Assert.Equal("", thread.CurrentInstruction);
        Assert.Equal("", thread.Module);
    }

    // ── MemoryRegionDisplayItem ──

    [Fact]
    public void MemoryRegionDisplayItem_DefaultProperties()
    {
        var region = new MemoryRegionDisplayItem();
        Assert.Equal("", region.BaseAddress);
        Assert.Equal("", region.Size);
        Assert.Equal("", region.Protection);
        Assert.Equal("", region.OwnerModule);
        Assert.False(region.IsReadable);
        Assert.False(region.IsWritable);
        Assert.False(region.IsExecutable);
    }

    [Fact]
    public void MemoryRegionDisplayItem_InitProperties()
    {
        var region = new MemoryRegionDisplayItem
        {
            BaseAddress = "0x7FFE0000",
            Size = "4096",
            Protection = "RWX",
            OwnerModule = "ntdll.dll",
            IsReadable = true,
            IsWritable = true,
            IsExecutable = true
        };
        Assert.True(region.IsReadable);
        Assert.True(region.IsWritable);
        Assert.True(region.IsExecutable);
    }

    // ── HitLogDisplayItem ──

    [Fact]
    public void HitLogDisplayItem_DefaultProperties()
    {
        var hit = new HitLogDisplayItem();
        Assert.Equal("", hit.BreakpointId);
        Assert.Equal("", hit.Address);
        Assert.Equal(0, hit.ThreadId);
        Assert.Equal("", hit.Timestamp);
    }

    // ── CodeCaveHookDisplayItem ──

    [Fact]
    public void CodeCaveHookDisplayItem_DefaultProperties()
    {
        var hook = new CodeCaveHookDisplayItem();
        Assert.Equal("", hook.Id);
        Assert.Equal("", hook.OriginalAddress);
        Assert.Equal("", hook.CaveAddress);
        Assert.False(hook.IsActive);
        Assert.Equal(0, hook.HitCount);
    }

    // ── SnapshotDisplayItem ──

    [Fact]
    public void SnapshotDisplayItem_DefaultProperties()
    {
        var snap = new SnapshotDisplayItem();
        Assert.Equal("", snap.Id);
        Assert.Equal("", snap.Label);
        Assert.Equal("", snap.Address);
        Assert.Equal("", snap.Size);
        Assert.Equal("", snap.CapturedAt);
    }

    // ── SnapshotDiffDisplayItem ──

    [Fact]
    public void SnapshotDiffDisplayItem_DefaultProperties()
    {
        var diff = new SnapshotDiffDisplayItem();
        Assert.Equal("", diff.Offset);
        Assert.Equal("", diff.OldValue);
        Assert.Equal("", diff.NewValue);
        Assert.Equal("", diff.Interpretation);
    }

    // ── HotkeyDisplayItem ──

    [Fact]
    public void HotkeyDisplayItem_DefaultProperties()
    {
        var item = new HotkeyDisplayItem();
        Assert.Equal(0, item.Id);
        Assert.Equal("", item.KeyCombo);
        Assert.Equal("", item.Description);
    }

    // ── FindResultDisplayItem ──

    [Fact]
    public void FindResultDisplayItem_DefaultProperties()
    {
        var item = new FindResultDisplayItem();
        Assert.Equal("", item.Address);
        Assert.Equal("", item.Instruction);
        Assert.Equal("", item.Module);
        Assert.Equal("", item.Context);
    }

    // ── PatchHistoryDisplayItem ──

    [Fact]
    public void PatchHistoryDisplayItem_DefaultProperties()
    {
        var item = new PatchHistoryDisplayItem();
        Assert.Equal("", item.Timestamp);
        Assert.Equal("", item.Address);
        Assert.Equal("", item.DataType);
        Assert.Equal("", item.NewValue);
    }

    // ── JournalEntryDisplayItem ──

    [Fact]
    public void JournalEntryDisplayItem_DefaultProperties()
    {
        var item = new JournalEntryDisplayItem();
        Assert.Equal("", item.OperationId);
        Assert.Equal("", item.Timestamp);
        Assert.Equal("", item.OperationType);
        Assert.Equal("", item.Address);
        Assert.Equal("", item.Mode);
        Assert.Equal("", item.Status);
    }

    // ── ByteSelection ──

    [Fact]
    public void ByteSelection_Empty_IsEmpty()
    {
        Assert.True(ByteSelection.Empty.IsEmpty);
        Assert.Equal(0, ByteSelection.Empty.Start);
        Assert.Equal(0, ByteSelection.Empty.Length);
    }

    [Fact]
    public void ByteSelection_NonEmpty_Properties()
    {
        var sel = new ByteSelection(10, 5);
        Assert.False(sel.IsEmpty);
        Assert.Equal(10, sel.Start);
        Assert.Equal(5, sel.Length);
        Assert.Equal(15, sel.End);
    }

    [Fact]
    public void ByteSelection_Contains_InsideRange_ReturnsTrue()
    {
        var sel = new ByteSelection(10, 5);
        Assert.True(sel.Contains(10));
        Assert.True(sel.Contains(14));
        Assert.True(sel.Contains(12));
    }

    [Fact]
    public void ByteSelection_Contains_OutsideRange_ReturnsFalse()
    {
        var sel = new ByteSelection(10, 5);
        Assert.False(sel.Contains(9));
        Assert.False(sel.Contains(15));
        Assert.False(sel.Contains(100));
    }

    [Fact]
    public void ByteSelection_Contains_Empty_ReturnsFalse()
    {
        var sel = ByteSelection.Empty;
        Assert.False(sel.Contains(0));
        Assert.False(sel.Contains(5));
    }

    [Fact]
    public void ByteSelection_RecordEquality()
    {
        var a = new ByteSelection(5, 10);
        var b = new ByteSelection(5, 10);
        Assert.Equal(a, b);
    }

    // ── ByteEditedEventArgs ──

    [Fact]
    public void ByteEditedEventArgs_Construction_StoresProperties()
    {
        var routedEvent = HexEditorControl.ByteEditedEvent;
        var args = new ByteEditedEventArgs(routedEvent, this, 42, 0xAA, 0xBB);

        Assert.Equal(42, args.BufferOffset);
        Assert.Equal(0xAA, args.OldValue);
        Assert.Equal(0xBB, args.NewValue);
        Assert.False(args.IsUndo);
        Assert.False(args.IsRedo);
    }

    [Fact]
    public void ByteEditedEventArgs_UndoFlag()
    {
        var routedEvent = HexEditorControl.ByteEditedEvent;
        var args = new ByteEditedEventArgs(routedEvent, this, 0, 0, 0, isUndo: true);
        Assert.True(args.IsUndo);
        Assert.False(args.IsRedo);
    }

    [Fact]
    public void ByteEditedEventArgs_RedoFlag()
    {
        var routedEvent = HexEditorControl.ByteEditedEvent;
        var args = new ByteEditedEventArgs(routedEvent, this, 0, 0, 0, isRedo: true);
        Assert.False(args.IsUndo);
        Assert.True(args.IsRedo);
    }

    // ── StructureFieldDisplayItem mutable Name ──

    [Fact]
    public void StructureFieldDisplayItem_Name_CanBeSet()
    {
        var item = new StructureFieldDisplayItem { Offset = 0x10 };
        Assert.Equal("", item.Name);
        item.Name = "PlayerHP";
        Assert.Equal("PlayerHP", item.Name);
    }

    // ── TextContentBlock mutable Content ──

    [Fact]
    public void TextContentBlock_ContentCanBeSet()
    {
        var block = new TextContentBlock { Content = "initial" };
        Assert.Equal("initial", block.Content);
        block.Content = "updated";
        Assert.Equal("updated", block.Content);
    }

    // ── ToolCallBlock mutable fields ──

    [Fact]
    public void ToolCallBlock_MutableFields()
    {
        var block = new ToolCallBlock { ToolName = "ReadMemory", Arguments = "{}" };
        block.Status = "completed";
        block.Result = "00 01 02 03";
        block.IsExpanded = true;
        Assert.Equal("completed", block.Status);
        Assert.Equal("00 01 02 03", block.Result);
        Assert.True(block.IsExpanded);
    }

    // ── ApprovalBlock mutable fields ──

    [Fact]
    public void ApprovalBlock_MutableFields()
    {
        bool resolved = false;
        var block = new ApprovalBlock { ToolName = "WriteMemory", Arguments = "{\"addr\": 0}" };
        block.Resolve = approved => resolved = approved;
        block.Status = "approved";
        block.Resolve!(true);
        Assert.Equal("approved", block.Status);
        Assert.True(resolved);
    }

    // ── ChatContentBlock Timestamp ──

    [Fact]
    public void ChatContentBlock_TimestampInit()
    {
        var block = new TextContentBlock { Timestamp = "12:30 PM" };
        Assert.Equal("12:30 PM", block.Timestamp);
    }

    // ── ChatHistoryDisplayItem with values ──

    [Fact]
    public void ChatHistoryDisplayItem_InitProperties()
    {
        var item = new ChatHistoryDisplayItem
        {
            Id = "chat-123",
            Title = "My Chat",
            TimeAgo = "5m ago",
            Preview = "Hello there...",
            IsCurrent = true
        };
        Assert.Equal("chat-123", item.Id);
        Assert.Equal("My Chat", item.Title);
        Assert.True(item.IsCurrent);
    }
}
