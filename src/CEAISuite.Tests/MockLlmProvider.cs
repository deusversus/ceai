using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace CEAISuite.Tests;

/// <summary>
/// Unified mock LLM provider for testing. Supports queued responses (text, tool calls, errors),
/// call logging, and both sync and streaming APIs.
/// </summary>
#pragma warning disable CA1822
internal sealed class MockLlmProvider : IChatClient
{
    private readonly Queue<QueuedItem> _responseQueue = new();
    private readonly List<IReadOnlyList<ChatMessage>> _callLog = new();
    private int _callCount;

    /// <summary>Default text response returned when the queue is empty. Null = throw.</summary>
    public string? DefaultResponse { get; set; }

    /// <summary>Records of all message lists passed to GetResponseAsync/GetStreamingResponseAsync.</summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> CallLog => _callLog;

    /// <summary>Total number of calls made.</summary>
    public int CallCount => _callCount;

    public ChatClientMetadata Metadata => new("mock-llm");

    // ── Builder methods ─────────────────────────────────────────────

    /// <summary>Enqueue a plain text response.</summary>
    public MockLlmProvider EnqueueResponse(string text)
    {
        _responseQueue.Enqueue(new QueuedItem(QueuedItemType.Text, text, null, null));
        return this;
    }

    /// <summary>Enqueue a tool call response.</summary>
    public MockLlmProvider EnqueueToolCalls(List<FunctionCallContent> toolCalls)
    {
        _responseQueue.Enqueue(new QueuedItem(QueuedItemType.ToolCalls, null, toolCalls, null));
        return this;
    }

    /// <summary>Enqueue an HTTP error (throws HttpRequestException).</summary>
    public MockLlmProvider EnqueueError(HttpStatusCode statusCode)
    {
        _responseQueue.Enqueue(new QueuedItem(QueuedItemType.Error, null, null, statusCode));
        return this;
    }

    // ── IChatClient implementation ──────────────────────────────────

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        _callLog.Add(messageList);
        Interlocked.Increment(ref _callCount);

        var item = DequeueOrDefault();

        return item.Type switch
        {
            QueuedItemType.Error => throw new HttpRequestException(
                $"Mock {(int)item.StatusCode!} error", null, item.StatusCode),
            QueuedItemType.ToolCalls => Task.FromResult(BuildToolCallResponse(item.ToolCalls!)),
            _ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, item.Text ?? ""))),
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        _callLog.Add(messageList);
        Interlocked.Increment(ref _callCount);

        var item = DequeueOrDefault();

        if (item.Type == QueuedItemType.Error)
            throw new HttpRequestException(
                $"Mock {(int)item.StatusCode!} error", null, item.StatusCode);

        if (item.Type == QueuedItemType.ToolCalls)
        {
            var contents = new List<AIContent>();
            foreach (var fc in item.ToolCalls!)
                contents.Add(new FunctionCallContent(fc.CallId, fc.Name, fc.Arguments));

            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = contents };
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
        }
        else
        {
            // Stream text word by word
            var text = item.Text ?? "";
            var words = text.Split(' ');
            foreach (var word in words)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent(word + " ")],
                };
            }
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
        }

        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    // ── Helpers ──────────────────────────────────────────────────────

    private QueuedItem DequeueOrDefault()
    {
        if (_responseQueue.Count > 0)
            return _responseQueue.Dequeue();

        if (DefaultResponse is not null)
            return new QueuedItem(QueuedItemType.Text, DefaultResponse, null, null);

        throw new InvalidOperationException("MockLlmProvider response queue is empty and no DefaultResponse set");
    }

    private static ChatResponse BuildToolCallResponse(List<FunctionCallContent> toolCalls)
    {
        var msg = new ChatMessage(ChatRole.Assistant, "");
        foreach (var fc in toolCalls)
            msg.Contents.Add(new FunctionCallContent(fc.CallId, fc.Name, fc.Arguments));
        return new ChatResponse(msg) { FinishReason = ChatFinishReason.Stop };
    }

    // ── Internal types ──────────────────────────────────────────────

    private enum QueuedItemType { Text, ToolCalls, Error }

    private sealed record QueuedItem(
        QueuedItemType Type,
        string? Text,
        List<FunctionCallContent>? ToolCalls,
        HttpStatusCode? StatusCode);
}
#pragma warning restore CA1822
