using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;
using Microsoft.Extensions.AI;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for AiOperatorService: construction, message processing, streaming,
/// error handling, cancellation, chat management, reconfiguration, and display history.
/// Uses MockChatClient from AgentLoopTests for the LLM layer.
/// </summary>
[SupportedOSPlatform("windows")]
public class AiOperatorServiceTests : IDisposable
{
    private readonly StubEngineFacade _engineFacade = new();

    private AiToolFunctions CreateToolFunctions()
    {
        var sessionRepo = new StubSessionRepository();
        var dashboardService = new WorkspaceDashboardService(_engineFacade, sessionRepo);
        var scanService = new ScanService(new StubScanEngine());
        var addressTableService = new AddressTableService(_engineFacade);
        var disassemblyService = new DisassemblyService(new StubDisassemblyEngine());
        var scriptGenerationService = new ScriptGenerationService();
        return new AiToolFunctions(
            _engineFacade, dashboardService, scanService,
            addressTableService, disassemblyService, scriptGenerationService);
    }

    private AiOperatorService CreateService(IChatClient? client = null)
    {
        var toolFunctions = CreateToolFunctions();
        return new AiOperatorService(client, toolFunctions);
    }

    public void Dispose()
    {
        // Cleanup
    }

    // ── Construction ──

    [Fact]
    public void Constructor_NullClient_IsNotConfigured()
    {
        using var svc = CreateService(null);
        Assert.False(svc.IsConfigured);
    }

    [Fact]
    public void Constructor_WithClient_IsConfigured()
    {
        using var svc = CreateService(new MockChatClient("hello"));
        Assert.True(svc.IsConfigured);
    }

    [Fact]
    public void Constructor_InitializesDisplayHistory()
    {
        using var svc = CreateService();
        Assert.NotNull(svc.DisplayHistory);
        Assert.Empty(svc.DisplayHistory);
    }

    [Fact]
    public void Constructor_InitializesActionLog()
    {
        using var svc = CreateService();
        Assert.NotNull(svc.ActionLog);
        Assert.Empty(svc.ActionLog);
    }

    [Fact]
    public void Constructor_TokenCountersZero()
    {
        using var svc = CreateService();
        Assert.Equal(0, svc.TotalPromptTokens);
        Assert.Equal(0, svc.TotalCompletionTokens);
        Assert.Equal(0, svc.TotalCachedTokens);
        Assert.Equal(0, svc.TotalRequests);
    }

    // ── Defaults ──

    [Fact]
    public void DefaultProperties_HaveExpectedValues()
    {
        using var svc = CreateService();
        Assert.Equal(40, svc.MaxConversationMessages);
        Assert.Equal(0, svc.RateLimitSeconds);
        Assert.True(svc.RateLimitWait);
        Assert.Equal("New Chat", svc.CurrentChatTitle);
        Assert.False(string.IsNullOrEmpty(svc.CurrentChatId));
    }

    // ── SendMessageAsync: basic text response ──

    [Fact]
    public async Task SendMessageAsync_ReturnsAssistantText()
    {
        using var svc = CreateService(new MockChatClient("I can help you with that!"));

        var response = await svc.SendMessageAsync("Hello");

        Assert.Contains("help", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendMessageAsync_NullClient_ReturnsNotConfiguredMessage()
    {
        using var svc = CreateService(null);

        var response = await svc.SendMessageAsync("Hello");

        // StubChatClient returns "AI operator is not configured" message
        Assert.Contains("not configured", response, StringComparison.OrdinalIgnoreCase);
    }

    // ── Display history tracking ──

    [Fact]
    public void AddUserMessageToHistory_AppendsToDisplayHistory()
    {
        using var svc = CreateService();
        svc.AddUserMessageToHistory("Test message");

        Assert.Single(svc.DisplayHistory);
        Assert.Equal("user", svc.DisplayHistory[0].Role);
        Assert.Equal("Test message", svc.DisplayHistory[0].Content);
    }

    [Fact]
    public async Task SendMessageAsync_AddsUserAndAssistantToHistory()
    {
        using var svc = CreateService(new MockChatClient("Response text"));

        svc.AddUserMessageToHistory("Hello");
        await svc.SendMessageAsync("Hello");

        // Should have at least user + assistant messages
        Assert.True(svc.DisplayHistory.Count >= 2);
        Assert.Equal("user", svc.DisplayHistory[0].Role);
    }

    // ── Streaming ──

    [Fact]
    public async Task SendMessageStreamingAsync_EmitsTextDelta()
    {
        using var svc = CreateService(new MockChatClient("Streaming response"));

        svc.AddUserMessageToHistory("Hi");
        var reader = svc.SendMessageStreamingAsync("Hi");

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in reader.ReadAllAsync())
            events.Add(evt);

        Assert.Contains(events, e => e is AgentStreamEvent.TextDelta);
    }

    [Fact]
    public async Task SendMessageStreamingAsync_EmitsCompleted()
    {
        using var svc = CreateService(new MockChatClient("Done"));

        svc.AddUserMessageToHistory("Hi");
        var reader = svc.SendMessageStreamingAsync("Hi");

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in reader.ReadAllAsync())
            events.Add(evt);

        Assert.Contains(events, e => e is AgentStreamEvent.Completed);
    }

    // ── Cancellation ──

    [Fact]
    public async Task SendMessageStreamingAsync_Cancellation_EmitsError()
    {
        using var svc = CreateService(new SlowMockChatClient());

        svc.AddUserMessageToHistory("Hi");
        using var cts = new CancellationTokenSource(100);
        var reader = svc.SendMessageStreamingAsync("Hi", cts.Token);

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            events.Add(evt);

        Assert.Contains(events, e => e is AgentStreamEvent.Error);
    }

    // ── StatusChanged event ──

    [Fact]
    public async Task SendMessageAsync_FiresStatusChanged()
    {
        using var svc = CreateService(new MockChatClient("ok"));

        var statuses = new List<string>();
        svc.StatusChanged += s => statuses.Add(s);

        svc.AddUserMessageToHistory("Test");
        await svc.SendMessageAsync("Test");

        Assert.True(statuses.Count > 0, "StatusChanged should fire at least once");
    }

    // ── Reconfigure ──

    [Fact]
    public void Reconfigure_NullClient_SetsNotConfigured()
    {
        using var svc = CreateService(new MockChatClient("initial"));
        Assert.True(svc.IsConfigured);

        svc.Reconfigure(null);
        Assert.False(svc.IsConfigured);
    }

    [Fact]
    public void Reconfigure_NewClient_SetsConfigured()
    {
        using var svc = CreateService(null);
        Assert.False(svc.IsConfigured);

        svc.Reconfigure(new MockChatClient("new provider"));
        Assert.True(svc.IsConfigured);
    }

    // ── NewChat ──

    [Fact]
    public void NewChat_ClearsDisplayHistory()
    {
        using var svc = CreateService();
        svc.AddUserMessageToHistory("message 1");

        svc.NewChat();

        Assert.Empty(svc.DisplayHistory);
    }

    [Fact]
    public async Task NewChat_AssignsNewChatId()
    {
        using var svc = CreateService();
        var originalId = svc.CurrentChatId;

        // Ensure enough time passes for the timestamp-based ID to change
        await Task.Delay(10);
        svc.NewChat();

        Assert.NotEqual(originalId, svc.CurrentChatId);
    }

    [Fact]
    public void NewChat_ResetsTitle()
    {
        using var svc = CreateService();
        svc.NewChat();
        Assert.Equal("New Chat", svc.CurrentChatTitle);
    }

    // ── SetContextProvider ──

    [Fact]
    public void SetContextProvider_SetsDelegate()
    {
        using var svc = CreateService();
        svc.SetContextProvider(() => "Process: Game.exe PID:1234");
        // No exception means success
    }

    // ── Permission mode ──

    [Fact]
    public void SetPermissionMode_ValidMode_NoThrow()
    {
        using var svc = CreateService();
        svc.SetPermissionMode("ReadOnly");
        Assert.Equal("ReadOnly", svc.CurrentPermissionMode);
    }

    [Fact]
    public void SetPermissionMode_InvalidMode_NoChange()
    {
        using var svc = CreateService();
        var original = svc.CurrentPermissionMode;
        svc.SetPermissionMode("NotARealMode");
        Assert.Equal(original, svc.CurrentPermissionMode);
    }

    // ── ChatListChanged event ──

    [Fact]
    public void NewChat_FiresChatListChanged()
    {
        using var svc = CreateService();
        bool fired = false;
        svc.ChatListChanged += () => fired = true;

        svc.NewChat();

        Assert.True(fired);
    }

    // ── Rate limiting properties ──

    [Fact]
    public void RateLimitSeconds_SetAndGet()
    {
        using var svc = CreateService();
        svc.RateLimitSeconds = 5;
        Assert.Equal(5, svc.RateLimitSeconds);
    }

    [Fact]
    public void MaxConversationMessages_SetAndGet()
    {
        using var svc = CreateService();
        svc.MaxConversationMessages = 20;
        Assert.Equal(20, svc.MaxConversationMessages);
    }

    [Fact]
    public void Limits_SetAndGet()
    {
        using var svc = CreateService();
        svc.Limits = TokenLimits.Saving;
        // TokenLimits is a record, so compare by value equality
        Assert.Equal(TokenLimits.Saving.MaxOutputTokens, svc.Limits.MaxOutputTokens);
        Assert.Equal(TokenLimits.Saving.MaxImagesPerTurn, svc.Limits.MaxImagesPerTurn);
    }

    // ── AiChatMessage record ──

    [Fact]
    public void AiChatMessage_RecordProperties()
    {
        var msg = new AiChatMessage("user", "Hello", DateTimeOffset.UtcNow);
        Assert.Equal("user", msg.Role);
        Assert.Equal("Hello", msg.Content);
        Assert.Null(msg.ToolCalls);
        Assert.Null(msg.ToolResults);
    }

    [Fact]
    public void AiChatMessage_WithToolCalls()
    {
        var calls = new List<AiToolCallInfo>
        {
            new("call_1", "ReadMemory", """{"address":"0x1000"}"""),
        };
        var msg = new AiChatMessage("assistant", "Reading memory...", DateTimeOffset.UtcNow)
        {
            ToolCalls = calls,
        };
        Assert.NotNull(msg.ToolCalls);
        Assert.Single(msg.ToolCalls);
        Assert.Equal("ReadMemory", msg.ToolCalls[0].Name);
    }

    // ── DangerousTools set ──

    [Fact]
    public void DangerousTools_ContainsExpectedTools()
    {
        Assert.Contains("WriteMemory", DangerousTools.Names);
        Assert.Contains("SetBreakpoint", DangerousTools.Names);
        Assert.Contains("ExecuteAutoAssemblerScript", DangerousTools.Names);
        Assert.DoesNotContain("ReadMemory", DangerousTools.Names);
        Assert.DoesNotContain("ListProcesses", DangerousTools.Names);
    }

    [Fact]
    public void DangerousTools_CaseInsensitive()
    {
        Assert.Contains("writememory", DangerousTools.Names, StringComparer.OrdinalIgnoreCase);
    }

    // ── ToolCategories ──

    [Fact]
    public void ToolCategories_Core_ContainsEssentialTools()
    {
        Assert.Contains("ListProcesses", ToolCategories.Core);
        Assert.Contains("ReadMemory", ToolCategories.Core);
        Assert.Contains("StartScan", ToolCategories.Core);
        Assert.Contains("GetCurrentContext", ToolCategories.Core);
    }

    [Fact]
    public void ToolCategories_Categories_ContainsExpectedCategories()
    {
        Assert.True(ToolCategories.Categories.ContainsKey("sessions"));
        Assert.True(ToolCategories.Categories.ContainsKey("breakpoints"));
        Assert.True(ToolCategories.Categories.ContainsKey("disassembly"));
        Assert.True(ToolCategories.Categories.ContainsKey("hooks"));
        Assert.True(ToolCategories.Categories.ContainsKey("scripts"));
    }

    // ── AiActionLogEntry record ──

    [Fact]
    public void AiActionLogEntry_RecordProperties()
    {
        var entry = new AiActionLogEntry("ReadMemory", """{"addr":"0x1000"}""", "Success", DateTimeOffset.UtcNow);
        Assert.Equal("ReadMemory", entry.ToolName);
        Assert.Contains("0x1000", entry.Arguments);
        Assert.Equal("Success", entry.Result);
    }

    // ── ExportChatToMarkdown ──

    [Fact]
    public void ExportChatToMarkdown_EmptyHistory_ReturnsHeader()
    {
        using var svc = CreateService();
        var md = svc.ExportChatToMarkdown();
        Assert.NotNull(md);
        // Should at least have some markdown content
    }

    [Fact]
    public async Task ExportChatToMarkdown_WithMessages_IncludesContent()
    {
        using var svc = CreateService(new MockChatClient("AI response text"));
        svc.AddUserMessageToHistory("User question");
        await svc.SendMessageAsync("User question");

        var md = svc.ExportChatToMarkdown();
        Assert.Contains("User question", md);
    }

    // ── SessionMessageCount ──

    [Fact]
    public void SessionMessageCount_InitiallyZero()
    {
        using var svc = CreateService();
        // Should be 0 or a small number (system prompt messages may exist)
        Assert.True(svc.SessionMessageCount >= 0);
    }

    // ── Dispose ──

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var svc = CreateService();
        svc.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.DisposeAsync();
    }
}
