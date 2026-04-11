using System.Runtime.CompilerServices;
using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
using Microsoft.Extensions.AI;

namespace CEAISuite.Tests;

public class CompactionPipelineTests
{
    private static TokenLimits MakeLimits(
        int compactionToolResultMessages = 2,
        int compactionSummarizationTokens = 200,
        int compactionSlidingWindowTurns = 3,
        int compactionTruncationTokens = 500)
    {
        return new TokenLimits
        {
            CompactionToolResultMessages = compactionToolResultMessages,
            CompactionSummarizationTokens = compactionSummarizationTokens,
            CompactionSlidingWindowTurns = compactionSlidingWindowTurns,
            CompactionTruncationTokens = compactionTruncationTokens,
        };
    }

    // ── Empty history ────────────────────────────────────────────────

    [Fact]
    public async Task CompactAsync_EmptyHistory_ReturnsNoChange()
    {
        var history = new ChatHistoryManager();
        var pipeline = new CompactionPipeline(
            new SummaryMockChatClient("summary"), MakeLimits());

        var result = await pipeline.CompactAsync(history, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(0, result.TokensBefore);
        Assert.Equal(0, result.TokensAfter);
        Assert.Equal(0, history.Count);
    }

    // ── Simple chat preserves system prompt ──────────────────────────

    [Fact]
    public async Task CompactAsync_SmallHistory_NoCompactionNeeded()
    {
        var history = new ChatHistoryManager();
        history.AddSystemMessage("You are a test assistant.");
        history.AddUserMessage("Hello");

        var limits = MakeLimits(
            compactionToolResultMessages: 100,
            compactionSummarizationTokens: 100_000,
            compactionSlidingWindowTurns: 100,
            compactionTruncationTokens: 100_000);

        var pipeline = new CompactionPipeline(
            new SummaryMockChatClient("summary"), limits);

        var result = await pipeline.CompactAsync(history, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(2, history.Count);
    }

    // ── Compaction preserves recent messages ─────────────────────────

    [Fact]
    public async Task CompactAsync_SlidingWindow_KeepsRecentTurns()
    {
        var history = new ChatHistoryManager();
        for (int i = 0; i < 10; i++)
        {
            history.AddUserMessage($"User message {i}");
            history.AddAssistantMessage(new ChatMessage(ChatRole.Assistant, $"Assistant reply {i}"));
        }

        var limits = MakeLimits(
            compactionToolResultMessages: 100,
            compactionSummarizationTokens: 100_000,
            compactionSlidingWindowTurns: 3,
            compactionTruncationTokens: 100_000);

        var pipeline = new CompactionPipeline(
            new SummaryMockChatClient("summary"), limits);

        var result = await pipeline.CompactAsync(history, TestContext.Current.CancellationToken);

        Assert.True(result.Success);

        var messages = history.GetMessages();
        var userMessages = messages.Where(m => m.Role == ChatRole.User).ToList();
        Assert.True(userMessages.Count <= 4);

        Assert.Contains(messages, m => m.Text?.Contains("User message 9") == true);
    }

    // ── LLM summarization ────────────────────────────────────────────

    [Fact]
    public async Task CompactAsync_Summarization_ReducesTokenCount()
    {
        var history = new ChatHistoryManager();
        var longText = new string('x', 2000);
        for (int i = 0; i < 10; i++)
        {
            history.AddUserMessage($"User {i}: {longText}");
            history.AddAssistantMessage(new ChatMessage(ChatRole.Assistant, $"Assistant {i}: {longText}"));
        }

        int tokensBefore = history.EstimateTokens();

        var limits = MakeLimits(
            compactionToolResultMessages: 100,
            compactionSummarizationTokens: 100,
            compactionSlidingWindowTurns: 100,
            compactionTruncationTokens: 100_000);

        var pipeline = new CompactionPipeline(
            new SummaryMockChatClient("Compacted summary of earlier conversation."), limits);

        var result = await pipeline.CompactAsync(history, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.TokensAfter < tokensBefore);
        Assert.True(result.TokensSaved > 0);
    }

    // ── Tool call results are handled ────────────────────────────────

    [Fact]
    public async Task CompactAsync_ToolResults_CollapseWhenThresholdExceeded()
    {
        var history = new ChatHistoryManager();

        for (int i = 0; i < 5; i++)
        {
            history.AddUserMessage($"Do scan {i}");

            var assistantMsg = new ChatMessage(ChatRole.Assistant, $"Calling tool {i}");
            assistantMsg.Contents.Add(new FunctionCallContent($"call_{i}", $"scan_{i}"));
            history.AddAssistantMessage(assistantMsg);

            history.AddToolResults([new FunctionResultContent($"call_{i}", $"Result data for scan {i}: " + new string('R', 500))]);
        }

        var limits = MakeLimits(
            compactionToolResultMessages: 2,
            compactionSummarizationTokens: 100_000,
            compactionSlidingWindowTurns: 100,
            compactionTruncationTokens: 100_000);

        var pipeline = new CompactionPipeline(
            new SummaryMockChatClient("summary"), limits);

        var result = await pipeline.CompactAsync(history, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task CompactAsync_ToolResults_BelowThreshold_NoPruning()
    {
        var history = new ChatHistoryManager();
        history.AddUserMessage("Hello");

        var assistantMsg = new ChatMessage(ChatRole.Assistant, "Calling tool");
        assistantMsg.Contents.Add(new FunctionCallContent("call_1", "my_tool"));
        history.AddAssistantMessage(assistantMsg);

        history.AddToolResults([new FunctionResultContent("call_1", "Short result")]);

        var limits = MakeLimits(
            compactionToolResultMessages: 100,
            compactionSummarizationTokens: 100_000,
            compactionSlidingWindowTurns: 100,
            compactionTruncationTokens: 100_000);

        var pipeline = new CompactionPipeline(
            new SummaryMockChatClient("summary"), limits);

        var result = await pipeline.CompactAsync(history, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(3, history.Count);
    }

    // ── ShouldCompact ────────────────────────────────────────────────

    [Fact]
    public void ShouldCompact_EmptyHistory_ReturnsFalse()
    {
        var history = new ChatHistoryManager();
        var pipeline = new CompactionPipeline(
            new SummaryMockChatClient("summary"),
            MakeLimits(
                compactionToolResultMessages: 100,
                compactionSummarizationTokens: 100_000,
                compactionSlidingWindowTurns: 100,
                compactionTruncationTokens: 100_000));

        Assert.False(pipeline.ShouldCompact(history));
    }

    [Fact]
    public void ShouldCompact_OverSlidingWindowThreshold_ReturnsTrue()
    {
        var history = new ChatHistoryManager();
        for (int i = 0; i < 10; i++)
            history.AddUserMessage($"Message {i}");

        var pipeline = new CompactionPipeline(
            new SummaryMockChatClient("summary"),
            MakeLimits(
                compactionToolResultMessages: 100,
                compactionSummarizationTokens: 100_000,
                compactionSlidingWindowTurns: 3,
                compactionTruncationTokens: 100_000));

        Assert.True(pipeline.ShouldCompact(history));
    }

    // ── Emergency truncation ─────────────────────────────────────────

    [Fact]
    public async Task CompactAsync_EmergencyTruncation_DropsOldestMessages()
    {
        var history = new ChatHistoryManager();
        var hugeText = new string('A', 4000);
        for (int i = 0; i < 20; i++)
            history.AddUserMessage($"Turn {i}: {hugeText}");

        int tokensBefore = history.EstimateTokens();

        var limits = MakeLimits(
            compactionToolResultMessages: 100,
            compactionSummarizationTokens: 100_000,
            compactionSlidingWindowTurns: 100,
            compactionTruncationTokens: 500);

        var pipeline = new CompactionPipeline(
            new SummaryMockChatClient("summary"), limits);

        var result = await pipeline.CompactAsync(history, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(history.Count < 20);
        Assert.True(result.TokensAfter < tokensBefore);
    }

    // ── Orphaned tool message boundary safety ─────────────────────────

    [Fact]
    public async Task CompactAsync_Summarization_NeverOrphansToolMessages()
    {
        // Reproduce the exact crash scenario: many tool call/result pairs where
        // the 2/3 split lands between an assistant(tool_calls) and tool(results).
        var history = new ChatHistoryManager();

        // Single user message followed by many assistant+tool pairs
        history.AddUserMessage("Hey, let's analyze the EXP addresses.");

        for (int i = 0; i < 5; i++)
        {
            var assistantMsg = new ChatMessage(ChatRole.Assistant, []);
            assistantMsg.Contents.Add(new FunctionCallContent($"call_{i}", $"Tool_{i}"));
            history.AddAssistantMessage(assistantMsg);

            history.AddToolResults([new FunctionResultContent($"call_{i}",
                $"Result data {i}: " + new string('D', 400))]);
        }

        // Force summarization with tight threshold
        var limits = MakeLimits(
            compactionToolResultMessages: 100,
            compactionSummarizationTokens: 100,
            compactionSlidingWindowTurns: 100,
            compactionTruncationTokens: 100_000);

        var pipeline = new CompactionPipeline(
            new SummaryMockChatClient("Compacted summary."), limits);

        await pipeline.CompactAsync(history, TestContext.Current.CancellationToken);

        // Verify: no message in the history starts with a tool role
        // unless the previous message is an assistant with matching tool_calls
        var messages = history.GetMessages();
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == ChatRole.Tool)
            {
                Assert.True(i > 0, $"Tool message at index {i} has no preceding message");
                Assert.Equal(ChatRole.Assistant, messages[i - 1].Role);

                // The preceding assistant message must contain FunctionCallContent
                var hasToolCalls = messages[i - 1].Contents
                    .Any(c => c is FunctionCallContent);
                Assert.True(hasToolCalls,
                    $"Tool message at index {i} preceded by assistant without tool_calls");
            }
        }
    }

    [Fact]
    public async Task CompactAsync_Truncation_NeverStartsWithToolMessage()
    {
        var history = new ChatHistoryManager();

        // Build history: user, then pairs of assistant(tool_calls)+tool(results)
        history.AddUserMessage("Start");

        for (int i = 0; i < 10; i++)
        {
            var assistantMsg = new ChatMessage(ChatRole.Assistant, []);
            assistantMsg.Contents.Add(new FunctionCallContent($"call_{i}", $"Tool_{i}"));
            history.AddAssistantMessage(assistantMsg);

            history.AddToolResults([new FunctionResultContent($"call_{i}",
                new string('R', 2000))]);
        }

        // Tight truncation limit to force heavy trimming
        var limits = MakeLimits(
            compactionToolResultMessages: 100,
            compactionSummarizationTokens: 100_000,
            compactionSlidingWindowTurns: 100,
            compactionTruncationTokens: 500);

        var pipeline = new CompactionPipeline(
            new SummaryMockChatClient("summary"), limits);

        await pipeline.CompactAsync(history, TestContext.Current.CancellationToken);

        var messages = history.GetMessages();
        Assert.True(messages.Count > 0);
        // First message must never be a tool message
        Assert.NotEqual(ChatRole.Tool, messages[0].Role);
    }

    // ── CompactionResult ─────────────────────────────────────────────

    [Fact]
    public void CompactionResult_TokensSaved_ComputedCorrectly()
    {
        var result = new CompactionResult(true, 10_000, 3_000);
        Assert.Equal(7_000, result.TokensSaved);
    }

    // ── PostCompactionRestorer round-trip tests ─────────────────────

    [Fact]
    public async Task PostCompactionRestorer_CaptureCompactRestore_ToolResultsSurvive()
    {
        var history = new ChatHistoryManager();

        // Build a conversation with tool calls and results
        for (int i = 0; i < 4; i++)
        {
            history.AddUserMessage($"Do operation {i}");

            var assistantMsg = new ChatMessage(ChatRole.Assistant, $"Calling tool {i}");
            assistantMsg.Contents.Add(new FunctionCallContent($"call_{i}", $"Tool_{i}"));
            history.AddAssistantMessage(assistantMsg);

            // Make results >50 chars so CaptureSnapshot doesn't skip them
            history.AddToolResults([new FunctionResultContent($"call_{i}",
                $"MARKER_RESULT_{i}_" + new string('D', 100))]);
        }

        var categories = new HashSet<string> { "Memory", "Scanning" };

        // Step 1: Capture snapshot
        var snapshot = PostCompactionRestorer.CaptureSnapshot(
            history, categories, () => "Attached to TestGame.exe PID 9999");

        Assert.True(snapshot.RecentToolResults.Count > 0);
        Assert.Equal("Attached to TestGame.exe PID 9999", snapshot.ProcessContext);

        // Step 2: Compact (aggressive limits to force summarization)
        var limits = MakeLimits(
            compactionToolResultMessages: 1,
            compactionSummarizationTokens: 50,
            compactionSlidingWindowTurns: 1,
            compactionTruncationTokens: 100_000);
        var pipeline = new CompactionPipeline(
            new SummaryMockChatClient("Compacted conversation summary."), limits);

        await pipeline.CompactAsync(history, TestContext.Current.CancellationToken);

        // Step 3: Restore
        PostCompactionRestorer.Restore(history, snapshot);

        // Step 4: Verify restored content
        var messages = history.GetMessages();
        var restoredText = string.Join("\n", messages.Select(m => m.Text ?? ""));

        // Tool results should be present
        Assert.Contains("MARKER_RESULT_3", restoredText); // Most recent
        Assert.Contains("Tool_3", restoredText); // Tool name

        // Process context restored
        Assert.Contains("TestGame.exe PID 9999", restoredText);

        // Categories restored
        Assert.Contains("Memory", restoredText);
        Assert.Contains("Scanning", restoredText);
    }

    [Fact]
    public void PostCompactionRestorer_CaptureSnapshot_RespectsMaxRecentResults()
    {
        var history = new ChatHistoryManager();

        // Add 8 tool results (max is 5)
        for (int i = 0; i < 8; i++)
        {
            var assistantMsg = new ChatMessage(ChatRole.Assistant, $"Call {i}");
            assistantMsg.Contents.Add(new FunctionCallContent($"call_{i}", $"Tool_{i}"));
            history.AddAssistantMessage(assistantMsg);

            history.AddToolResults([new FunctionResultContent($"call_{i}",
                $"Result_{i}_" + new string('X', 100))]);
        }

        var snapshot = PostCompactionRestorer.CaptureSnapshot(
            history, new HashSet<string>());

        Assert.True(snapshot.RecentToolResults.Count <= ContextSnapshot.MaxRecentResults);
        // Should capture the most recent (7, 6, 5, 4, 3)
        Assert.Contains(snapshot.RecentToolResults, r => r.ToolName == "Tool_7");
        Assert.DoesNotContain(snapshot.RecentToolResults, r => r.ToolName == "Tool_0");
    }

    [Fact]
    public void PostCompactionRestorer_Restore_TruncatesLargeResults()
    {
        var history = new ChatHistoryManager();
        int countBefore = history.Count;

        // Create a snapshot with one oversized result
        var largeResult = new string('Z', 10_000);
        var snapshot = new ContextSnapshot
        {
            RecentToolResults = [("BigTool", largeResult)],
            ProcessContext = null,
            AddressTableSummary = null,
            ActiveCategories = new HashSet<string>(),
        };

        PostCompactionRestorer.Restore(history, snapshot);

        var messages = history.GetMessages();
        var restoredMsg = messages[^1];
        var text = restoredMsg.Text ?? "";

        // Should contain truncation marker
        Assert.Contains("truncated at", text);
        // The full 10K result should NOT be in the message
        Assert.DoesNotContain(largeResult, text);
    }

    [Fact]
    public void PostCompactionRestorer_Restore_EmptySnapshot_NoMessage()
    {
        var history = new ChatHistoryManager();
        history.AddUserMessage("Hello");
        int countBefore = history.Count;

        var snapshot = new ContextSnapshot
        {
            RecentToolResults = [],
            ProcessContext = null,
            AddressTableSummary = null,
            ActiveCategories = new HashSet<string>(),
        };

        PostCompactionRestorer.Restore(history, snapshot);

        // Guard at line 75 of PostCompactionRestorer should prevent injection
        Assert.Equal(countBefore, history.Count);
    }
}

// ── Test double ──────────────────────────────────────────────────────

#pragma warning disable CA1822
internal sealed class SummaryMockChatClient : IChatClient
{
    private readonly string _summaryText;

    public SummaryMockChatClient(string summaryText) => _summaryText = summaryText;

    public ChatClientMetadata Metadata => new("summary-mock");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msg = new ChatMessage(ChatRole.Assistant, _summaryText);
        return Task.FromResult(new ChatResponse(msg));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent(_summaryText)],
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
