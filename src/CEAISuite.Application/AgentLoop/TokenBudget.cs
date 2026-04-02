namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Real-time token usage tracking and budget enforcement.
///
/// Modeled after Claude Code's cost tracking which displays cumulative
/// token usage and can enforce session/monthly budgets.
///
/// Tracks input tokens, output tokens, cached tokens, and computes
/// estimated cost based on configurable pricing. Emits warnings when
/// approaching budget limits and can hard-stop at the ceiling.
/// </summary>
public sealed class TokenBudget
{
    private readonly Action<string, string>? _log;
    private readonly object _lock = new();

    // Cumulative counters
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private long _totalCachedTokens;
    private int _totalRequests;
    private int _totalToolCalls;

    // Budget limits (0 = unlimited)
    private long _maxInputTokens;
    private long _maxOutputTokens;
    private decimal _maxCostDollars;

    // Pricing (per million tokens, configurable per provider)
    private decimal _inputPricePerMillion = 3.00m;   // Default: Claude Sonnet-class
    private decimal _outputPricePerMillion = 15.00m;
    private decimal _cachedInputPricePerMillion = 0.30m;

    public TokenBudget(Action<string, string>? log = null) => _log = log;

    // ── Read-only accessors ──

    public long TotalInputTokens => Interlocked.Read(ref _totalInputTokens);
    public long TotalOutputTokens => Interlocked.Read(ref _totalOutputTokens);
    public long TotalCachedTokens => Interlocked.Read(ref _totalCachedTokens);
    public int TotalRequests => _totalRequests;
    public int TotalToolCalls => _totalToolCalls;

    /// <summary>Estimated session cost in USD.</summary>
    public decimal EstimatedCostUsd
    {
        get
        {
            var input = TotalInputTokens;
            var cached = TotalCachedTokens;
            var output = TotalOutputTokens;
            var nonCachedInput = Math.Max(0, input - cached);

            return (nonCachedInput / 1_000_000m) * _inputPricePerMillion
                 + (cached / 1_000_000m) * _cachedInputPricePerMillion
                 + (output / 1_000_000m) * _outputPricePerMillion;
        }
    }

    /// <summary>Cache hit rate as a percentage (0-100).</summary>
    public double CacheHitRate
    {
        get
        {
            var input = TotalInputTokens;
            return input > 0 ? TotalCachedTokens * 100.0 / input : 0;
        }
    }

    /// <summary>Whether the budget has been exceeded.</summary>
    public bool IsExhausted
    {
        get
        {
            if (_maxInputTokens > 0 && TotalInputTokens >= _maxInputTokens) return true;
            if (_maxOutputTokens > 0 && TotalOutputTokens >= _maxOutputTokens) return true;
            if (_maxCostDollars > 0 && EstimatedCostUsd >= _maxCostDollars) return true;
            return false;
        }
    }

    /// <summary>Remaining budget as a fraction (0.0-1.0). Returns 1.0 if no budget set.</summary>
    public double RemainingBudgetFraction
    {
        get
        {
            var fractions = new List<double>();
            if (_maxInputTokens > 0)
                fractions.Add(1.0 - (double)TotalInputTokens / _maxInputTokens);
            if (_maxOutputTokens > 0)
                fractions.Add(1.0 - (double)TotalOutputTokens / _maxOutputTokens);
            if (_maxCostDollars > 0)
                fractions.Add(1.0 - (double)(EstimatedCostUsd / _maxCostDollars));

            return fractions.Count > 0 ? Math.Max(0, fractions.Min()) : 1.0;
        }
    }

    // ── Configuration ──

    /// <summary>Set token budget limits. 0 = unlimited.</summary>
    public void SetLimits(long maxInputTokens = 0, long maxOutputTokens = 0, decimal maxCostDollars = 0)
    {
        _maxInputTokens = maxInputTokens;
        _maxOutputTokens = maxOutputTokens;
        _maxCostDollars = maxCostDollars;
        _log?.Invoke("BUDGET", $"Limits set: input={maxInputTokens}, output={maxOutputTokens}, cost=${maxCostDollars:F2}");
    }

    /// <summary>Set pricing (per million tokens).</summary>
    public void SetPricing(decimal inputPerMillion, decimal outputPerMillion, decimal cachedInputPerMillion)
    {
        _inputPricePerMillion = inputPerMillion;
        _outputPricePerMillion = outputPerMillion;
        _cachedInputPricePerMillion = cachedInputPerMillion;
    }

    // ── Tracking ──

    /// <summary>
    /// Record token usage from an LLM response. Called by AgentLoop after each API call.
    /// Returns a <see cref="BudgetCheckResult"/> indicating whether to continue.
    /// </summary>
    public BudgetCheckResult RecordUsage(long inputTokens, long outputTokens, long cachedTokens)
    {
        Interlocked.Add(ref _totalInputTokens, inputTokens);
        Interlocked.Add(ref _totalOutputTokens, outputTokens);
        Interlocked.Add(ref _totalCachedTokens, cachedTokens);
        Interlocked.Increment(ref _totalRequests);

        var cost = EstimatedCostUsd;
        var cacheRate = CacheHitRate;

        _log?.Invoke("BUDGET",
            $"Request #{_totalRequests}: +{inputTokens}↑ +{outputTokens}↓ +{cachedTokens}⚡ " +
            $"| Total: {TotalInputTokens:#,0}↑ {TotalOutputTokens:#,0}↓ " +
            $"| Cache: {cacheRate:F0}% | Cost: ${cost:F4}");

        // Check budget
        if (IsExhausted)
        {
            _log?.Invoke("BUDGET", $"EXHAUSTED: cost=${cost:F4}, remaining={RemainingBudgetFraction:P0}");
            return BudgetCheckResult.Exhausted;
        }

        // Warning at 80%
        if (RemainingBudgetFraction < 0.2)
        {
            _log?.Invoke("BUDGET", $"WARNING: {RemainingBudgetFraction:P0} budget remaining (${cost:F4})");
            return BudgetCheckResult.Warning;
        }

        return BudgetCheckResult.Ok;
    }

    /// <summary>Record a tool call (for tracking purposes).</summary>
    public void RecordToolCall() => Interlocked.Increment(ref _totalToolCalls);

    /// <summary>Reset all counters (e.g., on new chat).</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalInputTokens, 0);
        Interlocked.Exchange(ref _totalOutputTokens, 0);
        Interlocked.Exchange(ref _totalCachedTokens, 0);
        _totalRequests = 0;
        _totalToolCalls = 0;
    }

    /// <summary>Get a formatted summary for display.</summary>
    public string GetSummary()
    {
        var cost = EstimatedCostUsd;
        var cacheRate = CacheHitRate;

        var summary = $"Tokens: {TotalInputTokens:#,0}↑ {TotalOutputTokens:#,0}↓ " +
                      $"({TotalCachedTokens:#,0} cached, {cacheRate:F0}% hit rate) " +
                      $"| {_totalRequests} requests, {_totalToolCalls} tool calls " +
                      $"| ~${cost:F4}";

        if (_maxCostDollars > 0)
            summary += $" / ${_maxCostDollars:F2} ({RemainingBudgetFraction:P0} remaining)";

        return summary;
    }
}

/// <summary>Result of a budget check after recording usage.</summary>
public enum BudgetCheckResult
{
    /// <summary>Within budget, proceed normally.</summary>
    Ok,

    /// <summary>Approaching budget limit (&lt;20% remaining). Agent should wrap up.</summary>
    Warning,

    /// <summary>Budget exhausted. Agent must stop.</summary>
    Exhausted,
}
