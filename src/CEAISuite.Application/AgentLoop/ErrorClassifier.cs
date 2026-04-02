using System.Net;
using System.Text.RegularExpressions;

namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Classifies exceptions from LLM providers into actionable categories
/// for the retry policy. Works with OpenAI SDK (<c>ClientResultException</c>),
/// Anthropic SDK exceptions, and generic HTTP errors.
/// </summary>
public static partial class ErrorClassifier
{
    /// <summary>HTTP 429 Too Many Requests.</summary>
    public static bool IsRateLimited(Exception ex)
        => GetStatusCode(ex) == 429;

    /// <summary>HTTP 529 Overloaded (Anthropic-specific).</summary>
    public static bool IsOverloaded(Exception ex)
        => GetStatusCode(ex) == 529
           || ex.Message.Contains("overloaded_error", StringComparison.OrdinalIgnoreCase);

    /// <summary>HTTP 413 or error message indicating prompt exceeds context window.</summary>
    public static bool IsPromptTooLong(Exception ex)
        => GetStatusCode(ex) == 413
           || ex.Message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("input is too long", StringComparison.OrdinalIgnoreCase);

    /// <summary>HTTP 401 Unauthorized or 403 Forbidden.</summary>
    public static bool IsAuthError(Exception ex)
    {
        var status = GetStatusCode(ex);
        return status is 401 or 403;
    }

    /// <summary>Connection reset, timeout, or network-level failure.</summary>
    public static bool IsConnectionError(Exception ex)
        => ex is HttpRequestException
           || ex is TaskCanceledException { InnerException: TimeoutException }
           || ex.Message.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("EPIPE", StringComparison.OrdinalIgnoreCase);

    /// <summary>HTTP 5xx server error.</summary>
    public static bool IsServerError(Exception ex)
    {
        var status = GetStatusCode(ex);
        return status >= 500 && status < 600 && status != 529;
    }

    /// <summary>
    /// Whether an exception is retriable at all (any of: rate limit, overloaded,
    /// connection error, server error, auth error, timeout).
    /// </summary>
    public static bool IsRetriable(Exception ex)
        => IsRateLimited(ex) || IsOverloaded(ex) || IsConnectionError(ex)
           || IsServerError(ex) || IsAuthError(ex) || IsStreamingTimeout(ex);

    /// <summary>Whether the exception is a streaming idle timeout.</summary>
    public static bool IsStreamingTimeout(Exception ex)
        => ex is StreamingTimeoutException;

    /// <summary>
    /// Check if a chat response indicates max output tokens was hit
    /// (finish_reason = "length" or "max_tokens").
    /// </summary>
    public static bool IsMaxOutputTokens(string? finishReason)
        => string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)
           || string.Equals(finishReason, "max_tokens", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parse retry-after header value from an exception's message or data.
    /// Returns null if not found.
    /// </summary>
    public static TimeSpan? ParseRetryAfter(Exception ex)
    {
        // Try common patterns: "retry-after: N", "Retry-After: N", "retry after N seconds"
        var match = RetryAfterPattern().Match(ex.Message);
        if (match.Success && double.TryParse(match.Groups[1].Value, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        return null;
    }

    /// <summary>
    /// Parse a "max_tokens context overflow" error: "X + Y > Z" pattern.
    /// Returns (inputTokens, maxTokens, contextLimit) or null.
    /// </summary>
    public static (int inputTokens, int maxTokens, int contextLimit)? ParseMaxTokensOverflow(Exception ex)
    {
        var match = ContextOverflowPattern().Match(ex.Message);
        if (match.Success
            && int.TryParse(match.Groups[1].Value, out var input)
            && int.TryParse(match.Groups[2].Value, out var max)
            && int.TryParse(match.Groups[3].Value, out var limit))
        {
            return (input, max, limit);
        }
        return null;
    }

    /// <summary>
    /// Extract HTTP status code from an exception. Inspects known SDK exception
    /// types via reflection to avoid hard dependencies on provider-specific types.
    /// </summary>
    public static int? GetStatusCode(Exception ex)
    {
        // OpenAI SDK: ClientResultException.Status (int)
        var statusProp = ex.GetType().GetProperty("Status");
        if (statusProp?.GetValue(ex) is int status && status > 0)
            return status;

        // Anthropic SDK: AnthropicException.StatusCode or HttpStatusCode
        var statusCodeProp = ex.GetType().GetProperty("StatusCode");
        if (statusCodeProp?.GetValue(ex) is HttpStatusCode httpStatus)
            return (int)httpStatus;
        if (statusCodeProp?.GetValue(ex) is int sc && sc > 0)
            return sc;

        // Generic HttpRequestException
        if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
            return (int)httpEx.StatusCode.Value;

        // Recurse into inner exception
        if (ex.InnerException is not null)
            return GetStatusCode(ex.InnerException);

        return null;
    }

    [GeneratedRegex(@"[Rr]etry[-_ ]?[Aa]fter[:\s]+(\d+\.?\d*)", RegexOptions.Compiled)]
    private static partial Regex RetryAfterPattern();

    [GeneratedRegex(@"(\d+)\s*\+\s*(\d+)\s*>\s*(\d+)", RegexOptions.Compiled)]
    private static partial Regex ContextOverflowPattern();
}
