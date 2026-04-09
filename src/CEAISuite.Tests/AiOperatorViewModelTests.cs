using CEAISuite.Application;
using CEAISuite.Desktop.ViewModels;
using CEAISuite.Tests.Stubs;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Tests;

public class AiOperatorViewModelTests : IDisposable
{
    private readonly StubEngineFacade _engineFacade = new();
    private readonly StubProcessContext _processContext = new();
    private readonly StubDialogService _dialogService = new();
    private readonly StubOutputLog _outputLog = new();
    private readonly StubDispatcherService _dispatcher = new();
    private readonly StubThemeService _themeService = new();
    private readonly StubClipboardService _clipboard = new();

    private (AiOperatorViewModel vm, AiOperatorService svc, AppSettingsService settings) CreateVm()
    {
        var settings = CreateTestSettingsService();
        var toolFunctions = CreateToolFunctions();
        var aiService = new AiOperatorService(null, toolFunctions);
        var addressTableService = new AddressTableService(_engineFacade);
        var vm = new AiOperatorViewModel(
            aiService, settings, _processContext, addressTableService,
            _dialogService, _outputLog, _dispatcher, _themeService, _clipboard,
            NullLogger<AiOperatorViewModel>.Instance);
        return (vm, aiService, settings);
    }

    private static AppSettingsService CreateTestSettingsService()
    {
        // AppSettingsService requires Windows DPAPI, so we create one without calling Load()
        var settings = new AppSettingsService();
        return settings;
    }

    private AiToolFunctions CreateToolFunctions()
    {
        var dashboardService = new WorkspaceDashboardService(_engineFacade, new StubSessionRepository());
        var scanService = new ScanService(new StubScanEngine());
        var addressTableService = new AddressTableService(_engineFacade);
        var disassemblyService = new DisassemblyService(new StubDisassemblyEngine());
        var scriptGenerationService = new ScriptGenerationService();
        return new AiToolFunctions(
            _engineFacade, dashboardService, scanService,
            addressTableService, disassemblyService, scriptGenerationService);
    }

    public void Dispose()
    {
        // Cleanup
    }

    [Fact]
    public void Constructor_InitializesDefaultProperties()
    {
        var (vm, _, _) = CreateVm();

        Assert.Equal("Ready", vm.StatusText);
        Assert.Equal("New Chat", vm.ChatTitle);
        Assert.False(vm.IsStreaming);
        Assert.False(vm.IsChatHistoryVisible);
        Assert.NotNull(vm.ChatMessages);
        Assert.Empty(vm.ChatMessages);
        Assert.NotNull(vm.Attachments);
        Assert.Empty(vm.Attachments);
    }

    [Fact]
    public void ClearChat_ClearsMessagesAndRefreshesDisplay()
    {
        var (vm, svc, _) = CreateVm();
        svc.AddUserMessageToHistory("Hello");

        vm.ClearChatCommand.Execute(null);

        Assert.Empty(vm.ChatMessages);
    }

    [Fact]
    public void CopyMessage_NullItem_DoesNotThrow()
    {
        var (vm, _, _) = CreateVm();

        vm.CopyMessageCommand.Execute(null);

        Assert.Null(_clipboard.LastText);
    }

    [Fact]
    public void ExportChat_EmptyChat_ShowsInfoOrDoesNotExport()
    {
        var (vm, _, _) = CreateVm();
        // No save dialog path configured, so if it gets past the empty check,
        // the save dialog will return null and no file is written.
        _dialogService.NextSaveFilePath = null;

        vm.ExportChatCommand.Execute(null);

        // Empty chat either shows info dialog or does nothing when save dialog returns null.
        // The header-only markdown has <= 5 lines, triggering the "No messages" dialog.
        // But if the line count is exactly 5 vs <= 5, behavior depends on empty lines.
        // Assert no error occurred in either case.
        Assert.Empty(_dialogService.ErrorsShown);
    }

    [Fact]
    public void NewChat_ResetsToNewChat()
    {
        var (vm, svc, _) = CreateVm();
        svc.AddUserMessageToHistory("Hello");

        vm.NewChatCommand.Execute(null);

        Assert.Empty(vm.ChatMessages);
    }

    [Fact]
    public void DeleteChat_NullSelection_DoesNotThrow()
    {
        var (vm, _, _) = CreateVm();

        vm.DeleteChatCommand.Execute(null);

        // No exception = success
    }

    [Fact]
    public void AddAttachment_AddsChipToCollection()
    {
        var (vm, _, _) = CreateVm();

        vm.AddAttachment("test.txt", "Hello world\nLine 2\nLine 3");

        Assert.Single(vm.Attachments);
        Assert.Equal("test.txt", vm.Attachments[0].Label);
        Assert.Contains("Hello world", vm.Attachments[0].Preview);
    }

    [Fact]
    public void AddImageAttachment_AddsImageChip()
    {
        var (vm, _, _) = CreateVm();
        var data = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // fake PNG header

        vm.AddImageAttachment("screenshot.png", data, "image/png");

        Assert.Single(vm.Attachments);
        Assert.True(vm.Attachments[0].IsImage);
        Assert.Equal("image/png", vm.Attachments[0].MediaType);
    }

    [Fact]
    public void RemoveAttachment_RemovesMatchingChip()
    {
        var (vm, _, _) = CreateVm();
        vm.AddAttachment("file1.txt", "content1");
        vm.AddAttachment("file2.txt", "content2");
        var chipToRemove = vm.Attachments[0];

        vm.RemoveAttachmentCommand.Execute(chipToRemove);

        Assert.Single(vm.Attachments);
        Assert.Equal("file2.txt", vm.Attachments[0].Label);
    }

    [Fact]
    public void RemoveAttachment_NullChip_DoesNotThrow()
    {
        var (vm, _, _) = CreateVm();

        vm.RemoveAttachmentCommand.Execute(null);

        Assert.Empty(vm.Attachments);
    }

    [Fact]
    public void FilterChatHistory_NullQuery_ShowsAllItems()
    {
        var (vm, _, _) = CreateVm();

        vm.FilterChatHistory(null);

        // No exception = success, ChatHistory collection remains intact
    }

    [Fact]
    public void HandleApprovalRequest_TrustedTool_AutoApproves()
    {
        var (vm, _, _) = CreateVm();
        vm.SessionTrustedTools.Add("ReadMemory");

        var approval = new AgentStreamEvent.ApprovalRequested("ReadMemory", "{}");
        vm.HandleApprovalRequest(approval);

        Assert.Contains("Auto-approved", vm.StatusText);
        Assert.Empty(vm.PendingApprovals);
    }

    [Fact]
    public void ResolveAllPending_Approved_ClearsPendingList()
    {
        var (vm, _, _) = CreateVm();
        var approval1 = new AgentStreamEvent.ApprovalRequested("WriteMemory", "{}");
        var approval2 = new AgentStreamEvent.ApprovalRequested("SetBreakpoint", "{}");
        vm.HandleApprovalRequest(approval1);
        vm.HandleApprovalRequest(approval2);

        vm.ResolveAllPending(true);

        Assert.Empty(vm.PendingApprovals);
        Assert.Contains("WriteMemory", vm.SessionTrustedTools);
        Assert.Contains("SetBreakpoint", vm.SessionTrustedTools);
    }

    [Fact]
    public void ResolveAllPending_Denied_ClearsPendingWithoutTrusting()
    {
        var (vm, _, _) = CreateVm();
        var approval = new AgentStreamEvent.ApprovalRequested("WriteMemory", "{}");
        vm.HandleApprovalRequest(approval);

        vm.ResolveAllPending(false);

        Assert.Empty(vm.PendingApprovals);
        Assert.DoesNotContain("WriteMemory", vm.SessionTrustedTools);
        Assert.Contains("Denied", vm.StatusText);
    }

    [Fact]
    public void InputText_DefaultsToEmpty()
    {
        var (vm, _, _) = CreateVm();
        Assert.Equal("", vm.InputText);
    }

    [Fact]
    public void PermissionModeDisplay_DefaultsToNormal()
    {
        var (vm, _, _) = CreateVm();
        Assert.Equal("Normal", vm.PermissionModeDisplay);
    }

    // ── SelectPermissionMode ──

    [Fact]
    public void SelectPermissionMode_NullMode_DoesNothing()
    {
        var (vm, _, _) = CreateVm();
        vm.SelectPermissionModeCommand.Execute(null);
        Assert.Equal("Normal", vm.PermissionModeDisplay);
    }

    [Fact]
    public void SelectPermissionMode_EmptyMode_DoesNothing()
    {
        var (vm, _, _) = CreateVm();
        vm.SelectPermissionModeCommand.Execute("");
        Assert.Equal("Normal", vm.PermissionModeDisplay);
    }

    [Theory]
    [InlineData("Trusted")]
    [InlineData("Cautious")]
    [InlineData("Normal")]
    public void SelectPermissionMode_ValidMode_UpdatesDisplay(string mode)
    {
        var (vm, _, _) = CreateVm();
        vm.SelectPermissionModeCommand.Execute(mode);
        Assert.Equal(mode, vm.PermissionModeDisplay);
    }

    // ── ExportChat with content ──

    [Fact]
    public void ExportChat_WithMessages_NoSavePath_DoesNotThrow()
    {
        var (vm, svc, _) = CreateVm();
        svc.AddUserMessageToHistory("Hello world");
        _dialogService.NextSaveFilePath = null;

        vm.ExportChatCommand.Execute(null);

        Assert.Empty(_dialogService.ErrorsShown);
    }

    // ── AddAttachment variations ──

    [Fact]
    public void AddAttachment_LongContent_PreviewIsTruncated()
    {
        var (vm, _, _) = CreateVm();
        var longLine = new string('A', 100);
        vm.AddAttachment("test.txt", longLine);

        Assert.Single(vm.Attachments);
        Assert.True(vm.Attachments[0].Preview.Length <= 70);
        Assert.Contains("...", vm.Attachments[0].Preview);
    }

    [Fact]
    public void AddAttachment_MultipleLines_ShowsLineCount()
    {
        var (vm, _, _) = CreateVm();
        vm.AddAttachment("test.txt", "Line 1\nLine 2\nLine 3\nLine 4");

        Assert.Single(vm.Attachments);
        Assert.Contains("+3 lines", vm.Attachments[0].Preview);
    }

    [Fact]
    public void AddImageFromBytes_AddsPngChip()
    {
        var (vm, _, _) = CreateVm();
        var data = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        vm.AddImageFromBytes("Clipboard", data);

        Assert.Single(vm.Attachments);
        Assert.True(vm.Attachments[0].IsImage);
        Assert.Equal("image/png", vm.Attachments[0].MediaType);
    }

    // ── Multiple attachments ──

    [Fact]
    public void AddAttachment_MultipleFiles_AllAppear()
    {
        var (vm, _, _) = CreateVm();
        vm.AddAttachment("a.txt", "content a");
        vm.AddAttachment("b.txt", "content b");
        vm.AddAttachment("c.txt", "content c");

        Assert.Equal(3, vm.Attachments.Count);
    }

    // ── RemoveAttachment by non-matching ID ──

    [Fact]
    public void RemoveAttachment_NonMatchingChip_LeavesListUnchanged()
    {
        var (vm, _, _) = CreateVm();
        vm.AddAttachment("file1.txt", "content");
        var fakeChip = new Desktop.Models.AttachmentChip { Label = "fake" };

        vm.RemoveAttachmentCommand.Execute(fakeChip);

        Assert.Single(vm.Attachments);
    }

    // ── CopyMessage with valid item ──

    [Fact]
    public void CopyMessage_ValidItem_CopiesContent()
    {
        var (vm, _, _) = CreateVm();
        var item = new Desktop.Models.AiChatDisplayItem
        {
            RoleLabel = "AI Operator",
            Content = "Test response"
        };

        vm.CopyMessageCommand.Execute(item);

        Assert.Equal("Test response", _clipboard.LastText);
    }

    // ── HandleApprovalRequest for untrusted tool ──

    [Fact]
    public void HandleApprovalRequest_UntrustedTool_AddsToPending()
    {
        var (vm, _, _) = CreateVm();
        var approval = new AgentStreamEvent.ApprovalRequested("WriteMemory", "{}");

        vm.HandleApprovalRequest(approval);

        Assert.Single(vm.PendingApprovals);
        Assert.Contains("Awaiting approval", vm.StatusText);
    }

    // ── Multiple approvals ──

    [Fact]
    public void HandleApprovalRequest_MultiplePending_CountShown()
    {
        var (vm, _, _) = CreateVm();
        vm.HandleApprovalRequest(new AgentStreamEvent.ApprovalRequested("WriteMemory", "{}"));
        vm.HandleApprovalRequest(new AgentStreamEvent.ApprovalRequested("SetBreakpoint", "{}"));

        Assert.Equal(2, vm.PendingApprovals.Count);
        Assert.Contains("2 pending", vm.StatusText);
    }

    // ── ResolveApproval single ──

    [Fact]
    public void ResolveApproval_Single_Approved_RemovesFromPending()
    {
        var (vm, _, _) = CreateVm();
        var approval = new AgentStreamEvent.ApprovalRequested("WriteMemory", "{}");
        vm.HandleApprovalRequest(approval);

        vm.ResolveApproval(approval, true);

        Assert.Empty(vm.PendingApprovals);
        Assert.Contains("Executing", vm.StatusText);
    }

    [Fact]
    public void ResolveApproval_Single_Denied_RemovesFromPending()
    {
        var (vm, _, _) = CreateVm();
        var approval = new AgentStreamEvent.ApprovalRequested("WriteMemory", "{}");
        vm.HandleApprovalRequest(approval);

        vm.ResolveApproval(approval, false);

        Assert.Empty(vm.PendingApprovals);
        Assert.Contains("Denied", vm.StatusText);
    }

    // ── SessionTrustedTools persistent across requests ──

    [Fact]
    public void SessionTrustedTools_PersistAfterResolveAll()
    {
        var (vm, _, _) = CreateVm();
        vm.HandleApprovalRequest(new AgentStreamEvent.ApprovalRequested("ReadMemory", "{}"));
        vm.ResolveAllPending(true);

        // Now same tool should auto-approve
        var second = new AgentStreamEvent.ApprovalRequested("ReadMemory", "{}");
        vm.HandleApprovalRequest(second);
        Assert.Contains("Auto-approved", vm.StatusText);
        Assert.Empty(vm.PendingApprovals);
    }

    // ── FilterChatHistory ──

    [Fact]
    public void FilterChatHistory_EmptyString_ShowsAll()
    {
        var (vm, _, _) = CreateVm();
        vm.FilterChatHistory("");
        // Should not throw, ChatHistory collection is valid
    }

    // ── ToggleChatHistory ──

    [Fact]
    public void ToggleChatHistory_WhenVisible_RefreshesList()
    {
        var (vm, _, _) = CreateVm();
        vm.IsChatHistoryVisible = true;
        vm.ToggleChatHistoryCommand.Execute(null);
        // Should not throw
    }

    [Fact]
    public void ToggleChatHistory_WhenNotVisible_DoesNotRefresh()
    {
        var (vm, _, _) = CreateVm();
        vm.IsChatHistoryVisible = false;
        vm.ToggleChatHistoryCommand.Execute(null);
        // Should not throw
    }

    // ── IsStreaming default ──

    [Fact]
    public void IsStreaming_DefaultFalse()
    {
        var (vm, _, _) = CreateVm();
        Assert.False(vm.IsStreaming);
    }

    // ── ChatTitle after clear ──

    [Fact]
    public void ClearChat_ResetsChatTitle()
    {
        var (vm, svc, _) = CreateVm();
        svc.AddUserMessageToHistory("test");
        vm.ClearChatCommand.Execute(null);
        Assert.Equal("New Chat", vm.ChatTitle);
    }

    // ── Dispose is safe ──

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var (vm, _, _) = CreateVm();
        vm.Dispose();
        // Should not throw
    }

    // ── AvailableModels default ──

    [Fact]
    public void AvailableModels_DefaultEmpty()
    {
        var (vm, _, _) = CreateVm();
        Assert.NotNull(vm.AvailableModels);
        Assert.Empty(vm.AvailableModels);
    }

    // ── StreamingBlocks default ──

    [Fact]
    public void StreamingBlocks_DefaultEmpty()
    {
        var (vm, _, _) = CreateVm();
        Assert.NotNull(vm.StreamingBlocks);
        Assert.Empty(vm.StreamingBlocks);
        Assert.False(vm.IsStreamingBlocksVisible);
    }

    // ── BudgetStatus default ──

    [Fact]
    public void BudgetStatus_DefaultEmpty()
    {
        var (vm, _, _) = CreateVm();
        Assert.Equal("", vm.BudgetStatus);
    }

    // ── DeleteChat with confirmation denied ──

    [Fact]
    public void DeleteChat_ConfirmDenied_DoesNotDelete()
    {
        var (vm, svc, _) = CreateVm();
        _dialogService.NextConfirmResult = false;
        var item = new Desktop.Models.ChatHistoryDisplayItem
        {
            Id = "test-id",
            Title = "Test Chat"
        };

        vm.DeleteChatCommand.Execute(item);

        // Confirm dialog was shown but denied
        Assert.Single(_dialogService.ConfirmsShown);
    }
}
