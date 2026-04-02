using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Wraps a streaming LLM response with an idle timeout watchdog.
/// If no streaming chunks arrive within <see cref="IdleTimeout"/>,
/// throws <see cref="StreamingTimeoutException"/> so the retry policy
/// can fall back to a non-streaming request.
///
/// Modeled after Claude Code's 90-second streaming watchdog.
/// </summary>
public static class StreamingWatchdog
{
    /// <summary>
    /// Wrap a streaming response with idle timeout monitoring.
    /// </summary>
    public static async IAsyncEnumerable<ChatResponseUpdate> WithIdleTimeout(
        IAsyncEnumerable<ChatResponseUpdate> source,
        TimeSpan idleTimeout,
        Action<string, string>? log = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Race: MoveNextAsync vs timeout
                var moveNext = enumerator.MoveNextAsync();

                if (moveNext.IsCompleted)
                {
                    // Fast path — already have a result
                    if (!moveNext.Result)
                        yield break;
                    yield return enumerator.Current;
                    continue;
                }

                // Slow path — need to wait with timeout
                var moveNextTask = moveNext.AsTask();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var timeoutTask = Task.Delay(idleTimeout, timeoutCts.Token);

                var completed = await Task.WhenAny(moveNextTask, timeoutTask);

                if (completed == timeoutTask && !moveNextTask.IsCompleted)
                {
                    log?.Invoke("WATCHDOG", $"Streaming idle for {idleTimeout.TotalSeconds:F0}s — aborting stream");
                    throw new StreamingTimeoutException(
                        $"No streaming data received for {idleTimeout.TotalSeconds:F0} seconds.");
                }

                // Cancel the timeout since we got data
                await timeoutCts.CancelAsync();

                if (!await moveNextTask)
                    yield break;

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}

/// <summary>
/// Thrown when the streaming response has been idle for longer than the configured timeout.
/// The retry policy treats this as retriable — it will attempt a non-streaming fallback
/// or retry the streaming request.
/// </summary>
public sealed class StreamingTimeoutException : Exception
{
    public StreamingTimeoutException(string message) : base(message) { }
    public StreamingTimeoutException(string message, Exception inner) : base(message, inner) { }
}
