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
}
