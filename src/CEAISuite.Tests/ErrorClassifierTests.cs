using System.Net;
using System.Net.Http;
using CEAISuite.Application.AgentLoop;

#pragma warning disable CA2201 // Exception type System.Exception is not sufficiently specific

namespace CEAISuite.Tests;

public class ErrorClassifierTests
{
    // ── IsRateLimited ──

    [Fact]
    public void IsRateLimited_HttpRequestException429_ReturnsTrue()
    {
        var ex = new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);
        Assert.True(ErrorClassifier.IsRateLimited(ex));
    }

    [Fact]
    public void IsRateLimited_OtherStatus_ReturnsFalse()
    {
        var ex = new HttpRequestException("not found", null, HttpStatusCode.NotFound);
        Assert.False(ErrorClassifier.IsRateLimited(ex));
    }

    // ── IsOverloaded ──

    [Fact]
    public void IsOverloaded_Status503_ReturnsTrue()
    {
        var ex = new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable);
        Assert.True(ErrorClassifier.IsOverloaded(ex));
    }

    [Fact]
    public void IsOverloaded_MessageContainsOverloaded_ReturnsTrue()
    {
        var ex = new Exception("overloaded_error: try again later");
        Assert.True(ErrorClassifier.IsOverloaded(ex));
    }

    [Fact]
    public void IsOverloaded_MessageContainsCapacity_ReturnsTrue()
    {
        var ex = new Exception("insufficient capacity");
        Assert.True(ErrorClassifier.IsOverloaded(ex));
    }

    [Fact]
    public void IsOverloaded_NormalError_ReturnsFalse()
    {
        var ex = new Exception("something else");
        Assert.False(ErrorClassifier.IsOverloaded(ex));
    }

    // ── IsPromptTooLong ──

    [Fact]
    public void IsPromptTooLong_Status413_ReturnsTrue()
    {
        var ex = new HttpRequestException("too large", null, HttpStatusCode.RequestEntityTooLarge);
        Assert.True(ErrorClassifier.IsPromptTooLong(ex));
    }

    [Fact]
    public void IsPromptTooLong_MessageContainsPromptTooLong_ReturnsTrue()
    {
        var ex = new Exception("prompt is too long for this model");
        Assert.True(ErrorClassifier.IsPromptTooLong(ex));
    }

    [Fact]
    public void IsPromptTooLong_MessageContainsMaximumContextLength_ReturnsTrue()
    {
        var ex = new Exception("maximum context length exceeded");
        Assert.True(ErrorClassifier.IsPromptTooLong(ex));
    }

    [Fact]
    public void IsPromptTooLong_MessageContainsInputTooLong_ReturnsTrue()
    {
        var ex = new Exception("input is too long");
        Assert.True(ErrorClassifier.IsPromptTooLong(ex));
    }

    // ── IsAuthError ──

    [Fact]
    public void IsAuthError_Status401_ReturnsTrue()
    {
        var ex = new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);
        Assert.True(ErrorClassifier.IsAuthError(ex));
    }

    [Fact]
    public void IsAuthError_Status403_ReturnsTrue()
    {
        var ex = new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden);
        Assert.True(ErrorClassifier.IsAuthError(ex));
    }

    [Fact]
    public void IsAuthError_Status200_ReturnsFalse()
    {
        var ex = new Exception("ok");
        Assert.False(ErrorClassifier.IsAuthError(ex));
    }

    // ── IsConnectionError ──

    [Fact]
    public void IsConnectionError_HttpRequestException_ReturnsTrue()
    {
        var ex = new HttpRequestException("connection reset");
        Assert.True(ErrorClassifier.IsConnectionError(ex));
    }

    [Fact]
    public void IsConnectionError_TaskCancelledWithTimeout_ReturnsTrue()
    {
        var ex = new TaskCanceledException("timed out", new TimeoutException());
        Assert.True(ErrorClassifier.IsConnectionError(ex));
    }

    [Fact]
    public void IsConnectionError_MessageContainsEconnreset_ReturnsTrue()
    {
        var ex = new Exception("ECONNRESET detected");
        Assert.True(ErrorClassifier.IsConnectionError(ex));
    }

    [Fact]
    public void IsConnectionError_NormalException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("bad state");
        Assert.False(ErrorClassifier.IsConnectionError(ex));
    }

    // ── IsServerError ──

    [Fact]
    public void IsServerError_Status500_ReturnsTrue()
    {
        var ex = new HttpRequestException("internal error", null, HttpStatusCode.InternalServerError);
        Assert.True(ErrorClassifier.IsServerError(ex));
    }

    [Fact]
    public void IsServerError_Status503_ReturnsFalse_HandledAsOverloaded()
    {
        var ex = new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable);
        Assert.False(ErrorClassifier.IsServerError(ex));
    }

    [Fact]
    public void IsServerError_Status404_ReturnsFalse()
    {
        var ex = new HttpRequestException("not found", null, HttpStatusCode.NotFound);
        Assert.False(ErrorClassifier.IsServerError(ex));
    }

    // ── IsRetriable ──

    [Fact]
    public void IsRetriable_RateLimited_ReturnsTrue()
    {
        var ex = new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);
        Assert.True(ErrorClassifier.IsRetriable(ex));
    }

    [Fact]
    public void IsRetriable_NonRetriable_ReturnsFalse()
    {
        var ex = new InvalidOperationException("bad logic");
        Assert.False(ErrorClassifier.IsRetriable(ex));
    }

    // ── IsMaxOutputTokens ──

    [Theory]
    [InlineData("length", true)]
    [InlineData("max_tokens", true)]
    [InlineData("max_output_tokens", true)]
    [InlineData("token_limit", true)]
    [InlineData("LENGTH", true)]
    [InlineData("stop", false)]
    [InlineData("end_turn", false)]
    public void IsMaxOutputTokens_VariousReasons(string? reason, bool expected)
    {
        Assert.Equal(expected, ErrorClassifier.IsMaxOutputTokens(reason));
    }

    [Fact]
    public void IsMaxOutputTokens_Null_ReturnsFalse()
    {
        Assert.False(ErrorClassifier.IsMaxOutputTokens(null));
    }

    // ── ParseRetryAfter ──

    [Fact]
    public void ParseRetryAfter_WithRetryAfterHeader_ReturnsTimeSpan()
    {
        var ex = new Exception("retry-after: 30");
        var result = ErrorClassifier.ParseRetryAfter(ex);
        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Value);
    }

    [Fact]
    public void ParseRetryAfter_NoHeader_ReturnsNull()
    {
        var ex = new Exception("something else entirely");
        Assert.Null(ErrorClassifier.ParseRetryAfter(ex));
    }

    [Fact]
    public void ParseRetryAfter_DecimalSeconds_Parses()
    {
        var ex = new Exception("Retry-After: 1.5");
        var result = ErrorClassifier.ParseRetryAfter(ex);
        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromSeconds(1.5), result.Value);
    }

    // ── ParseMaxTokensOverflow ──

    [Fact]
    public void ParseMaxTokensOverflow_ValidPattern_ReturnsTriple()
    {
        var ex = new Exception("This model's max context is 128000 + 4096 > 128000");
        var result = ErrorClassifier.ParseMaxTokensOverflow(ex);
        Assert.NotNull(result);
        Assert.Equal(128000, result.Value.inputTokens);
        Assert.Equal(4096, result.Value.maxTokens);
        Assert.Equal(128000, result.Value.contextLimit);
    }

    [Fact]
    public void ParseMaxTokensOverflow_NoPattern_ReturnsNull()
    {
        var ex = new Exception("no overflow here");
        Assert.Null(ErrorClassifier.ParseMaxTokensOverflow(ex));
    }

    // ── GetStatusCode ──

    [Fact]
    public void GetStatusCode_HttpRequestException_ReturnsCode()
    {
        var ex = new HttpRequestException("not found", null, HttpStatusCode.NotFound);
        Assert.Equal(404, ErrorClassifier.GetStatusCode(ex));
    }

    [Fact]
    public void GetStatusCode_InnerException_RecursesInto()
    {
        var inner = new HttpRequestException("inner", null, HttpStatusCode.BadGateway);
        var outer = new Exception("outer", inner);
        Assert.Equal(502, ErrorClassifier.GetStatusCode(outer));
    }

    [Fact]
    public void GetStatusCode_NoStatusCode_ReturnsNull()
    {
        var ex = new InvalidOperationException("no status");
        Assert.Null(ErrorClassifier.GetStatusCode(ex));
    }
}
