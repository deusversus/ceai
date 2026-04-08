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

    // ── CompactionResult ─────────────────────────────────────────────

    [Fact]
    public void CompactionResult_TokensSaved_ComputedCorrectly()
    {
        var result = new CompactionResult(true, 10_000, 3_000);
        Assert.Equal(7_000, result.TokensSaved);
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
