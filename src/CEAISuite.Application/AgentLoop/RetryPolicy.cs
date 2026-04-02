using System.Diagnostics;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Retry policy with exponential backoff, jitter, and retry-after header support.
/// Modeled after Claude Code's withRetry.ts (10 max retries, 500ms base, 32s cap, 25% jitter).
/// </summary>
public sealed class RetryPolicy
{
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly double _jitterPercent;
    private readonly Action<string, string>? _log;
    private int _consecutive529Count;

    public RetryPolicy(
        int maxRetries = 10,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        double jitterPercent = 0.25,
        Action<string, string>? log = null)
    {
        _maxRetries = maxRetries;
        _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(500);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(32);
        _jitterPercent = jitterPercent;
        _log = log;
    }

    /// <summary>
    /// Result of a retry-wrapped operation. Contains either the result or
    /// a signal that a special recovery path is needed.
    /// </summary>
    public sealed record RetryResult<T>
    {
        public T? Value { get; init; }
        public bool Success { get; init; }

        /// <summary>True if the error was prompt-too-long and compaction should be attempted.</summary>
        public bool NeedsCompaction { get; init; }

        /// <summary>True if max output tokens overflow was detected; contains adjusted max tokens.</summary>
        public int? AdjustedMaxTokens { get; init; }

        /// <summary>The final exception if all retries exhausted.</summary>
        public Exception? Exception { get; init; }

        public static RetryResult<T> Ok(T value) => new() { Value = value, Success = true };
        public static RetryResult<T> CompactionNeeded() => new() { NeedsCompaction = true };
        public static RetryResult<T> TokenOverflow(int adjustedMax) => new() { AdjustedMaxTokens = adjustedMax };
        public bool NeedsModelFallback { get; init; }

        public static RetryResult<T> Failure(Exception ex) => new() { Exception = ex };
        public static RetryResult<T> ModelFallback() => new() { NeedsModelFallback = true };
    }

    /// <summary>
    /// Execute an async operation with retry logic. Returns a <see cref="RetryResult{T}"/>
    /// that may signal special recovery paths (compaction, token adjustment) instead of
    /// throwing on all errors.
    /// </summary>
    public async Task<RetryResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Action<string>? onHeartbeat = null,
        CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= _maxRetries + 1; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await operation(cancellationToken);
                _consecutive529Count = 0; // Reset on success
                return RetryResult<T>.Ok(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // User cancellation — don't retry
            }
            catch (Exception ex)
            {
                lastException = ex;
                _log?.Invoke("RETRY", $"Attempt {attempt}/{_maxRetries + 1} failed: {ex.GetType().Name}: {Truncate(ex.Message, 200)}");

                // Prompt too long → signal compaction, don't retry
                if (ErrorClassifier.IsPromptTooLong(ex))
                {
                    _log?.Invoke("RETRY", "Prompt too long detected — signaling compaction");
                    return RetryResult<T>.CompactionNeeded();
                }

                // Max tokens context overflow → calculate adjusted max and signal
                var overflow = ErrorClassifier.ParseMaxTokensOverflow(ex);
                if (overflow.HasValue)
                {
                    var (input, _, limit) = overflow.Value;
                    var available = limit - input - 1000; // safety buffer
                    if (available < 3000)
                    {
                        _log?.Invoke("RETRY", $"Context overflow unrecoverable: only {available} tokens available");
                        return RetryResult<T>.Failure(ex);
                    }
                    _log?.Invoke("RETRY", $"Context overflow — adjusting max_tokens to {available}");
                    return RetryResult<T>.TokenOverflow(available);
                }

                // Track consecutive 529 overloaded errors → model fallback
                if (ErrorClassifier.IsOverloaded(ex))
                {
                    _consecutive529Count++;
                    _log?.Invoke("RETRY", $"Consecutive 529: {_consecutive529Count}");
                    if (_consecutive529Count >= 3)
                    {
                        _log?.Invoke("RETRY", "3 consecutive 529s — signaling model fallback");
                        return RetryResult<T>.ModelFallback();
                    }
                }
                else
                {
                    _consecutive529Count = 0; // Reset on non-529 error
                }

                // Non-retriable errors → fail immediately
                if (!ErrorClassifier.IsRetriable(ex))
                {
                    _log?.Invoke("RETRY", $"Non-retriable error: {ex.GetType().Name}");
                    return RetryResult<T>.Failure(ex);
                }

                // Last attempt — don't sleep, just fail
                if (attempt > _maxRetries)
                    break;

                // Calculate delay
                var delay = CalculateDelay(attempt, ErrorClassifier.ParseRetryAfter(ex));
                _log?.Invoke("RETRY", $"Retrying in {delay.TotalSeconds:F1}s (attempt {attempt + 1})");

                // Chunk long waits into heartbeat intervals (30s) so the UI stays alive
                var remaining = delay;
                var heartbeatInterval = TimeSpan.FromSeconds(30);
                while (remaining > TimeSpan.Zero)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sleepTime = remaining < heartbeatInterval ? remaining : heartbeatInterval;
                    await Task.Delay(sleepTime, cancellationToken);
                    remaining -= sleepTime;
                    if (remaining > TimeSpan.Zero)
                        onHeartbeat?.Invoke($"Retrying in {remaining.TotalSeconds:F0}s...");
                }
            }
        }

        _log?.Invoke("RETRY", $"All {_maxRetries + 1} attempts exhausted");
        return RetryResult<T>.Failure(lastException ?? new InvalidOperationException("Retry exhausted with no exception"));
    }

    private TimeSpan CalculateDelay(int attempt, TimeSpan? retryAfter)
    {
        // Honor retry-after header if present
        if (retryAfter.HasValue)
            return retryAfter.Value;

        // Exponential backoff: baseDelay * 2^(attempt-1), capped at maxDelay
        var exponential = _baseDelay * Math.Pow(2, attempt - 1);
        var capped = exponential > _maxDelay ? _maxDelay : exponential;

        // Add jitter: ±jitterPercent of the delay
        var jitter = Random.Shared.NextDouble() * _jitterPercent * capped.TotalMilliseconds;
        return capped + TimeSpan.FromMilliseconds(jitter);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");
}
