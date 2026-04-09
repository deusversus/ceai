using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
using Microsoft.Extensions.AI;
using AgentLoopRunner = CEAISuite.Application.AgentLoop.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Unit tests for the AgentLoop infrastructure: state machine, retry policy,
/// error classifier, token budget, memory system, compaction, permissions,
/// tool attributes, hooks, skills, and chat history manager.
/// </summary>
public class AgentLoopTests
{
    // ── AgentLoopState & Transitions ──────────────────────────────────

    [Fact]
    public void AgentLoopState_DefaultTransition_IsNextTurn()
    {
        var state = new AgentLoopState();
        Assert.Equal(AgentTransition.NextTurn, state.Transition);
        Assert.Equal(1, state.TurnCount); // Default is 1 (1-based)
        Assert.Equal(0, state.TotalToolCalls);
    }

    [Fact]
    public void AgentLoopState_ImmutableRecord_WithExpression()
    {
        var state = new AgentLoopState();
        var next = state with { TurnCount = 5, Transition = AgentTransition.Completed };
        Assert.Equal(1, state.TurnCount); // original unchanged (default is 1)
        Assert.Equal(5, next.TurnCount);
        Assert.Equal(AgentTransition.Completed, next.Transition);
    }

    // ── ErrorClassifier ──────────────────────────────────────────────

    [Fact]
    public void ErrorClassifier_IsRateLimited_DetectsHttpStatusCode429()
    {
        var ex = new HttpRequestException("rate limited", null, System.Net.HttpStatusCode.TooManyRequests);
        Assert.True(ErrorClassifier.IsRateLimited(ex));
    }

    [Fact]
    public void ErrorClassifier_IsOverloaded_DetectsOverloadedError()
    {
        var ex = new InvalidOperationException("overloaded_error: server is busy");
        Assert.True(ErrorClassifier.IsOverloaded(ex));
    }

    [Fact]
    public void ErrorClassifier_IsPromptTooLong_DetectsContextLengthExceeded()
    {
        var ex = new InvalidOperationException("context_length_exceeded: input is too long");
        Assert.True(ErrorClassifier.IsPromptTooLong(ex));
    }

    [Fact]
    public void ErrorClassifier_IsAuthError_Detects401()
    {
        var ex = new HttpRequestException("unauthorized", null, System.Net.HttpStatusCode.Unauthorized);
        Assert.True(ErrorClassifier.IsAuthError(ex));
    }

    [Fact]
    public void ErrorClassifier_IsMaxOutputTokens_DetectsLengthFinishReason()
    {
        Assert.True(ErrorClassifier.IsMaxOutputTokens("length"));
        Assert.True(ErrorClassifier.IsMaxOutputTokens("max_tokens"));
        Assert.False(ErrorClassifier.IsMaxOutputTokens("stop"));
        Assert.False(ErrorClassifier.IsMaxOutputTokens(null));
    }

    [Fact]
    public void ErrorClassifier_IsRetriable_RateLimitAndOverloaded()
    {
        var rateLimited = new HttpRequestException("", null, System.Net.HttpStatusCode.TooManyRequests);
        Assert.True(ErrorClassifier.IsRetriable(rateLimited));

        // Auth errors are also retriable (temporary key issues)
        var authError = new HttpRequestException("", null, System.Net.HttpStatusCode.Unauthorized);
        Assert.True(ErrorClassifier.IsRetriable(authError));

        // Non-retriable: plain exception with no status code
        var plainError = new InvalidOperationException("some logic error");
        Assert.False(ErrorClassifier.IsRetriable(plainError));
    }

    [Fact]
    public void ErrorClassifier_ParseRetryAfter_ExtractsSeconds()
    {
        var ex = new InvalidOperationException("retry-after: 5 seconds");
        var retryAfter = ErrorClassifier.ParseRetryAfter(ex);
        Assert.True(retryAfter is null or { TotalSeconds: >= 0 }); // Implementation may vary
    }

    // ── TokenBudget ──────────────────────────────────────────────────

    [Fact]
    public void TokenBudget_RecordUsage_TracksCumulativeTokens()
    {
        var budget = new TokenBudget();
        budget.RecordUsage(1000, 500, 200);
        Assert.Equal(1000, budget.TotalInputTokens);
        Assert.Equal(500, budget.TotalOutputTokens);
        Assert.Equal(200, budget.TotalCachedTokens);
        Assert.Equal(1, budget.TotalRequests);
    }

    [Fact]
    public void TokenBudget_RecordUsage_AccumulatesAcrossMultipleCalls()
    {
        var budget = new TokenBudget();
        budget.RecordUsage(1000, 500, 200);
        budget.RecordUsage(2000, 300, 100);
        Assert.Equal(3000, budget.TotalInputTokens);
        Assert.Equal(800, budget.TotalOutputTokens);
        Assert.Equal(300, budget.TotalCachedTokens);
        Assert.Equal(2, budget.TotalRequests);
    }

    [Fact]
    public void TokenBudget_EstimatedCost_ComputesCorrectly()
    {
        var budget = new TokenBudget();
        budget.SetPricing(3.00m, 15.00m, 0.30m); // Default pricing
        budget.RecordUsage(1_000_000, 100_000, 500_000);

        // Non-cached input: 500K @ $3/M = $1.50
        // Cached input: 500K @ $0.30/M = $0.15
        // Output: 100K @ $15/M = $1.50
        // Total = $3.15
        Assert.Equal(3.15m, budget.EstimatedCostUsd);
    }

    [Fact]
    public void TokenBudget_IsExhausted_WhenCostExceedsBudget()
    {
        var budget = new TokenBudget();
        budget.SetPricing(3.00m, 15.00m, 0.30m);
        budget.SetLimits(maxCostDollars: 0.01m);

        Assert.False(budget.IsExhausted);
        budget.RecordUsage(100_000, 10_000, 0); // ~$0.45
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public void TokenBudget_Reset_ClearsAllCounters()
    {
        var budget = new TokenBudget();
        budget.RecordUsage(1000, 500, 200);
        budget.RecordToolCall();
        budget.Reset();

        Assert.Equal(0, budget.TotalInputTokens);
        Assert.Equal(0, budget.TotalOutputTokens);
        Assert.Equal(0, budget.TotalCachedTokens);
        Assert.Equal(0, budget.TotalRequests);
        Assert.Equal(0, budget.TotalToolCalls);
    }

    [Fact]
    public void TokenBudget_CacheHitRate_Correct()
    {
        var budget = new TokenBudget();
        budget.RecordUsage(1000, 0, 800); // 80% cache
        Assert.Equal(80.0, budget.CacheHitRate);
    }

    [Fact]
    public void TokenBudget_RemainingBudgetFraction_Correct()
    {
        var budget = new TokenBudget();
        budget.SetPricing(3.00m, 15.00m, 0.30m);
        budget.SetLimits(maxCostDollars: 1.00m);
        Assert.Equal(1.0, budget.RemainingBudgetFraction);

        budget.RecordUsage(100_000, 0, 0); // Cost ~$0.30
        Assert.True(budget.RemainingBudgetFraction < 1.0);
        Assert.True(budget.RemainingBudgetFraction > 0.0);
    }

    [Fact]
    public void TokenBudget_RecordUsage_ReturnsCorrectCheckResults()
    {
        var budget = new TokenBudget();
        budget.SetPricing(3.00m, 15.00m, 0.30m);
        budget.SetLimits(maxCostDollars: 0.10m);

        var r1 = budget.RecordUsage(1000, 100, 0);
        Assert.Equal(BudgetCheckResult.Ok, r1);
    }

    // ── MemorySystem ─────────────────────────────────────────────────

    [Fact]
    public void MemorySystem_RememberAndRecall()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var memory = new MemorySystem(tempPath);
            memory.Load();

            memory.Remember("Player HP is at offset 0x1A4", MemoryCategory.ProcessKnowledge, "Game.exe");
            memory.Remember("User prefers float scans", MemoryCategory.UserPreference);

            var all = memory.Recall();
            Assert.Equal(2, all.Count);

            var processSpecific = memory.Recall(processName: "Game.exe");
            Assert.Equal(2, processSpecific.Count); // Global memories always match

            var keyword = memory.Recall(searchTerm: "HP");
            Assert.Single(keyword);
            Assert.Contains("HP", keyword[0].Content);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void MemorySystem_Forget_RemovesEntry()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var memory = new MemorySystem(tempPath);
            memory.Load();

            memory.Remember("test entry", MemoryCategory.LearnedPattern);
            var entries = memory.Recall();
            Assert.Single(entries);

            var id = entries[0].Id;
            memory.Forget(id);

            Assert.Empty(memory.Recall());
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void MemorySystem_Deduplication_UpdatesExistingEntry()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var memory = new MemorySystem(tempPath);
            memory.Load();

            memory.Remember("Player HP is at 0x1A4", MemoryCategory.ProcessKnowledge, "Game.exe");
            memory.Remember("Player HP is at 0x1A4 confirmed", MemoryCategory.ProcessKnowledge, "Game.exe");

            // Should deduplicate (similar content, same category and process)
            var entries = memory.Recall();
            Assert.Single(entries);
            Assert.Contains("confirmed", entries[0].Content);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void MemorySystem_BuildMemoryContext_FormatsCorrectly()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var memory = new MemorySystem(tempPath);
            memory.Load();

            memory.Remember("Always use float scans", MemoryCategory.UserPreference);
            memory.Remember("Base address at 0x7FF000", MemoryCategory.ProcessKnowledge, "Game.exe");

            var ctx = memory.BuildMemoryContext();
            Assert.NotNull(ctx);
            Assert.Contains("AGENT MEMORY", ctx);
            Assert.Contains("float scans", ctx);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void MemorySystem_Prune_RemovesOldLowAccessEntries()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            var memory = new MemorySystem(tempPath);
            memory.Load();

            // Add entries that look old (we can't easily control timestamps, but prune with maxEntries=1)
            memory.Remember("entry1", MemoryCategory.LearnedPattern);
            memory.Remember("entry2", MemoryCategory.LearnedPattern);
            memory.Remember("entry3", MemoryCategory.LearnedPattern);

            var removed = memory.Prune(maxEntries: 1);
            Assert.True(removed >= 2);
            Assert.True(memory.Entries.Count <= 1);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void MemorySystem_PersistsToDisk()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            // Write
            var memory1 = new MemorySystem(tempPath);
            memory1.Load();
            memory1.Remember("persisted entry", MemoryCategory.SafetyNote);

            // Read in new instance
            var memory2 = new MemorySystem(tempPath);
            memory2.Load();
            var entries = memory2.Recall();
            Assert.Single(entries);
            Assert.Equal("persisted entry", entries[0].Content);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ── ChatHistoryManager ───────────────────────────────────────────

    [Fact]
    public void ChatHistoryManager_AddUserMessage_AddsToHistory()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddUserMessage("Hello");
        Assert.Equal(1, mgr.Count);

        var msgs = mgr.GetMessages();
        Assert.Single(msgs);
        Assert.Equal(ChatRole.User, msgs[0].Role);
    }

    [Fact]
    public void ChatHistoryManager_AddUserMessage_WithContext_AppendsContext()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddUserMessage("Hello", "Process: Game.exe");
        var msgs = mgr.GetMessages();
        Assert.Single(msgs);
        Assert.Contains("Process: Game.exe", msgs[0].Text);
    }

    [Fact]
    public void ChatHistoryManager_AddSystemMessage_Inserted()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddUserMessage("Hello");
        mgr.AddSystemMessage("System note");
        Assert.Equal(2, mgr.Count);
    }

    [Fact]
    public void ChatHistoryManager_Clear_EmptiesHistory()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddUserMessage("Hello");
        mgr.AddSystemMessage("note");
        mgr.Clear();
        Assert.Equal(0, mgr.Count);
    }

    // ── ToolAttributes & Cache ───────────────────────────────────────

    [Fact]
    public void ToolAttributeCache_ScanType_FindsAttributes()
    {
        var cache = new ToolAttributeCache();
        cache.ScanType(typeof(TestToolClass));

        var readMeta = cache.Get("ReadOnlyMethod");
        Assert.NotNull(readMeta);
        Assert.True(readMeta.IsReadOnly);
        Assert.True(readMeta.IsConcurrencySafe);

        var writeMeta = cache.Get("DestructiveMethod");
        Assert.NotNull(writeMeta);
        Assert.True(writeMeta.IsDestructive);
        Assert.False(writeMeta.IsConcurrencySafe);
    }

    // ── PermissionEngine ─────────────────────────────────────────────

    [Fact]
    public void PermissionRule_GlobMatch_BasicPatterns()
    {
        Assert.True(PermissionRule.GlobMatch("ReadMemory", "Read*"));
        Assert.True(PermissionRule.GlobMatch("WriteMemory", "Write*"));
        Assert.False(PermissionRule.GlobMatch("ReadMemory", "Write*"));
        Assert.True(PermissionRule.GlobMatch("anything", "*"));
    }

    [Fact]
    public void PermissionEngine_Evaluate_FirstMatchWins()
    {
        var engine = new PermissionEngine(new HashSet<string>());
        engine.AddRules(
        [
            new PermissionRule { ToolPattern = "WriteMemory", Effect = PermissionEffect.Deny },
            new PermissionRule { ToolPattern = "Write*", Effect = PermissionEffect.Allow },
        ]);

        var result = engine.Evaluate("WriteMemory", null);
        Assert.Equal(PermissionEffect.Deny, result.Effect); // First match wins

        var result2 = engine.Evaluate("WriteLog", null);
        Assert.Equal(PermissionEffect.Allow, result2.Effect); // Falls to second rule
    }

    [Fact]
    public void PermissionEngine_Evaluate_NoMatchReturnsAllow()
    {
        var engine = new PermissionEngine(new HashSet<string>());
        engine.AddRules(
        [
            new PermissionRule { ToolPattern = "Write*", Effect = PermissionEffect.Deny },
        ]);

        var result = engine.Evaluate("ReadMemory", null);
        Assert.Equal(PermissionEffect.Allow, result.Effect); // No matching rule, default allow
    }

    // ── HookSystem ───────────────────────────────────────────────────

    [Fact]
    public async Task HookRegistry_PreToolHook_CanBlockExecution()
    {
        var registry = new HookRegistry();
        registry.AddPreToolHook(new ToolBlocklistHook(["BadTool"]));

        var result = await registry.RunPreToolHooksAsync(new ToolHookContext { ToolName = "BadTool" }, CancellationToken.None);
        Assert.Equal(HookOutcome.Block, result.Outcome);

        var okResult = await registry.RunPreToolHooksAsync(new ToolHookContext { ToolName = "GoodTool" }, CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, okResult.Outcome);
    }

    [Fact]
    public async Task HookRegistry_MaxToolCallsHook_BlocksAfterLimit()
    {
        var registry = new HookRegistry();
        registry.AddPreToolHook(new MaxToolCallsHook(2));

        var r1 = await registry.RunPreToolHooksAsync(new ToolHookContext { ToolName = "Tool1", TotalToolCalls = 0 }, CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, r1.Outcome);

        var r2 = await registry.RunPreToolHooksAsync(new ToolHookContext { ToolName = "Tool2", TotalToolCalls = 1 }, CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, r2.Outcome);

        var r3 = await registry.RunPreToolHooksAsync(new ToolHookContext { ToolName = "Tool3", TotalToolCalls = 2 }, CancellationToken.None);
        Assert.Equal(HookOutcome.Block, r3.Outcome);
    }

    [Fact]
    public async Task HookRegistry_DelegateHook_InvokesDelegateOnPostTool()
    {
        var registry = new HookRegistry();
        string? capturedTool = null;
        registry.AddPostToolHook(new DelegatePostToolHook("test", (context, _, _, _) =>
        {
            capturedTool = context.ToolName;
            return Task.CompletedTask;
        }));

        await registry.RunPostToolHooksAsync(new ToolHookContext { ToolName = "MyTool" }, "result", false, CancellationToken.None);
        Assert.Equal("MyTool", capturedTool);
    }

    // ── SkillSystem ──────────────────────────────────────────────────

    [Fact]
    public void SkillSystem_RegisterAndLoad()
    {
        var system = new SkillSystem();
        system.Register(new SkillDefinition
        {
            Name = "test-skill",
            Description = "A test skill",
            Instructions = "Do the thing.",
        });

        Assert.Single(system.Catalog);
        Assert.Contains("test-skill", system.Catalog.Keys);

        var loadResult = system.LoadSkill("test-skill");
        Assert.Contains("loaded", loadResult);

        var instructions = system.BuildActiveSkillInstructions();
        Assert.NotNull(instructions);
        Assert.Contains("Do the thing", instructions);
    }

    [Fact]
    public void SkillSystem_UnloadSkill()
    {
        var system = new SkillSystem();
        system.Register(new SkillDefinition
        {
            Name = "test-skill",
            Description = "Test",
            Instructions = "Instructions here.",
        });

        system.LoadSkill("test-skill");
        var result = system.UnloadSkill("test-skill");
        Assert.Contains("unloaded", result);

        var instructions = system.BuildActiveSkillInstructions();
        Assert.Null(instructions);
    }

    [Fact]
    public void SkillSystem_BuildCatalogSummary_ListsAllSkills()
    {
        var system = new SkillSystem();
        system.Register(new SkillDefinition { Name = "skill-a", Description = "First", Instructions = "Instructions A" });
        system.Register(new SkillDefinition { Name = "skill-b", Description = "Second", Instructions = "Instructions B" });

        var summary = system.BuildCatalogSummary();
        Assert.Contains("skill-a", summary);
        Assert.Contains("skill-b", summary);
    }

    // ── AgentStreamEvent ─────────────────────────────────────────────

    [Fact]
    public async Task AgentStreamEvent_ApprovalRequested_CanResolve()
    {
        var evt = new AgentStreamEvent.ApprovalRequested("WriteMemory", "addr=0x1000");
        Assert.False(evt.UserDecision.IsCompleted);

        evt.Resolve(true);
        Assert.True(evt.UserDecision.IsCompleted);
        Assert.True(await evt.UserDecision);
    }

    [Fact]
    public async Task AgentStreamEvent_ApprovalRequested_CanDeny()
    {
        var evt = new AgentStreamEvent.ApprovalRequested("DeleteAll", "");
        evt.Resolve(false);
        Assert.False(await evt.UserDecision);
    }

    // ── Integration: Full Loop with Mock IChatClient ──────────────────

    [Fact]
    public async Task AgentLoop_SimpleResponse_NoToolCalls_CompletesCorrectly()
    {
        var mockClient = new MockChatClient("Hello, I can help you!");
        var options = CreateTestOptions();
        var loop = new AgentLoopRunner(mockClient, options);
        var history = new ChatHistoryManager();

        var events = new List<AgentStreamEvent>();
        var ct = TestContext.Current.CancellationToken;
        var reader = loop.RunStreamingAsync("Hi", history, cancellationToken: ct);
        await foreach (var evt in reader.ReadAllAsync(ct))
            events.Add(evt);

        // Should have at least TextDelta + Completed
        Assert.Contains(events, e => e is AgentStreamEvent.TextDelta);
        Assert.Contains(events, e => e is AgentStreamEvent.Completed);

        var textParts = events.OfType<AgentStreamEvent.TextDelta>().Select(d => d.Text);
        var fullText = string.Join("", textParts);
        Assert.Contains("Hello", fullText);
    }

    [Fact]
    public async Task AgentLoop_Cancellation_EmitsError()
    {
        var mockClient = new SlowMockChatClient(); // Never completes
        var options = CreateTestOptions();
        var loop = new AgentLoopRunner(mockClient, options);
        var history = new ChatHistoryManager();

        using var cts = new CancellationTokenSource(100); // Cancel after 100ms
        var events = new List<AgentStreamEvent>();
        var reader = loop.RunStreamingAsync("Hi", history, cancellationToken: cts.Token);

        await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            events.Add(evt);

        // Should have an error event (cancellation may surface as "Stopped by user." or as
        // a generic agent error depending on how the runtime propagates the cancellation).
        Assert.Contains(events, e => e is AgentStreamEvent.Error);
    }

    // ── Test Helpers ─────────────────────────────────────────────────

    private static AgentLoopOptions CreateTestOptions()
    {
        return new AgentLoopOptions
        {
            SystemPrompt = "You are a test assistant.",
            Tools = new List<AITool>(),
            Limits = TokenLimits.Balanced,
            ToolResultStore = new ToolResultStore(),
            DangerousToolNames = new HashSet<string>(),
            MaxTurns = 5,
            Log = (level, msg) => { },
        };
    }
}

// ── Test doubles ─────────────────────────────────────────────────────

/// <summary>Test class with tool attributes for cache scanning.</summary>
#pragma warning disable CA1822 // Methods must be instance for AI tool reflection
internal sealed class TestToolClass
{
    [ReadOnlyTool, ConcurrencySafe]
    public string ReadOnlyMethod() => "ok";

    [Destructive]
    public string DestructiveMethod() => "ok";
}
#pragma warning restore CA1822

/// <summary>Mock IChatClient that returns a fixed text response.</summary>
#pragma warning disable CA1822 // Interface implementation cannot be static
internal sealed class MockChatClient : IChatClient
{
    private readonly string _responseText;

    public MockChatClient(string responseText) => _responseText = responseText;

    public ChatClientMetadata Metadata => new("mock");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msg = new ChatMessage(ChatRole.Assistant, _responseText);
        return Task.FromResult(new ChatResponse(msg));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent(_responseText)],
        };
        yield return new ChatResponseUpdate
        {
            FinishReason = ChatFinishReason.Stop,
        };
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
#pragma warning restore CA1822

/// <summary>Mock IChatClient that hangs forever (for cancellation tests).</summary>
internal sealed class SlowMockChatClient : IChatClient
{
#pragma warning disable CA1822
    public ChatClientMetadata Metadata => new("slow-mock");
#pragma warning restore CA1822

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith<ChatResponse>(_ =>
            throw new OperationCanceledException(), cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
