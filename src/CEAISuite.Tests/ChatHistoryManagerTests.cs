using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
using Microsoft.Extensions.AI;

namespace CEAISuite.Tests;

public class ChatHistoryManagerTests
{
    [Fact]
    public void Count_InitiallyZero()
    {
        var mgr = new ChatHistoryManager();
        Assert.Equal(0, mgr.Count);
    }

    [Fact]
    public void AddUserMessage_IncreasesCount()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddUserMessage("hello");
        Assert.Equal(1, mgr.Count);
    }

    [Fact]
    public void AddUserMessage_WithContextSuffix_AppendsToContent()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddUserMessage("hello", "context info");

        var messages = mgr.GetMessages();
        Assert.Single(messages);
        Assert.Contains("hello", messages[0].Text);
        Assert.Contains("context info", messages[0].Text);
    }

    [Fact]
    public void AddUserMessage_WithoutContext_NoSuffix()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddUserMessage("plain message");

        var messages = mgr.GetMessages();
        Assert.Equal("plain message", messages[0].Text);
    }

    [Fact]
    public void AddUserMessage_WithContents_AddsMultiContentMessage()
    {
        var mgr = new ChatHistoryManager();
        var contents = new List<AIContent> { new TextContent("text part") };
        mgr.AddUserMessage(contents, "suffix");

        Assert.Equal(1, mgr.Count);
        var messages = mgr.GetMessages();
        Assert.Equal(ChatRole.User, messages[0].Role);
    }

    [Fact]
    public void AddAssistantMessage_StoresMessage()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddAssistantMessage(new ChatMessage(ChatRole.Assistant, "I am Claude"));

        Assert.Equal(1, mgr.Count);
        var messages = mgr.GetMessages();
        Assert.Equal(ChatRole.Assistant, messages[0].Role);
    }

    [Fact]
    public void AddToolResults_StoresAsToolRole()
    {
        var mgr = new ChatHistoryManager();
        var results = new[] { new FunctionResultContent("call-1", "result data") };
        mgr.AddToolResults(results);

        Assert.Equal(1, mgr.Count);
        var messages = mgr.GetMessages();
        Assert.Equal(ChatRole.Tool, messages[0].Role);
    }

    [Fact]
    public void AddSystemMessage_WrapsInSystemReminder()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddSystemMessage("important context");

        var messages = mgr.GetMessages();
        Assert.Single(messages);
        Assert.Contains("<system-reminder>", messages[0].Text);
        Assert.Contains("important context", messages[0].Text);
        Assert.Contains("</system-reminder>", messages[0].Text);
    }

    [Fact]
    public void Clear_RemovesAllMessages()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddUserMessage("msg1");
        mgr.AddUserMessage("msg2");
        Assert.Equal(2, mgr.Count);

        mgr.Clear();
        Assert.Equal(0, mgr.Count);
    }

    [Fact]
    public void GetMessages_ReturnsSnapshot()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddUserMessage("msg1");

        var snapshot = mgr.GetMessages();
        mgr.AddUserMessage("msg2");

        // Snapshot should not be affected by subsequent additions
        Assert.Single(snapshot);
        Assert.Equal(2, mgr.Count);
    }

    [Fact]
    public void ReplaceAll_ReplacesEntireHistory()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddUserMessage("old message");

        var newMessages = new List<ChatMessage>
        {
            new(ChatRole.User, "new1"),
            new(ChatRole.Assistant, "new2")
        };
        mgr.ReplaceAll(newMessages);

        Assert.Equal(2, mgr.Count);
        var messages = mgr.GetMessages();
        Assert.Equal("new1", messages[0].Text);
        Assert.Equal("new2", messages[1].Text);
    }

    [Fact]
    public void EstimateTokens_ReturnsApproximateCount()
    {
        var mgr = new ChatHistoryManager();
        // 100 chars / 4 = 25 tokens approximately
        mgr.AddUserMessage(new string('a', 100));

        var tokens = mgr.EstimateTokens();
        Assert.Equal(25, tokens);
    }

    [Fact]
    public void EstimateTokens_EmptyHistory_ReturnsZero()
    {
        var mgr = new ChatHistoryManager();
        Assert.Equal(0, mgr.EstimateTokens());
    }

    [Fact]
    public void CountUserTurns_CountsOnlyUserMessages()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddUserMessage("user1");
        mgr.AddAssistantMessage(new ChatMessage(ChatRole.Assistant, "assistant1"));
        mgr.AddUserMessage("user2");

        Assert.Equal(2, mgr.CountUserTurns());
    }

    [Fact]
    public void PruneOldToolResults_TruncatesOldResults()
    {
        var mgr = new ChatHistoryManager();

        // Add old tool result with long content
        var longResult = new string('x', 500);
        mgr.AddToolResults(new[] { new FunctionResultContent("call-old", longResult) });
        mgr.AddUserMessage("user turn 1");
        mgr.AddUserMessage("user turn 2");

        int pruned = mgr.PruneOldToolResults(keepRecentTurns: 2, summaryMaxChars: 50);

        Assert.Equal(1, pruned);
    }

    [Fact]
    public void PruneOldToolResults_KeepsRecentResults()
    {
        var mgr = new ChatHistoryManager();

        // Place two user turns first, then tool result after them
        // With keepRecentTurns=2, cutoff should be at the first user turn,
        // so tool result at the end is beyond cutoff and should NOT be pruned
        mgr.AddUserMessage("user turn 1");
        mgr.AddUserMessage("user turn 2");
        var recentResult = new string('y', 500);
        mgr.AddToolResults(new[] { new FunctionResultContent("call-recent", recentResult) });

        int pruned = mgr.PruneOldToolResults(keepRecentTurns: 2, summaryMaxChars: 50);

        Assert.Equal(0, pruned);
    }

    [Fact]
    public void ReplaceToolResult_ReplacesMatchingCallId()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddToolResults(new[] { new FunctionResultContent("call-1", "original content") });

        bool replaced = mgr.ReplaceToolResult("call-1", "updated content");

        Assert.True(replaced);
    }

    [Fact]
    public void ReplaceToolResult_NonexistentCallId_ReturnsFalse()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddToolResults(new[] { new FunctionResultContent("call-1", "content") });

        bool replaced = mgr.ReplaceToolResult("call-999", "new content");

        Assert.False(replaced);
    }

    [Fact]
    public void ReplayFromSaved_ReconstructsHistory()
    {
        var mgr = new ChatHistoryManager();
        var limits = TokenLimits.Balanced;
        var store = new ToolResultStore();

        var saved = new List<AiChatMessage>
        {
            new("user", "Hello", DateTimeOffset.UtcNow),
            new("assistant", "Hi there", DateTimeOffset.UtcNow),
            new("user", "Do something", DateTimeOffset.UtcNow),
        };

        mgr.ReplayFromSaved(saved, 10, limits, store);

        Assert.Equal(3, mgr.Count);
        var messages = mgr.GetMessages();
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal(ChatRole.Assistant, messages[1].Role);
        Assert.Equal(ChatRole.User, messages[2].Role);
    }

    [Fact]
    public void ReplayFromSaved_RespectsMaxMessages()
    {
        var mgr = new ChatHistoryManager();
        var limits = TokenLimits.Balanced;
        var store = new ToolResultStore();

        var saved = new List<AiChatMessage>
        {
            new("user", "msg1", DateTimeOffset.UtcNow),
            new("assistant", "reply1", DateTimeOffset.UtcNow),
            new("user", "msg2", DateTimeOffset.UtcNow),
            new("assistant", "reply2", DateTimeOffset.UtcNow),
        };

        mgr.ReplayFromSaved(saved, 2, limits, store);

        // Should only replay the last 2
        Assert.Equal(2, mgr.Count);
    }

    [Fact]
    public void ReplayFromSaved_WithToolCalls_ReconstructsFunctionContent()
    {
        var mgr = new ChatHistoryManager();
        var limits = TokenLimits.Balanced;
        var store = new ToolResultStore();

        var saved = new List<AiChatMessage>
        {
            new("assistant", "Let me check", DateTimeOffset.UtcNow)
            {
                ToolCalls = new List<AiToolCallInfo>
                {
                    new("tc-1", "ReadMemory", "{\"address\": \"0x1000\"}")
                },
                ToolResults = new List<AiToolResultInfo>
                {
                    new("tc-1", "ReadMemory", "42")
                }
            }
        };

        mgr.ReplayFromSaved(saved, 10, limits, store);

        // Should have assistant message + tool result message
        Assert.Equal(2, mgr.Count);
        var messages = mgr.GetMessages();
        Assert.Equal(ChatRole.Assistant, messages[0].Role);
        Assert.Equal(ChatRole.Tool, messages[1].Role);
    }

    [Fact]
    public void ReplayFromSaved_ClearsExistingMessages()
    {
        var mgr = new ChatHistoryManager();
        mgr.AddUserMessage("existing");
        Assert.Equal(1, mgr.Count);

        var limits = TokenLimits.Balanced;
        var store = new ToolResultStore();

        mgr.ReplayFromSaved(new List<AiChatMessage>
        {
            new("user", "replayed", DateTimeOffset.UtcNow)
        }, 10, limits, store);

        Assert.Equal(1, mgr.Count);
        Assert.Equal("replayed", mgr.GetMessages()[0].Text);
    }
}
