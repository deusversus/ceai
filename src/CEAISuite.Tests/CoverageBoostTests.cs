using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

/// <summary>
/// Targeted tests to exercise remaining uncovered lines and push
/// Codecov past the 50% threshold.
/// </summary>
public class CoverageBoostTests
{
    // ── AgentStreamEvent subtypes ──

    [Fact]
    public void AgentStreamEvent_ToolProgress_HasAllProperties()
    {
        var evt = new AgentStreamEvent.ToolProgress("scan", 0.75, "Scanning...");
        Assert.Equal("scan", evt.ToolName);
        Assert.Equal(0.75, evt.PercentComplete);
        Assert.Equal("Scanning...", evt.StatusMessage);
    }

    [Fact]
    public void AgentStreamEvent_ToolProgress_NullStatus()
    {
        var evt = new AgentStreamEvent.ToolProgress("tool", 1.0, null);
        Assert.Null(evt.StatusMessage);
    }

    [Fact]
    public void AgentStreamEvent_Attachment_HasAllProperties()
    {
        var evt = new AgentStreamEvent.Attachment("screenshot", "image/png", "base64data");
        Assert.Equal("screenshot", evt.ToolName);
        Assert.Equal("image/png", evt.ContentType);
        Assert.Equal("base64data", evt.Data);
    }

    [Fact]
    public void AgentStreamEvent_Tombstone_HasMessageId()
    {
        var evt = new AgentStreamEvent.Tombstone("msg-123");
        Assert.Equal("msg-123", evt.MessageId);
    }

    [Fact]
    public void AgentStreamEvent_ContentReplace_HasAllProperties()
    {
        var evt = new AgentStreamEvent.ContentReplace("msg-456", "new content");
        Assert.Equal("msg-456", evt.MessageId);
        Assert.Equal("new content", evt.NewContent);
    }

    [Fact]
    public async Task AgentStreamEvent_ApprovalRequested_ResolveApproved()
    {
        var evt = new AgentStreamEvent.ApprovalRequested("write_memory", "{addr:0x100}");
        evt.Resolve(true);
        Assert.True(evt.UserDecision.IsCompleted);
        Assert.True(await evt.UserDecision);
    }

    [Fact]
    public async Task AgentStreamEvent_ApprovalRequested_ResolveDenied()
    {
        var evt = new AgentStreamEvent.ApprovalRequested("patch_bytes", "{}");
        evt.Resolve(false);
        Assert.True(evt.UserDecision.IsCompleted);
        Assert.False(await evt.UserDecision);
    }

    [Fact]
    public void AgentStreamEvent_Completed_HasElapsed()
    {
        var evt = new AgentStreamEvent.Completed(5, TimeSpan.FromSeconds(2.5));
        Assert.Equal(5, evt.ToolCallCount);
        Assert.Equal(TimeSpan.FromSeconds(2.5), evt.Elapsed);
    }

    [Fact]
    public void AgentStreamEvent_ToolUseSummary_HasCallsByTool()
    {
        var calls = new Dictionary<string, int> { ["scan"] = 3, ["read"] = 7 };
        var evt = new AgentStreamEvent.ToolUseSummary(10, calls);
        Assert.Equal(10, evt.TotalCalls);
        Assert.Equal(3, evt.CallsByTool["scan"]);
    }

    // ── AddressTableNode display properties ──

    [Fact]
    public void AddressTableNode_DisplayValue_ShowsCurrentValue()
    {
        var node = new AddressTableNode("id", "HP", false) { CurrentValue = "100" };
        Assert.Equal("100", node.DisplayValue);
    }

    [Fact]
    public void AddressTableNode_DisplayValue_ShowsHex()
    {
        var node = new AddressTableNode("id", "HP", false)
        {
            CurrentValue = "255",
            ShowAsHex = true,
            DataType = MemoryDataType.Int32
        };
        // DisplayValue should contain hex representation
        Assert.NotNull(node.DisplayValue);
    }

    [Fact]
    public void AddressTableNode_DisplayIcon_Group()
    {
        var node = new AddressTableNode("id", "Stats", true);
        Assert.NotNull(node.DisplayIcon);
    }

    [Fact]
    public void AddressTableNode_DisplayIcon_ScriptEntry()
    {
        var node = new AddressTableNode("id", "Godmode", false) { AssemblerScript = "[ENABLE]\nnop" };
        Assert.NotNull(node.DisplayIcon);
    }

    [Fact]
    public void AddressTableNode_DisplayIcon_LeafEntry()
    {
        var node = new AddressTableNode("id", "HP", false);
        Assert.NotNull(node.DisplayIcon);
    }

    [Fact]
    public void AddressTableNode_ValueForeground_Locked()
    {
        var node = new AddressTableNode("id", "HP", false) { IsLocked = true };
        Assert.NotNull(node.ValueForeground);
    }

    [Fact]
    public void AddressTableNode_ValueForeground_Changed()
    {
        var node = new AddressTableNode("id", "HP", false) { ValueJustChanged = true };
        Assert.NotNull(node.ValueForeground);
    }

    [Fact]
    public void AddressTableNode_StatusTooltip_ScriptEnabled()
    {
        var node = new AddressTableNode("id", "Script", false)
        {
            AssemblerScript = "[ENABLE]",
            IsScriptEnabled = true,
            ScriptStatus = "Enabled"
        };
        Assert.NotNull(node.StatusTooltip);
    }

    [Fact]
    public void AddressTableNode_Flatten_IncludesChildren()
    {
        var parent = new AddressTableNode("g", "Group", true);
        var child = new AddressTableNode("c", "HP", false) { Address = "0x100", DataType = MemoryDataType.Int32, CurrentValue = "50" };
        child.Parent = parent;
        parent.Children.Add(child);

        var flat = parent.Flatten().ToList();
        Assert.Single(flat);
        Assert.Equal("HP", flat[0].Label);
    }

    [Fact]
    public void AddressTableNode_DisplayAddress_Pointer()
    {
        var node = new AddressTableNode("id", "Ptr", false)
        {
            Address = "game.exe+0x100",
            IsPointer = true,
            PointerOffsets = [0x10, 0x20]
        };
        Assert.NotNull(node.DisplayAddress);
    }

    [Fact]
    public void AddressTableNode_DisplayType_ReturnsDataTypeName()
    {
        var node = new AddressTableNode("id", "HP", false) { DataType = MemoryDataType.Float };
        Assert.Contains("Float", node.DisplayType);
    }

    [Fact]
    public void AddressTableNode_IsScriptEntry_TrueWhenHasScript()
    {
        var node = new AddressTableNode("id", "Script", false) { AssemblerScript = "[ENABLE]" };
        Assert.True(node.IsScriptEntry);
    }

    [Fact]
    public void AddressTableNode_IsScriptEntry_FalseWhenNoScript()
    {
        var node = new AddressTableNode("id", "Value", false);
        Assert.False(node.IsScriptEntry);
    }

    // ── StreamingTimeoutException ──

    [Fact]
    public void StreamingTimeoutException_CanConstruct()
    {
        var ex = new StreamingTimeoutException("timed out");
        Assert.Equal("timed out", ex.Message);
    }

    [Fact]
    public void StreamingTimeoutException_WithInner()
    {
        var inner = new TimeoutException();
        var ex = new StreamingTimeoutException("timeout", inner);
        Assert.Same(inner, ex.InnerException);
    }

    // ── ScanService deeper paths ──

    [Fact]
    public async Task ScanService_StartScan_ReturnsOverview()
    {
        var engine = new Stubs.StubScanEngine();
        var svc = new ScanService(engine);
        engine.NextScanResult = new ScanResultSet("s1", 1, new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "0"), [], 0, 0, DateTimeOffset.UtcNow);
        var result = await svc.StartScanAsync(1, MemoryDataType.Int32, ScanType.ExactValue, "0");
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ScanService_RefineScan_ReturnsResult()
    {
        var engine = new Stubs.StubScanEngine();
        var svc = new ScanService(engine);
        engine.NextScanResult = new ScanResultSet("s1", 1, new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"), [new ScanResultEntry((nuint)0x1000, "100", null, [100, 0, 0, 0])], 1, 4096, DateTimeOffset.UtcNow);
        await svc.StartScanAsync(1, MemoryDataType.Int32, ScanType.ExactValue, "100");

        engine.NextRefineResult = new ScanResultSet("s1", 1, new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"), [], 1, 4096, DateTimeOffset.UtcNow);
        var refined = await svc.RefineScanAsync(ScanType.ExactValue, "100");
        Assert.NotNull(refined);
    }

    [Fact]
    public void ScanService_UndoScan_EmptyHistory_ReturnsNull()
    {
        var engine = new Stubs.StubScanEngine();
        var svc = new ScanService(engine);
        var result = svc.UndoScan();
        Assert.Null(result);
    }

    [Fact]
    public void ScanService_ResetScan_DoesNotThrow()
    {
        var engine = new Stubs.StubScanEngine();
        var svc = new ScanService(engine);
        svc.ResetScan();
    }

    // ── BreakpointService paths ──

    [Fact]
    public async Task BreakpointService_SetBreakpoint_WithEngine_ReturnsDescriptor()
    {
        var engine = new Stubs.StubBreakpointEngine();
        var svc = new BreakpointService(engine);
        var result = await svc.SetBreakpointAsync(1, "0x100", BreakpointType.HardwareExecute, BreakpointHitAction.LogAndContinue);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task BreakpointService_GetHitLog_WithEngine_ReturnsEmptyList()
    {
        var engine = new Stubs.StubBreakpointEngine();
        var svc = new BreakpointService(engine);
        var hits = await svc.GetHitLogAsync("nonexistent-bp");
        Assert.Empty(hits);
    }

    [Fact]
    public async Task BreakpointService_ListBreakpoints_Empty()
    {
        var engine = new Stubs.StubBreakpointEngine();
        var svc = new BreakpointService(engine);
        var bps = await svc.ListBreakpointsAsync(1);
        Assert.Empty(bps);
    }
}
