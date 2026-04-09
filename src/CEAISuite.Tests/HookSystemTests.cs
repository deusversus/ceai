using CEAISuite.Application.AgentLoop;
using Microsoft.Extensions.AI;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for <see cref="HookRegistry"/>: pre-tool hooks (allow/block),
/// post-tool hooks, lifecycle hooks, delegate hooks, failure policy, and clearing.
/// </summary>
public class HookSystemTests
{
    private static ToolHookContext MakeContext(string toolName, int totalCalls = 0) => new()
    {
        ToolName = toolName,
        TotalToolCalls = totalCalls,
        TurnNumber = 1,
    };

    // ── PreToolHook: Block ──────────────────────────────────────────

    [Fact]
    public async Task RunPreToolHooksAsync_BlocklistHook_BlocksMatchingTool()
    {
        var registry = new HookRegistry();
        registry.AddPreToolHook(new ToolBlocklistHook(["DangerousTool"]));

        var result = await registry.RunPreToolHooksAsync(MakeContext("DangerousTool"), CancellationToken.None);
        Assert.Equal(HookOutcome.Block, result.Outcome);
        Assert.Contains("blocklist", result.Message);
    }

    [Fact]
    public async Task RunPreToolHooksAsync_BlocklistHook_AllowsNonMatchingTool()
    {
        var registry = new HookRegistry();
        registry.AddPreToolHook(new ToolBlocklistHook(["DangerousTool"]));

        var result = await registry.RunPreToolHooksAsync(MakeContext("SafeTool"), CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, result.Outcome);
    }

    [Fact]
    public async Task RunPreToolHooksAsync_BlocklistHook_DynamicAddRemove()
    {
        var hook = new ToolBlocklistHook([]);
        var registry = new HookRegistry();
        registry.AddPreToolHook(hook);

        // Initially allowed
        var r1 = await registry.RunPreToolHooksAsync(MakeContext("MyTool"), CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, r1.Outcome);

        // Block it
        hook.Add("MyTool");
        var r2 = await registry.RunPreToolHooksAsync(MakeContext("MyTool"), CancellationToken.None);
        Assert.Equal(HookOutcome.Block, r2.Outcome);

        // Unblock it
        hook.Remove("MyTool");
        var r3 = await registry.RunPreToolHooksAsync(MakeContext("MyTool"), CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, r3.Outcome);
    }

    // ── PreToolHook: MaxToolCalls ───────────────────────────────────

    [Fact]
    public async Task RunPreToolHooksAsync_MaxToolCalls_BlocksWhenExceeded()
    {
        var registry = new HookRegistry();
        registry.AddPreToolHook(new MaxToolCallsHook(5));

        var r1 = await registry.RunPreToolHooksAsync(MakeContext("Tool", totalCalls: 4), CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, r1.Outcome);

        var r2 = await registry.RunPreToolHooksAsync(MakeContext("Tool", totalCalls: 5), CancellationToken.None);
        Assert.Equal(HookOutcome.Block, r2.Outcome);
    }

    // ── PreToolHook: Delegate ───────────────────────────────────────

    [Fact]
    public async Task RunPreToolHooksAsync_DelegateHook_Invoked()
    {
        var registry = new HookRegistry();
        string? captured = null;

        registry.AddPreToolHook(new DelegatePreToolHook("test-hook", (ctx, ct) =>
        {
            captured = ctx.ToolName;
            return Task.FromResult(HookResult.Continue());
        }));

        await registry.RunPreToolHooksAsync(MakeContext("TestTool"), CancellationToken.None);
        Assert.Equal("TestTool", captured);
    }

    [Fact]
    public async Task RunPreToolHooksAsync_DelegateHook_CanBlock()
    {
        var registry = new HookRegistry();
        registry.AddPreToolHook(new DelegatePreToolHook("blocker", (ctx, ct) =>
            Task.FromResult(HookResult.Blocked("Not allowed"))));

        var result = await registry.RunPreToolHooksAsync(MakeContext("AnyTool"), CancellationToken.None);
        Assert.Equal(HookOutcome.Block, result.Outcome);
        Assert.Equal("Not allowed", result.Message);
    }

    // ── PreToolHook: Tool pattern matching ──────────────────────────

    [Fact]
    public async Task RunPreToolHooksAsync_ToolPattern_OnlyMatchingToolsAffected()
    {
        var registry = new HookRegistry();
        bool invoked = false;

        registry.AddPreToolHook(new DelegatePreToolHook("write-guard", (ctx, ct) =>
        {
            invoked = true;
            return Task.FromResult(HookResult.Blocked("write blocked"));
        })
        { ToolPattern = "Write*" });

        // ReadMemory should not trigger the hook
        var r1 = await registry.RunPreToolHooksAsync(MakeContext("ReadMemory"), CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, r1.Outcome);
        Assert.False(invoked);

        // WriteMemory should trigger
        var r2 = await registry.RunPreToolHooksAsync(MakeContext("WriteMemory"), CancellationToken.None);
        Assert.Equal(HookOutcome.Block, r2.Outcome);
        Assert.True(invoked);
    }

    // ── PreToolHook: Failure policy ─────────────────────────────────

    [Fact]
    public async Task RunPreToolHooksAsync_HookThrows_IgnorePolicy_ContinuesExecution()
    {
        var registry = new HookRegistry();
        registry.AddPreToolHook(new DelegatePreToolHook("crashing", (ctx, ct) =>
            throw new InvalidOperationException("hook crashed"))
        { FailurePolicy = HookFailurePolicy.Ignore });

        var result = await registry.RunPreToolHooksAsync(MakeContext("Tool"), CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, result.Outcome);
    }

    [Fact]
    public async Task RunPreToolHooksAsync_HookThrows_BlockPolicy_BlocksExecution()
    {
        var registry = new HookRegistry();
        registry.AddPreToolHook(new DelegatePreToolHook("crashing", (ctx, ct) =>
            throw new InvalidOperationException("hook crashed"))
        { FailurePolicy = HookFailurePolicy.Block });

        var result = await registry.RunPreToolHooksAsync(MakeContext("Tool"), CancellationToken.None);
        Assert.Equal(HookOutcome.Block, result.Outcome);
        Assert.Contains("failed", result.Message);
    }

    // ── PreToolHook: Multiple hooks, first block wins ───────────────

    [Fact]
    public async Task RunPreToolHooksAsync_MultipleHooks_FirstBlockWins()
    {
        var registry = new HookRegistry();
        var secondInvoked = false;

        registry.AddPreToolHook(new DelegatePreToolHook("blocker", (_, _) =>
            Task.FromResult(HookResult.Blocked("first blocks"))));
        registry.AddPreToolHook(new DelegatePreToolHook("after", (_, _) =>
        {
            secondInvoked = true;
            return Task.FromResult(HookResult.Continue());
        }));

        var result = await registry.RunPreToolHooksAsync(MakeContext("Tool"), CancellationToken.None);
        Assert.Equal(HookOutcome.Block, result.Outcome);
        Assert.False(secondInvoked); // Second hook should not run
    }

    // ── PostToolHook ────────────────────────────────────────────────

    [Fact]
    public async Task RunPostToolHooksAsync_DelegateHook_ReceivesResult()
    {
        var registry = new HookRegistry();
        string? capturedResult = null;
        bool capturedIsError = true;

        registry.AddPostToolHook(new DelegatePostToolHook("logger", (ctx, result, isError, ct) =>
        {
            capturedResult = result;
            capturedIsError = isError;
            return Task.CompletedTask;
        }));

        await registry.RunPostToolHooksAsync(MakeContext("ReadMemory"), "0x1A4: 100", false, CancellationToken.None);
        Assert.Equal("0x1A4: 100", capturedResult);
        Assert.False(capturedIsError);
    }

    [Fact]
    public async Task RunPostToolHooksAsync_ToolPattern_OnlyMatchingToolsFire()
    {
        var registry = new HookRegistry();
        int callCount = 0;

        registry.AddPostToolHook(new DelegatePostToolHook("write-logger", (_, _, _, _) =>
        {
            callCount++;
            return Task.CompletedTask;
        })
        { ToolPattern = "Write*" });

        await registry.RunPostToolHooksAsync(MakeContext("ReadMemory"), "ok", false, CancellationToken.None);
        Assert.Equal(0, callCount);

        await registry.RunPostToolHooksAsync(MakeContext("WriteMemory"), "ok", false, CancellationToken.None);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task RunPostToolHooksAsync_HookThrows_DoesNotCrash()
    {
        var registry = new HookRegistry();
        registry.AddPostToolHook(new DelegatePostToolHook("crasher", (_, _, _, _) =>
            throw new InvalidOperationException("boom")));

        // Should not throw
        await registry.RunPostToolHooksAsync(MakeContext("Tool"), "result", false, CancellationToken.None);
    }

    // ── ToolAuditHook ───────────────────────────────────────────────

    [Fact]
    public async Task ToolAuditHook_CapturesAllFields()
    {
        var registry = new HookRegistry();
        string? auditTool = null;
        string? auditResult = null;
        bool auditIsError = false;

        registry.AddPostToolHook(new ToolAuditHook((tool, args, result, isError) =>
        {
            auditTool = tool;
            auditResult = result;
            auditIsError = isError;
        }));

        await registry.RunPostToolHooksAsync(
            new ToolHookContext
            {
                ToolName = "ReadMemory",
                Arguments = new Dictionary<string, object?> { ["addr"] = "0x1000" },
            },
            "value: 42", false, CancellationToken.None);

        Assert.Equal("ReadMemory", auditTool);
        Assert.Equal("value: 42", auditResult);
        Assert.False(auditIsError);
    }

    // ── PostToolFailureHook ─────────────────────────────────────────

    [Fact]
    public async Task RunPostToolFailureHooksAsync_ReturnsAction()
    {
        var registry = new HookRegistry();
        registry.AddPostToolFailureHook(new StubPostToolFailureHook("retry-helper",
            new PostFailureAction { RetryHint = "Try smaller range", SuppressDetailedError = true }));

        var action = await registry.RunPostToolFailureHooksAsync(
            MakeContext("ReadMemory"), "access denied", false, CancellationToken.None);

        Assert.NotNull(action);
        Assert.Equal("Try smaller range", action.RetryHint);
        Assert.True(action.SuppressDetailedError);
    }

    [Fact]
    public async Task RunPostToolFailureHooksAsync_NoHooks_ReturnsNull()
    {
        var registry = new HookRegistry();
        var action = await registry.RunPostToolFailureHooksAsync(
            MakeContext("Tool"), "error", false, CancellationToken.None);
        Assert.Null(action);
    }

    // ── Stop Hook ───────────────────────────────────────────────────

    [Fact]
    public async Task RunStopHooksAsync_NoHooks_ReturnsContinue()
    {
        var registry = new HookRegistry();
        var ctx = new StopHookContext { TurnCount = 5, TotalToolCalls = 10, LastAssistantText = "done" };

        var result = await registry.RunStopHooksAsync(ctx, CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, result.Outcome);
    }

    [Fact]
    public async Task RunStopHooksAsync_BlockingHook_ForceContinue()
    {
        var registry = new HookRegistry();
        registry.AddStopHook(new StubStopHook("force-continue", HookOutcome.Block));

        var ctx = new StopHookContext { TurnCount = 1, TotalToolCalls = 0, LastAssistantText = "incomplete" };
        var result = await registry.RunStopHooksAsync(ctx, CancellationToken.None);
        Assert.Equal(HookOutcome.Block, result.Outcome);
    }

    // ── Session Lifecycle Hooks ─────────────────────────────────────

    [Fact]
    public async Task RunSessionStartHooksAsync_InvokesHook()
    {
        var registry = new HookRegistry();
        bool invoked = false;
        registry.AddSessionStartHook(new StubSessionHook("startup", () => invoked = true));

        var ctx = new SessionLifecycleContext { SessionId = "test-session", IsNewSession = true };
        await registry.RunSessionStartHooksAsync(ctx, CancellationToken.None);
        Assert.True(invoked);
    }

    [Fact]
    public async Task RunSessionEndHooksAsync_InvokesHook()
    {
        var registry = new HookRegistry();
        bool invoked = false;
        registry.AddSessionEndHook(new StubSessionHook("shutdown", () => invoked = true));

        var ctx = new SessionLifecycleContext { SessionId = "test-session", IsNewSession = false };
        await registry.RunSessionEndHooksAsync(ctx, CancellationToken.None);
        Assert.True(invoked);
    }

    // ── Subagent Lifecycle Hooks ────────────────────────────────────

    [Fact]
    public async Task RunSubagentStartHooksAsync_InvokesHook()
    {
        var registry = new HookRegistry();
        string? capturedId = null;
        registry.AddSubagentStartHook(new StubSubagentHook("tracker", ctx => capturedId = ctx.SubagentId));

        var ctx = new SubagentLifecycleContext
        {
            SubagentId = "sub-1",
            Description = "test task",
            IsStart = true,
        };
        await registry.RunSubagentStartHooksAsync(ctx, CancellationToken.None);
        Assert.Equal("sub-1", capturedId);
    }

    [Fact]
    public async Task RunSubagentEndHooksAsync_InvokesHook()
    {
        var registry = new HookRegistry();
        bool invoked = false;
        registry.AddSubagentEndHook(new StubSubagentHook("end-tracker", _ => invoked = true));

        var ctx = new SubagentLifecycleContext
        {
            SubagentId = "sub-1",
            Description = "test task",
            IsStart = false,
            Duration = TimeSpan.FromSeconds(5),
            ToolCallCount = 3,
        };
        await registry.RunSubagentEndHooksAsync(ctx, CancellationToken.None);
        Assert.True(invoked);
    }

    // ── PreLlmHook ──────────────────────────────────────────────────

    [Fact]
    public async Task RunPreLlmHooksAsync_NoHooks_ReturnsContinue()
    {
        var registry = new HookRegistry();
        var ctx = new PreLlmContext
        {
            Messages = new List<ChatMessage>(),
            Options = new ChatOptions(),
            TurnNumber = 1,
        };

        var result = await registry.RunPreLlmHooksAsync(ctx, CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, result.Outcome);
    }

    // ── Clear ───────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_RemovesAllHooks()
    {
        var registry = new HookRegistry();
        registry.AddPreToolHook(new ToolBlocklistHook(["Block"]));
        registry.AddPostToolHook(new DelegatePostToolHook("test", (_, _, _, _) => Task.CompletedTask));

        registry.Clear();

        // Pre-tool hook should be gone
        var result = await registry.RunPreToolHooksAsync(MakeContext("Block"), CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, result.Outcome);

        Assert.Empty(registry.PreToolHooks);
        Assert.Empty(registry.PostToolHooks);
    }

    // ── Hook Properties ─────────────────────────────────────────────

    [Fact]
    public void HookResult_StaticFactories_SetCorrectValues()
    {
        var cont = HookResult.Continue();
        Assert.Equal(HookOutcome.Continue, cont.Outcome);
        Assert.Null(cont.Message);

        var blocked = HookResult.Blocked("reason");
        Assert.Equal(HookOutcome.Block, blocked.Outcome);
        Assert.Equal("reason", blocked.Message);

        var allowed = HookResult.Allowed("ok");
        Assert.Equal(HookOutcome.Allow, allowed.Outcome);
        Assert.Equal("ok", allowed.Message);
    }

    [Fact]
    public void HookResult_Continue_WithModifiedArgs()
    {
        var args = new Dictionary<string, object?> { ["addr"] = "0x2000" };
        var result = HookResult.Continue(args);
        Assert.Equal(HookOutcome.Continue, result.Outcome);
        Assert.NotNull(result.ModifiedArguments);
        Assert.Equal("0x2000", result.ModifiedArguments["addr"]);
    }

    // ── Stub hook implementations ───────────────────────────────────

    private sealed class StubPostToolFailureHook : PostToolFailureHook
    {
        private readonly PostFailureAction? _action;
        public StubPostToolFailureHook(string name, PostFailureAction? action) : base(name) => _action = action;
        public override Task<PostFailureAction?> ExecuteAsync(
            ToolHookContext context, string errorMessage, bool wasInterrupted, CancellationToken ct)
            => Task.FromResult(_action);
    }

    private sealed class StubStopHook : StopHook
    {
        private readonly HookOutcome _outcome;
        public StubStopHook(string name, HookOutcome outcome) : base(name) => _outcome = outcome;
        public override Task<HookResult> ExecuteAsync(StopHookContext ctx, CancellationToken ct)
            => Task.FromResult(new HookResult { Outcome = _outcome });
    }

    private sealed class StubSessionHook : SessionLifecycleHook
    {
        private readonly Action _onExecute;
        public StubSessionHook(string name, Action onExecute) { Name = name; _onExecute = onExecute; }
        public override Task ExecuteAsync(SessionLifecycleContext context, CancellationToken ct)
        {
            _onExecute();
            return Task.CompletedTask;
        }
    }

    private sealed class StubSubagentHook : SubagentLifecycleHook
    {
        private readonly Action<SubagentLifecycleContext> _onExecute;
        public StubSubagentHook(string name, Action<SubagentLifecycleContext> onExecute) { Name = name; _onExecute = onExecute; }
        public override Task ExecuteAsync(SubagentLifecycleContext ctx, CancellationToken ct)
        {
            _onExecute(ctx);
            return Task.CompletedTask;
        }
    }
}
