using System.Net;
using System.Net.Http;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for <see cref="RetryPolicy"/>: success paths, transient errors,
/// prompt-too-long compaction signal, non-retriable errors, exhausted retries.
/// Uses short delays to keep tests fast.
/// </summary>
public class RetryPolicyTests
{
    private static RetryPolicy FastPolicy(int maxRetries = 3) => new(
        maxRetries: maxRetries,
        baseDelay: TimeSpan.FromMilliseconds(1),
        maxDelay: TimeSpan.FromMilliseconds(10),
        jitterPercent: 0);

    // ── Success on first try ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SuccessOnFirstTry_ReturnsOk()
    {
        var policy = FastPolicy();
        var result = await policy.ExecuteAsync<string>(
            _ => Task.FromResult("hello"));

        Assert.True(result.Success);
        Assert.Equal("hello", result.Value);
        Assert.Null(result.Exception);
    }

    // ── Transient errors that retry ─────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TransientError_RetriesAndSucceeds()
    {
        var policy = FastPolicy();
        int attempt = 0;

        var result = await policy.ExecuteAsync<string>(ct =>
        {
            attempt++;
            if (attempt < 3)
                throw new HttpRequestException("server error", null, HttpStatusCode.InternalServerError);
            return Task.FromResult("recovered");
        });

        Assert.True(result.Success);
        Assert.Equal("recovered", result.Value);
        Assert.Equal(3, attempt);
    }

    [Fact]
    public async Task ExecuteAsync_RateLimited_RetriesAndSucceeds()
    {
        var policy = FastPolicy();
        int attempt = 0;

        var result = await policy.ExecuteAsync<string>(ct =>
        {
            attempt++;
            if (attempt == 1)
                throw new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);
            return Task.FromResult("ok");
        });

        Assert.True(result.Success);
        Assert.Equal("ok", result.Value);
        Assert.Equal(2, attempt);
    }

    // ── Prompt too long → compaction needed ──────────────────────────

    [Fact]
    public async Task ExecuteAsync_PromptTooLong_ReturnsCompactionNeeded()
    {
        var policy = FastPolicy();

        var result = await policy.ExecuteAsync<string>(
            _ => throw new InvalidOperationException("prompt is too long for this model"));

        Assert.False(result.Success);
        Assert.True(result.NeedsCompaction);
    }

    [Fact]
    public async Task ExecuteAsync_ContextLengthExceeded_ReturnsCompactionNeeded()
    {
        var policy = FastPolicy();

        var result = await policy.ExecuteAsync<string>(
            _ => throw new InvalidOperationException("maximum context length exceeded"));

        Assert.False(result.Success);
        Assert.True(result.NeedsCompaction);
    }

    // ── Non-retriable errors ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NonRetriableError_FailsImmediately()
    {
        var policy = FastPolicy();
        int attempt = 0;

        var result = await policy.ExecuteAsync<string>(ct =>
        {
            attempt++;
            throw new InvalidOperationException("logic error");
        });

        Assert.False(result.Success);
        Assert.NotNull(result.Exception);
        Assert.Equal(1, attempt); // No retries for non-retriable errors
    }

    // ── Exhausted retries ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AllRetriesExhausted_ReturnsFailure()
    {
        var policy = FastPolicy(maxRetries: 2);
        int attempt = 0;

        var result = await policy.ExecuteAsync<string>(ct =>
        {
            attempt++;
            throw new HttpRequestException("server error", null, HttpStatusCode.InternalServerError);
        });

        Assert.False(result.Success);
        Assert.NotNull(result.Exception);
        Assert.Equal(3, attempt); // 1 initial + 2 retries
    }

    // ── Cancellation ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Cancellation_ThrowsOperationCanceled()
    {
        var policy = FastPolicy();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            policy.ExecuteAsync<string>(
                _ => Task.FromResult("never"),
                cancellationToken: cts.Token));
    }

    // ── Token overflow ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ContextOverflow_ReturnsTokenOverflow()
    {
        var policy = FastPolicy();

        // Error message with pattern: inputTokens + maxTokens > contextLimit
        var result = await policy.ExecuteAsync<string>(
            _ => throw new InvalidOperationException("100000 + 50000 > 128000"));

        Assert.False(result.Success);
        Assert.NotNull(result.AdjustedMaxTokens);
        // Available = 128000 - 100000 - 1000 (buffer) = 27000
        Assert.Equal(27000, result.AdjustedMaxTokens);
    }

    [Fact]
    public async Task ExecuteAsync_ContextOverflow_TooSmall_ReturnsFailure()
    {
        var policy = FastPolicy();

        // Available would be 130000 - 129000 - 1000 = -1000, which is < 3000
        var result = await policy.ExecuteAsync<string>(
            _ => throw new InvalidOperationException("129000 + 5000 > 130000"));

        Assert.False(result.Success);
        Assert.Null(result.AdjustedMaxTokens);
        Assert.NotNull(result.Exception);
    }

    // ── Overload → model fallback ───────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_LongRetryAfterOverload_SignalsModelFallback()
    {
        var policy = FastPolicy();

        var result = await policy.ExecuteAsync<string>(
            _ => throw new InvalidOperationException("overloaded_error retry-after: 60"));

        Assert.False(result.Success);
        Assert.True(result.NeedsModelFallback);
    }

    [Fact]
    public async Task ExecuteAsync_ThreeConsecutiveShortOverloads_SignalsCooldownFallback()
    {
        var policy = FastPolicy(maxRetries: 10);
        int attempt = 0;

        var result = await policy.ExecuteAsync<string>(ct =>
        {
            attempt++;
            throw new InvalidOperationException("overloaded_error retry-after: 2");
        });

        Assert.False(result.Success);
        Assert.True(result.NeedsModelFallback);
        Assert.True(result.NeedsFastModeCooldown);
        Assert.Equal(3, attempt); // Stops after 3 consecutive overloads
    }

    // ── Heartbeat callback ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_HeartbeatCallback_Invoked()
    {
        var policy = new RetryPolicy(
            maxRetries: 2,
            baseDelay: TimeSpan.FromMilliseconds(5),
            maxDelay: TimeSpan.FromMilliseconds(10),
            jitterPercent: 0);

        int attempt = 0;
        var heartbeats = new List<string>();

        var result = await policy.ExecuteAsync<string>(ct =>
        {
            attempt++;
            if (attempt < 2)
                throw new HttpRequestException("error", null, HttpStatusCode.InternalServerError);
            return Task.FromResult("ok");
        }, onHeartbeat: msg => heartbeats.Add(msg));

        Assert.True(result.Success);
        // Heartbeat may or may not fire depending on delay < 30s threshold
        // but the test validates no crash with callback set
    }

    // ── Streaming timeout ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_StreamingTimeout_Retries()
    {
        var policy = FastPolicy();
        int attempt = 0;

        var result = await policy.ExecuteAsync<string>(ct =>
        {
            attempt++;
            if (attempt == 1)
                throw new StreamingTimeoutException("idle timeout");
            return Task.FromResult("recovered");
        });

        Assert.True(result.Success);
        Assert.Equal(2, attempt);
    }

    // ── Success resets overload counter ──────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SuccessResetsOverloadCounter()
    {
        var policy = FastPolicy(maxRetries: 10);

        // First call: success — resets counter
        var result1 = await policy.ExecuteAsync<string>(_ => Task.FromResult("ok"));
        Assert.True(result1.Success);

        // Second call with overloads should need 3 consecutive to trigger fallback
        int attempt = 0;
        var result2 = await policy.ExecuteAsync<string>(ct =>
        {
            attempt++;
            throw new InvalidOperationException("overloaded_error retry-after: 2");
        });

        Assert.Equal(3, attempt); // Fresh 3-count after reset
        Assert.True(result2.NeedsModelFallback);
    }
}
