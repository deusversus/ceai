using System.Windows.Media;
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
}
