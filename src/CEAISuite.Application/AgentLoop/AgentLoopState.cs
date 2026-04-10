namespace CEAISuite.Application.AgentLoop;

/// <summary>
/// Named transitions that drive the agent loop's state machine.
/// Each iteration of the while(true) loop ends by setting a transition
/// that determines what happens next.
/// </summary>
public enum AgentTransition
{
    /// <summary>Normal progression — execute next LLM turn.</summary>
    NextTurn,

    /// <summary>Max output tokens hit; retry with escalated token limit.</summary>
    TokenEscalation,

    /// <summary>Max turns reached — stop the loop.</summary>
    BudgetExhausted,

    /// <summary>LLM returned text with no tool calls — conversation turn complete.</summary>
    Completed,

    /// <summary>User cancelled via CancellationToken.</summary>
    Aborted,
}

/// <summary>Describes why the agent loop was aborted.</summary>
public sealed record AbortReason
{
    public required AbortKind Kind { get; init; }
    public string? Message { get; init; }
}

/// <summary>The kind of abort that occurred.</summary>
public enum AbortKind
{
    /// <summary>User pressed cancel / ESC.</summary>
    UserCancelled,
    /// <summary>Streaming response timed out (watchdog).</summary>
    StreamingTimeout,
    /// <summary>Token or cost budget exhausted.</summary>
    BudgetExhausted,
    /// <summary>All retry attempts exhausted.</summary>
    ErrorExhausted,
}

/// <summary>
/// Immutable state tracked across iterations of the agent loop.
/// A new instance is created for each transition; the previous is discarded.
/// </summary>
public sealed record AgentLoopState
{
    /// <summary>Current turn number (1-based). Incremented after each successful LLM round-trip.</summary>
    public int TurnCount { get; init; } = 1;

    /// <summary>Cumulative tool calls across all turns in this loop invocation.</summary>
    public int TotalToolCalls { get; init; }

    /// <summary>What the loop should do next.</summary>
    public AgentTransition Transition { get; init; } = AgentTransition.NextTurn;

    /// <summary>Override for MaxOutputTokens after escalation (null = use default).</summary>
    public int? MaxOutputTokensOverride { get; init; }

    /// <summary>How many times we've attempted max-output-tokens recovery.</summary>
    public int MaxOutputTokensRecoveryCount { get; init; }

    /// <summary>Number of consecutive compaction failures. Reset to 0 on success.</summary>
    public int ConsecutiveCompactionFailures { get; init; }

    /// <summary>Turn number of the last successful compaction (to avoid compacting every turn).</summary>
    public int LastCompactionTurn { get; init; } = -1;

    /// <summary>Turn number until which compaction is skipped (exponential backoff).</summary>
    public int CompactionSkipUntilTurn { get; init; }

    /// <summary>Maximum consecutive compaction failures before backoff kicks in.</summary>
    public const int MaxConsecutiveCompactionFailures = 3;

    /// <summary>Maximum turns to skip between compaction retries (cap for exponential backoff).</summary>
    public const int MaxCompactionBackoffTurns = 20;

    /// <summary>
    /// Consecutive turns where the LLM produced fewer than 500 output tokens.
    /// Used for diminishing-returns detection to prevent runaway loops.
    /// </summary>
    public int ConsecutiveLowOutputTurns { get; init; }

    /// <summary>Total output tokens produced in the most recent turn.</summary>
    public int LastTurnOutputTokens { get; init; }

    /// <summary>Human-readable reason for the current transition (for logging).</summary>
    public string? TransitionReason { get; init; }

    /// <summary>Reason the loop was aborted, if applicable.</summary>
    public AbortReason? AbortInfo { get; init; }
}
