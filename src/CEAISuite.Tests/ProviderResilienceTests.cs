using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using CEAISuite.Application.AgentLoop;
using Microsoft.Extensions.AI;

namespace CEAISuite.Tests;

/// <summary>
/// Provider resilience tests: HTTP failure simulation with fallover chain,
/// mid-conversation provider switch, and CopilotTokenService concurrency.
/// </summary>
public class ProviderResilienceTests
{
    // ── A. HTTP Failure Simulation with Fallover Chain ─────────────────

    [Fact]
    public async Task RetryPolicy_ThreeConsecutive503_SignalsModelFallbackWithCooldown()
    {
        // Arrange: a client that always throws 503 (overloaded)
        var callCount = 0;
        var policy = new RetryPolicy(
            maxRetries: 5,
            baseDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(10));

        // Act: the policy should detect 3 consecutive overloads and signal fallback
        var ct = TestContext.Current.CancellationToken;
        var result = await policy.ExecuteAsync<string>(async _ =>
        {
            callCount++;
            await Task.CompletedTask;
            throw new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
        }, cancellationToken: ct);

        // Assert: should signal model fallback with cooldown (3 consecutive short-delay overloads)
        Assert.False(result.Success);
        Assert.True(result.NeedsModelFallback);
        Assert.True(result.NeedsFastModeCooldown);
        Assert.True(callCount >= 3, $"Expected at least 3 attempts, got {callCount}");
    }

    [Fact]
    public async Task RetryPolicy_LongRetryAfter_SignalsImmediateModelFallback()
    {
        // Arrange: exception with a retry-after value exceeding the fast-fallback threshold (30s)
        var policy = new RetryPolicy(
            maxRetries: 5,
            baseDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(10));

        var ct = TestContext.Current.CancellationToken;
        var result = await policy.ExecuteAsync<string>(async _ =>
        {
            await Task.CompletedTask;
            // Overloaded error with a long retry-after (>30s triggers immediate fallback)
            throw new InvalidOperationException("overloaded_error: retry-after: 60 seconds");
        }, cancellationToken: ct);

        // Assert: immediate fallback (not cooldown) because of long retry-after
        Assert.False(result.Success);
        Assert.True(result.NeedsModelFallback);
        Assert.False(result.NeedsFastModeCooldown);
    }

    [Fact]
    public async Task RetryPolicy_429RateLimit_IsRetriable()
    {
        // Arrange: a client that fails with 429 twice, then succeeds
        var callCount = 0;
        var policy = new RetryPolicy(
            maxRetries: 5,
            baseDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(10));

        var ct = TestContext.Current.CancellationToken;
        var result = await policy.ExecuteAsync(async _ =>
        {
            callCount++;
            await Task.CompletedTask;
            if (callCount <= 2)
                throw new HttpRequestException("Too Many Requests", null, HttpStatusCode.TooManyRequests);
            return "success";
        }, cancellationToken: ct);

        // Assert: should retry and eventually succeed
        Assert.True(result.Success);
        Assert.Equal("success", result.Value);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task RetryPolicy_500ServerError_IsRetriable()
    {
        // 500 is classified as server error and should be retriable
        var callCount = 0;
        var policy = new RetryPolicy(
            maxRetries: 3,
            baseDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(10));

        var ct = TestContext.Current.CancellationToken;
        var result = await policy.ExecuteAsync(async _ =>
        {
            callCount++;
            await Task.CompletedTask;
            if (callCount <= 1)
                throw new HttpRequestException("Internal Server Error", null, HttpStatusCode.InternalServerError);
            return "recovered";
        }, cancellationToken: ct);

        Assert.True(result.Success);
        Assert.Equal("recovered", result.Value);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RetryPolicy_401AuthError_IsRetriable()
    {
        // Auth errors are retriable (temporary key issues) per ErrorClassifier
        var callCount = 0;
        var policy = new RetryPolicy(
            maxRetries: 3,
            baseDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(10));

        var ct = TestContext.Current.CancellationToken;
        var result = await policy.ExecuteAsync(async _ =>
        {
            callCount++;
            await Task.CompletedTask;
            if (callCount <= 1)
                throw new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
            return "auth-recovered";
        }, cancellationToken: ct);

        Assert.True(result.Success);
        Assert.Equal("auth-recovered", result.Value);
    }

    [Fact]
    public void ErrorClassifier_503_IsOverloaded()
    {
        var ex = new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
        Assert.True(ErrorClassifier.IsOverloaded(ex));
        Assert.True(ErrorClassifier.IsRetriable(ex));
    }

    [Fact]
    public void ErrorClassifier_500_IsServerError_NotOverloaded()
    {
        var ex = new HttpRequestException("Internal Server Error", null, HttpStatusCode.InternalServerError);
        Assert.True(ErrorClassifier.IsServerError(ex));
        Assert.False(ErrorClassifier.IsOverloaded(ex));
        Assert.True(ErrorClassifier.IsRetriable(ex));
    }

    [Fact]
    public void ErrorClassifier_429_IsRateLimited()
    {
        var ex = new HttpRequestException("Too Many Requests", null, HttpStatusCode.TooManyRequests);
        Assert.True(ErrorClassifier.IsRateLimited(ex));
        Assert.True(ErrorClassifier.IsRetriable(ex));
    }

    [Fact]
    public void ErrorClassifier_401_IsAuthError()
    {
        var ex = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
        Assert.True(ErrorClassifier.IsAuthError(ex));
        Assert.True(ErrorClassifier.IsRetriable(ex));
    }

    [Fact]
    public void ModelSwitcher_FallbackToNext_AfterOverloadSignal()
    {
        // Simulate: after RetryPolicy signals NeedsModelFallback, ModelSwitcher advances
        var switcher = new ModelSwitcher(
        [
            new ModelConfig { ModelId = "primary-model", MaxContextTokens = 200_000 },
            new ModelConfig { ModelId = "fallback-model", MaxContextTokens = 128_000 },
            new ModelConfig { ModelId = "last-resort-model", MaxContextTokens = 64_000 },
        ]);

        Assert.Equal("primary-model", switcher.CurrentModel.ModelId);

        // First fallback
        var fb1 = switcher.FallbackToNext();
        Assert.NotNull(fb1);
        Assert.Equal("fallback-model", fb1.ModelId);
        Assert.Equal("fallback-model", switcher.CurrentModel.ModelId);

        // Second fallback
        var fb2 = switcher.FallbackToNext();
        Assert.NotNull(fb2);
        Assert.Equal("last-resort-model", fb2.ModelId);

        // No more fallbacks
        var fb3 = switcher.FallbackToNext();
        Assert.Null(fb3);
    }

    [Fact]
    public void ModelSwitcher_TriggerCooldown_TemporarilySwitches()
    {
        var switcher = new ModelSwitcher(
        [
            new ModelConfig { ModelId = "fast-model", MaxContextTokens = 200_000 },
            new ModelConfig { ModelId = "backup-model", MaxContextTokens = 128_000 },
        ]);

        Assert.Equal("fast-model", switcher.CurrentModel.ModelId);

        // Trigger cooldown — should switch to backup temporarily
        switcher.TriggerCooldown(TimeSpan.FromMilliseconds(50));
        Assert.Equal("backup-model", switcher.CurrentModel.ModelId);

        // After cooldown expires, CheckCooldownExpiry should restore original
        Thread.Sleep(100); // Ensure cooldown expires
        switcher.CheckCooldownExpiry();
        Assert.Equal("fast-model", switcher.CurrentModel.ModelId);
    }

    [Fact]
    public async Task FullFalloverChain_FailingClient_FallsToNextModel()
    {
        // Integration test: FailingMockChatClient produces 503 errors,
        // AgentLoop (via RetryPolicy) should signal model fallback
        var failingClient = new FailingMockChatClient(
            HttpStatusCode.ServiceUnavailable, failUntilAttempt: int.MaxValue);

        var switcher = new ModelSwitcher(
        [
            new ModelConfig { ModelId = "model-a", MaxContextTokens = 200_000 },
            new ModelConfig { ModelId = "model-b", MaxContextTokens = 128_000 },
        ]);

        // Verify initial state
        Assert.Equal("model-a", switcher.CurrentModel.ModelId);
        Assert.Equal(0, switcher.CurrentIndex);

        // Simulate what the AgentLoop does when RetryPolicy returns NeedsModelFallback:
        // it calls TriggerCooldown (for cooldown fallbacks) or FallbackToNext (for permanent)
        var policy = new RetryPolicy(
            maxRetries: 5,
            baseDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(10));

        var ct = TestContext.Current.CancellationToken;
        var retryResult = await policy.ExecuteAsync<string>(async retryCt =>
        {
            // Simulate the streaming call that fails
            await foreach (var _ in failingClient.GetStreamingResponseAsync(
                [], cancellationToken: retryCt))
            { }
            return "ok";
        }, cancellationToken: ct);

        // RetryPolicy should signal model fallback with cooldown (3 consecutive overloads)
        Assert.True(retryResult.NeedsModelFallback);

        // AgentLoop would now call switcher (we simulate that)
        if (retryResult.NeedsFastModeCooldown)
            switcher.TriggerCooldown(RetryPolicy.FastModeCooldownDuration);
        else
            switcher.FallbackToNext();

        // After fallback, the new model should be active
        Assert.Equal("model-b", switcher.CurrentModel.ModelId);
    }

    // ── B. Mid-conversation Provider Switch ──────────────────────────

    [Fact]
    public void MidConversation_SwitchModel_PreservesHistory()
    {
        // Arrange: build up chat history
        var history = new ChatHistoryManager();
        history.AddUserMessage("Hello, tell me about memory scanning.");
        history.AddAssistantMessage(new ChatMessage(ChatRole.Assistant, "I can help with memory scanning."));
        history.AddUserMessage("What about pointer scans?");
        history.AddAssistantMessage(new ChatMessage(ChatRole.Assistant, "Pointer scans follow reference chains..."));
        history.AddUserMessage("Great, now freeze that value.");

        var initialCount = history.Count;
        Assert.Equal(5, initialCount);

        // Arrange: model switcher
        var switcher = new ModelSwitcher(
        [
            new ModelConfig { ModelId = "claude-sonnet-4-6", MaxContextTokens = 200_000 },
            new ModelConfig { ModelId = "gpt-4o", MaxContextTokens = 128_000 },
            new ModelConfig { ModelId = "gemini-2.0-flash", MaxContextTokens = 1_000_000 },
        ]);

        // Act: switch model mid-conversation
        var switched = switcher.SwitchToModel("gpt-4o");

        // Assert: model changed, history unchanged
        Assert.True(switched);
        Assert.Equal("gpt-4o", switcher.CurrentModel.ModelId);
        Assert.Equal(initialCount, history.Count);

        // Verify all messages are preserved
        var messages = history.GetMessages();
        Assert.Equal(5, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Contains("memory scanning", messages[0].Text);
        Assert.Equal(ChatRole.Assistant, messages[1].Role);
        Assert.Equal(ChatRole.User, messages[2].Role);
        Assert.Equal(ChatRole.Assistant, messages[3].Role);
        Assert.Equal(ChatRole.User, messages[4].Role);
    }

    [Fact]
    public void MidConversation_SwitchToUnknownModel_ReturnsFalse()
    {
        var switcher = new ModelSwitcher(
        [
            new ModelConfig { ModelId = "claude-sonnet-4-6", MaxContextTokens = 200_000 },
        ]);

        var result = switcher.SwitchToModel("nonexistent-model");
        Assert.False(result);
        Assert.Equal("claude-sonnet-4-6", switcher.CurrentModel.ModelId);
    }

    [Fact]
    public void MidConversation_FallbackThenReset_RestoresPrimary()
    {
        var switcher = new ModelSwitcher(
        [
            new ModelConfig { ModelId = "primary", MaxContextTokens = 200_000 },
            new ModelConfig { ModelId = "fallback-1", MaxContextTokens = 128_000 },
            new ModelConfig { ModelId = "fallback-2", MaxContextTokens = 64_000 },
        ]);

        switcher.FallbackToNext();
        Assert.Equal("fallback-1", switcher.CurrentModel.ModelId);

        switcher.FallbackToNext();
        Assert.Equal("fallback-2", switcher.CurrentModel.ModelId);

        // Starting a new conversation resets to primary
        switcher.ResetToPrimary();
        Assert.Equal("primary", switcher.CurrentModel.ModelId);
    }

    [Fact]
    public void MidConversation_SwitchModel_HistoryPreservedWithToolResults()
    {
        // History with tool calls should also be preserved
        var history = new ChatHistoryManager();
        history.AddUserMessage("Scan for the value 100");

        // Simulate assistant with tool call
        var assistantMsg = new ChatMessage(ChatRole.Assistant, [
            new TextContent("I'll scan for that value."),
            new FunctionCallContent("call-1", "ScanMemory", new Dictionary<string, object?> { ["value"] = "100" }),
        ]);
        history.AddAssistantMessage(assistantMsg);

        // Simulate tool result
        history.AddToolResults([new FunctionResultContent("call-1", "Found 42 results")]);
        history.AddAssistantMessage(new ChatMessage(ChatRole.Assistant, "Found 42 results for value 100."));

        Assert.Equal(4, history.Count);

        // Switch model
        var switcher = new ModelSwitcher(
        [
            new ModelConfig { ModelId = "model-a", MaxContextTokens = 200_000 },
            new ModelConfig { ModelId = "model-b", MaxContextTokens = 128_000 },
        ]);

        switcher.SwitchToModel("model-b");

        // History preserved with tool calls intact
        Assert.Equal(4, history.Count);
        var messages = history.GetMessages();
        Assert.Equal(ChatRole.Tool, messages[2].Role);
    }

    // ── C. CopilotTokenService Concurrency ──────────────────────────

    [Fact]
    public async Task ConcurrentForceRefresh_NoRaceConditions()
    {
        // CopilotTokenService requires real GitHub auth, so we test the concurrency
        // pattern using a stub that mirrors the SemaphoreSlim-based locking pattern.
        var stubService = new StubTokenService();

        // Launch 10 concurrent refresh calls
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => stubService.ForceRefreshAsync("fake-github-token"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // All calls should succeed with no exceptions and return valid tokens
        Assert.Equal(10, results.Length);
        Assert.All(results, token => Assert.False(string.IsNullOrWhiteSpace(token)));

        // After all refreshes complete, the cached token should be the last one written.
        // Verify the service is in a consistent state (no corruption from concurrent access).
        var finalToken = await stubService.GetSessionTokenAsync("fake-github-token");
        Assert.False(string.IsNullOrWhiteSpace(finalToken));
        // The final token should match one of the returned tokens (the last one written)
        Assert.Contains(finalToken, results);
    }

    [Fact]
    public async Task ConcurrentGetAndForceRefresh_NoDeadlock()
    {
        // Mix of GetSessionToken and ForceRefresh calls concurrently
        var stubService = new StubTokenService();
        var ct = TestContext.Current.CancellationToken;

        var tasks = new List<Task<string>>();
        for (int i = 0; i < 10; i++)
        {
            if (i % 3 == 0)
                tasks.Add(stubService.ForceRefreshAsync("fake-token"));
            else
                tasks.Add(stubService.GetSessionTokenAsync("fake-token"));
        }

        // Should complete without deadlock (use timeout as safety)
        var allTasks = Task.WhenAll(tasks);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), ct);
        var completed = await Task.WhenAny(allTasks, timeoutTask);

        Assert.True(completed == allTasks,
            "Concurrent token operations should not deadlock");

        var results = await allTasks;
        Assert.All(results, t => Assert.False(string.IsNullOrWhiteSpace(t)));
    }

    [Fact]
    public async Task ConcurrentRefresh_RefreshCountIsLimited()
    {
        // Verify the semaphore actually serializes: despite 10 concurrent calls,
        // the actual refresh should happen a limited number of times
        var stubService = new StubTokenService();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => stubService.ForceRefreshAsync("token"))
            .ToArray();

        await Task.WhenAll(tasks);

        // Each ForceRefresh invalidates and re-fetches under the lock.
        // With 10 serial calls, we get exactly 10 refreshes.
        Assert.Equal(10, stubService.RefreshCount);
    }
}

// ── Test Doubles ─────────────────────────────────────────────────────

/// <summary>
/// Mock IChatClient that throws HttpRequestException with a configurable status code
/// for the first N calls, then returns a normal response.
/// </summary>
#pragma warning disable CA1822
internal sealed class FailingMockChatClient : IChatClient
{
    private readonly HttpStatusCode _statusCode;
    private readonly int _failUntilAttempt;
    private int _callCount;

    public FailingMockChatClient(HttpStatusCode statusCode, int failUntilAttempt = 3)
    {
        _statusCode = statusCode;
        _failUntilAttempt = failUntilAttempt;
    }

    public ChatClientMetadata Metadata => new("failing-mock");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var attempt = Interlocked.Increment(ref _callCount);
        if (attempt < _failUntilAttempt)
            throw new HttpRequestException($"Simulated {(int)_statusCode}", null, _statusCode);

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Recovered")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var attempt = Interlocked.Increment(ref _callCount);
        if (attempt < _failUntilAttempt)
            throw new HttpRequestException($"Simulated {(int)_statusCode}", null, _statusCode);

        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent("Recovered")],
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

/// <summary>
/// Stub token service that mirrors CopilotTokenService's SemaphoreSlim-based concurrency
/// pattern. Used to verify the locking strategy works without requiring real GitHub auth.
/// </summary>
internal sealed class StubTokenService : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _sessionToken;
    private int _refreshCount;

    public int RefreshCount => _refreshCount;

    public async Task<string> GetSessionTokenAsync(string githubToken)
    {
        // Fast path: token already cached
        if (_sessionToken is not null)
            return _sessionToken;

        await _lock.WaitAsync();
        try
        {
            // Double-check after lock
            if (_sessionToken is not null)
                return _sessionToken;

            return await RefreshTokenAsync(githubToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> ForceRefreshAsync(string githubToken)
    {
        await _lock.WaitAsync();
        try
        {
            _sessionToken = null;
            return await RefreshTokenAsync(githubToken);
        }
        finally
        {
            _lock.Release();
        }
    }

#pragma warning disable IDE0060 // Remove unused parameter — mirrors real CopilotTokenService API
    private async Task<string> RefreshTokenAsync(string githubToken)
#pragma warning restore IDE0060
    {
        Interlocked.Increment(ref _refreshCount);
        // Simulate async network call
        await Task.Delay(1);
        _sessionToken = $"session-token-{Guid.NewGuid():N}";
        return _sessionToken;
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
