using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
using Microsoft.Extensions.AI;
using AgentLoopRunner = CEAISuite.Application.AgentLoop.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// End-to-end integration tests for the agent loop: full tool-calling loop,
/// dangerous tool approval, tool error handling, multi-turn conversations,
/// and max turn limits.
/// </summary>
public class EndToEndAgentLoopTests
{
    // ── A. Full tool-calling loop ────────────────────────────────────

    [Fact]
    public async Task AgentLoop_ToolCallingLoop_ExecutesToolAndReturnsResult()
    {
        // Arrange: tool that returns a known result
        int toolCallCount = 0;
        var tool = AIFunctionFactory.Create(() =>
        {
            toolCallCount++;
            return "42";
        }, "get_answer", "Returns the answer to everything.");

        var mockClient = new ToolCallingMockChatClient(
            toolCallSequence:
            [
                // Turn 1: LLM requests get_answer tool
                [new FunctionCallContent("call_1", "get_answer")],
            ],
            finalResponse: "The answer is 42.");

        var options = CreateTestOptions([tool]);
        var loop = new AgentLoopRunner(mockClient, options);
        var history = new ChatHistoryManager();

        // Act
        var events = await CollectEventsAsync(loop, history, "What is the answer?");

        // Assert: tool was called
        Assert.Equal(1, toolCallCount);

        // Assert: event sequence includes ToolCallStarted, ToolCallCompleted, TextDelta, Completed
        Assert.Contains(events, e => e is AgentStreamEvent.ToolCallStarted s && s.ToolName == "get_answer");
        Assert.Contains(events, e => e is AgentStreamEvent.ToolCallCompleted c && c.ToolName == "get_answer");
        Assert.Contains(events, e => e is AgentStreamEvent.TextDelta);
        Assert.Contains(events, e => e is AgentStreamEvent.Completed);

        // Assert: final text contains expected response
        var fullText = string.Join("", events.OfType<AgentStreamEvent.TextDelta>().Select(d => d.Text));
        Assert.Contains("42", fullText);

        // Assert: Completed reports 1 tool call
        var completed = events.OfType<AgentStreamEvent.Completed>().Single();
        Assert.Equal(1, completed.ToolCallCount);
    }

    // ── B. Dangerous tool approval flow ─────────────────────────────

    [Fact]
    public async Task AgentLoop_DangerousTool_EmitsApprovalAndCompletesOnApprove()
    {
        // Arrange: tool marked as dangerous
        int toolCallCount = 0;
        var tool = AIFunctionFactory.Create(() =>
        {
            toolCallCount++;
            return "memory_written";
        }, "write_memory", "Writes to process memory.");

        var mockClient = new ToolCallingMockChatClient(
            toolCallSequence:
            [
                [new FunctionCallContent("call_1", "write_memory")],
            ],
            finalResponse: "Memory written successfully.");

        var options = CreateTestOptions([tool], dangerousTools: ["write_memory"]);
        var loop = new AgentLoopRunner(mockClient, options);
        var history = new ChatHistoryManager();

        // Act: run loop and auto-approve dangerous tool
        var events = new List<AgentStreamEvent>();
        var ct = TestContext.Current.CancellationToken;
        var reader = loop.RunStreamingAsync("Write memory", history, cancellationToken: ct);
        await foreach (var evt in reader.ReadAllAsync(ct))
        {
            events.Add(evt);
            // Auto-approve when we see the approval request
            if (evt is AgentStreamEvent.ApprovalRequested approval)
            {
                Assert.Equal("write_memory", approval.ToolName);
                approval.Resolve(true);
            }
        }

        // Assert: approval was requested
        Assert.Contains(events, e => e is AgentStreamEvent.ApprovalRequested);

        // Assert: tool was actually executed after approval
        Assert.Equal(1, toolCallCount);

        // Assert: loop completed
        Assert.Contains(events, e => e is AgentStreamEvent.Completed);
    }

    [Fact]
    public async Task AgentLoop_DangerousTool_DenialProducesErrorResult()
    {
        // Arrange: tool marked as dangerous
        int toolCallCount = 0;
        var tool = AIFunctionFactory.Create(() =>
        {
            toolCallCount++;
            return "should_not_run";
        }, "dangerous_tool", "A dangerous operation.");

        var mockClient = new ToolCallingMockChatClient(
            toolCallSequence:
            [
                [new FunctionCallContent("call_1", "dangerous_tool")],
            ],
            finalResponse: "Tool was denied.");

        var options = CreateTestOptions([tool], dangerousTools: ["dangerous_tool"]);
        var loop = new AgentLoopRunner(mockClient, options);
        var history = new ChatHistoryManager();

        // Act: run loop and deny the dangerous tool
        var events = new List<AgentStreamEvent>();
        var ct = TestContext.Current.CancellationToken;
        var reader = loop.RunStreamingAsync("Do the thing", history, cancellationToken: ct);
        await foreach (var evt in reader.ReadAllAsync(ct))
        {
            events.Add(evt);
            if (evt is AgentStreamEvent.ApprovalRequested approval)
                approval.Resolve(false);
        }

        // Assert: tool was NOT executed
        Assert.Equal(0, toolCallCount);

        // Assert: loop still completed (LLM gets the denial as a tool result)
        Assert.Contains(events, e => e is AgentStreamEvent.Completed);
    }

    // ── C. Tool error handling ──────────────────────────────────────

    [Fact]
    public async Task AgentLoop_ToolThrowsException_ContinuesWithErrorResult()
    {
        // Arrange: tool that throws
        var tool = AIFunctionFactory.Create(new Func<string>(() =>
        {
            throw new InvalidOperationException("Something went wrong");
        }), "failing_tool", "A tool that always fails.");

        var mockClient = new ToolCallingMockChatClient(
            toolCallSequence:
            [
                [new FunctionCallContent("call_1", "failing_tool")],
            ],
            finalResponse: "The tool failed, here is the error summary.");

        var options = CreateTestOptions([tool]);
        var loop = new AgentLoopRunner(mockClient, options);
        var history = new ChatHistoryManager();

        // Act
        var events = await CollectEventsAsync(loop, history, "Run failing tool");

        // Assert: ToolCallCompleted event exists (with error info)
        var toolCompleted = events.OfType<AgentStreamEvent.ToolCallCompleted>()
            .FirstOrDefault(c => c.ToolName == "failing_tool");
        Assert.NotNull(toolCompleted);

        // Assert: loop made a second LLM call (final response includes text)
        var fullText = string.Join("", events.OfType<AgentStreamEvent.TextDelta>().Select(d => d.Text));
        Assert.Contains("error summary", fullText);

        // Assert: loop completed
        Assert.Contains(events, e => e is AgentStreamEvent.Completed);

        // Assert: mock client was called twice (tool call + final answer)
        Assert.Equal(2, mockClient.CallCount);
    }

    // ── D. Multi-turn conversation ──────────────────────────────────

    [Fact]
    public async Task AgentLoop_MultiTurnToolCalls_CompletesAllTurnsCorrectly()
    {
        // Arrange: 3 tools, each called once per turn
        int tool1Calls = 0, tool2Calls = 0, tool3Calls = 0;
        var tool1 = AIFunctionFactory.Create(() => { tool1Calls++; return "result_1"; },
            "step_one", "First step.");
        var tool2 = AIFunctionFactory.Create(() => { tool2Calls++; return "result_2"; },
            "step_two", "Second step.");
        var tool3 = AIFunctionFactory.Create(() => { tool3Calls++; return "result_3"; },
            "step_three", "Third step.");

        var mockClient = new ToolCallingMockChatClient(
            toolCallSequence:
            [
                // Turn 1: call step_one
                [new FunctionCallContent("call_1", "step_one")],
                // Turn 2: call step_two
                [new FunctionCallContent("call_2", "step_two")],
                // Turn 3: call step_three
                [new FunctionCallContent("call_3", "step_three")],
            ],
            finalResponse: "All three steps completed.");

        var options = CreateTestOptions([tool1, tool2, tool3]);
        var loop = new AgentLoopRunner(mockClient, options);
        var history = new ChatHistoryManager();

        // Act
        var events = await CollectEventsAsync(loop, history, "Run all steps");

        // Assert: all tools called
        Assert.Equal(1, tool1Calls);
        Assert.Equal(1, tool2Calls);
        Assert.Equal(1, tool3Calls);

        // Assert: Completed event reports 3 total tool calls
        var completed = events.OfType<AgentStreamEvent.Completed>().Single();
        Assert.Equal(3, completed.ToolCallCount);

        // Assert: 4 LLM calls (3 tool turns + 1 final)
        Assert.Equal(4, mockClient.CallCount);

        // Assert: history accumulated messages for each turn
        // Expected: user message + 3x(assistant+tool) + final assistant = at least 7+
        var msgs = history.GetMessages();
        Assert.True(msgs.Count >= 7, $"Expected at least 7 history messages, got {msgs.Count}");

        // Assert: final text
        var fullText = string.Join("", events.OfType<AgentStreamEvent.TextDelta>().Select(d => d.Text));
        Assert.Contains("three steps completed", fullText);
    }

    // ── E. Max turns limit ──────────────────────────────────────────

    [Fact]
    public async Task AgentLoop_MaxTurnsExceeded_StopsWithBudgetExhausted()
    {
        // Arrange: client always requests another tool call (infinite loop)
        int toolCallCount = 0;
        var tool = AIFunctionFactory.Create(() => { toolCallCount++; return "ok"; },
            "infinite_tool", "A tool that never stops being requested.");

        var mockClient = new InfiniteToolCallingMockChatClient("infinite_tool");
        var options = CreateTestOptions([tool], maxTurns: 2);
        var loop = new AgentLoopRunner(mockClient, options);
        var history = new ChatHistoryManager();

        // Act
        var events = await CollectEventsAsync(loop, history, "Go forever");

        // Assert: loop terminated
        Assert.Contains(events, e => e is AgentStreamEvent.Completed);

        // Assert: max turns message emitted
        var textParts = events.OfType<AgentStreamEvent.TextDelta>().Select(d => d.Text);
        var fullText = string.Join("", textParts);
        Assert.Contains("Max turns", fullText);

        // Assert: tool was called but not indefinitely (at most 2 turns worth)
        Assert.True(toolCallCount <= 2, $"Expected at most 2 tool calls, got {toolCallCount}");
    }

    // ── Test Helpers ─────────────────────────────────────────────────

    private static AgentLoopOptions CreateTestOptions(
        IList<AITool>? tools = null,
        IReadOnlySet<string>? dangerousTools = null,
        int maxTurns = 10)
    {
        return new AgentLoopOptions
        {
            SystemPrompt = "You are a test assistant.",
            Tools = tools ?? new List<AITool>(),
            Limits = TokenLimits.Balanced,
            ToolResultStore = new ToolResultStore(),
            DangerousToolNames = dangerousTools ?? new HashSet<string>(),
            MaxTurns = maxTurns,
            Log = (_, _) => { },
        };
    }

    private static async Task<List<AgentStreamEvent>> CollectEventsAsync(
        AgentLoopRunner loop,
        ChatHistoryManager history,
        string userMessage)
    {
        var events = new List<AgentStreamEvent>();
        var ct = TestContext.Current.CancellationToken;
        var reader = loop.RunStreamingAsync(userMessage, history, cancellationToken: ct);
        await foreach (var evt in reader.ReadAllAsync(ct))
            events.Add(evt);
        return events;
    }
}

// ── Test doubles for end-to-end agent loop tests ────────────────────

/// <summary>
/// Mock IChatClient that returns tool call responses for a configured number of turns,
/// then returns a final text response. Each entry in toolCallSequence corresponds to
/// one LLM call that returns FunctionCallContent items. After all tool call turns are
/// exhausted, subsequent calls return the final text response.
/// </summary>
#pragma warning disable CA1822 // Interface implementation cannot be static
internal sealed class ToolCallingMockChatClient : IChatClient
{
    private readonly List<List<FunctionCallContent>> _toolCallSequence;
    private readonly string _finalResponse;
    private int _callIndex;

    /// <summary>Number of times GetStreamingResponseAsync was called.</summary>
    public int CallCount => _callIndex;

    public ToolCallingMockChatClient(
        List<List<FunctionCallContent>> toolCallSequence,
        string finalResponse)
    {
        _toolCallSequence = toolCallSequence;
        _finalResponse = finalResponse;
    }

    public ChatClientMetadata Metadata => new("tool-calling-mock");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Use GetStreamingResponseAsync");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var currentCall = _callIndex;
        _callIndex++;

        if (currentCall < _toolCallSequence.Count)
        {
            // Return tool call(s) for this turn
            var toolCalls = _toolCallSequence[currentCall];
            var contents = new List<AIContent>();
            foreach (var fc in toolCalls)
                contents.Add(new FunctionCallContent(fc.CallId, fc.Name, fc.Arguments));

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = contents,
            };
            yield return new ChatResponseUpdate
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }
        else
        {
            // Return final text response
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent(_finalResponse)],
            };
            yield return new ChatResponseUpdate
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
#pragma warning restore CA1822

/// <summary>
/// Mock IChatClient that always requests the same tool call on every invocation.
/// Used for testing max turns limits — the loop never gets a text-only response.
/// </summary>
#pragma warning disable CA1822 // Interface implementation cannot be static
internal sealed class InfiniteToolCallingMockChatClient : IChatClient
{
    private readonly string _toolName;
    private int _callIndex;

    public int CallCount => _callIndex;

    public InfiniteToolCallingMockChatClient(string toolName)
    {
        _toolName = toolName;
    }

    public ChatClientMetadata Metadata => new("infinite-tool-calling-mock");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Use GetStreamingResponseAsync");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var callId = $"call_{_callIndex}";
        _callIndex++;

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent(callId, _toolName)],
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
