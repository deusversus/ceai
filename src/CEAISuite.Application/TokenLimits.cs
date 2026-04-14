namespace CEAISuite.Application;

/// <summary>
/// Configurable token-efficiency limits used by AiOperatorService and AiToolFunctions.
/// Three presets (Saving, Balanced, Performance) plus per-field overrides via AppSettings.
/// Every tool result is subject to <see cref="MaxToolResultChars"/> as a hard ceiling;
/// individual tools also respect finer-grained caps (instructions, regions, etc.) so the
/// AI never receives unbounded output from scans, disassembly, or memory dumps.
/// </summary>
public sealed class TokenLimits
{
    // ── LLM / Pipeline ──
    public int MaxOutputTokens { get; init; } = 4096;
    public int MaxImagesPerTurn { get; init; } = 5;
    public int MaxApprovalRounds { get; init; } = 10;
    public int MaxReplayMessages { get; init; } = 40;
    public int MaxToolResultChars { get; init; } = 5000;

    // ── Tool output ──
    public int MaxStackFrames { get; init; } = 16;
    public int MaxBrowseMemoryBytes { get; init; } = 2048;
    public int MaxHitLogEntries { get; init; } = 25;
    public int MaxSearchResults { get; init; } = 50;
    public int MaxChatSearchResults { get; init; } = 20;
    public bool FilterRegisters { get; init; } = true;
    public bool DereferenceHookRegisters { get; init; }

    // ── Per-tool caps (new) ──

    /// <summary>Max disassembly instructions returned by Disassemble.</summary>
    public int MaxDisassemblyInstructions { get; init; } = 60;

    /// <summary>Max memory regions returned by ListMemoryRegions.</summary>
    public int MaxListRegions { get; init; } = 80;

    /// <summary>Max structure fields returned by DissectStructure.</summary>
    public int MaxDissectFields { get; init; } = 64;

    /// <summary>Max bytes for a single HexDump call.</summary>
    public int MaxHexDumpBytes { get; init; } = 256;

    /// <summary>Max results for instruction-scanning tools (FindByMemoryOperand, SearchInstructionPattern, FindWritersToOffset).</summary>
    public int MaxCodeSearchResults { get; init; } = 30;

    /// <summary>Max results per strategy in TraceFieldWriters.</summary>
    public int MaxTraceFieldResults { get; init; } = 20;

    /// <summary>Max modules shown by InspectProcess.</summary>
    public int MaxInspectModules { get; init; } = 40;

    /// <summary>Max processes shown by ListProcesses.</summary>
    public int MaxListProcesses { get; init; } = 30;

    /// <summary>Max snapshots compared in CompareSnapshots.</summary>
    public int MaxSnapshotDiffEntries { get; init; } = 50;

    /// <summary>Max VEH breakpoint hit events returned by PollVehBreakpointHits.</summary>
    public int MaxVehPollHits { get; init; } = 25;

    /// <summary>Max results for SearchMemoryPattern.</summary>
    public int MaxPatternSearchResults { get; init; } = 10;

    /// <summary>Max bookmarks returned by ListBookmarks.</summary>
    public int MaxBookmarkResults { get; init; } = 50;

    /// <summary>Max chars for export tools (ExportReport, ExportChat, ExportAddressTableJson).</summary>
    public int MaxExportChars { get; init; } = 5000;

    /// <summary>Max trace steps shown in TraceVehBreakpoint output.</summary>
    public int MaxTraceSteps { get; init; } = 50;

    // ── Compaction pipeline ──

    /// <summary>Collapse old tool-call groups after this many messages.</summary>
    public int CompactionToolResultMessages { get; init; } = 12;

    /// <summary>LLM-powered summarization when token count exceeds this.</summary>
    public int CompactionSummarizationTokens { get; init; } = 32_000;

    /// <summary>Sliding window: keep most recent N user turns.</summary>
    public int CompactionSlidingWindowTurns { get; init; } = 15;

    /// <summary>Emergency truncation backstop token threshold.</summary>
    public int CompactionTruncationTokens { get; init; } = 64_000;

    // ── Presets ──

    public static TokenLimits Saving => new()
    {
        MaxOutputTokens = 2048,
        MaxImagesPerTurn = 3,
        MaxApprovalRounds = 5,
        MaxReplayMessages = 20,
        MaxToolResultChars = 2000,
        MaxStackFrames = 8,
        MaxBrowseMemoryBytes = 512,
        MaxHitLogEntries = 10,
        MaxSearchResults = 20,
        MaxChatSearchResults = 10,
        FilterRegisters = true,
        DereferenceHookRegisters = false,
        MaxDisassemblyInstructions = 30,
        MaxListRegions = 40,
        MaxDissectFields = 32,
        MaxHexDumpBytes = 128,
        MaxCodeSearchResults = 15,
        MaxTraceFieldResults = 10,
        MaxInspectModules = 20,
        MaxListProcesses = 20,
        MaxSnapshotDiffEntries = 25,
        MaxVehPollHits = 10,
        MaxPatternSearchResults = 5,
        MaxBookmarkResults = 25,
        MaxExportChars = 2000,
        MaxTraceSteps = 20,
        CompactionToolResultMessages = 8,
        CompactionSummarizationTokens = 16_000,
        CompactionSlidingWindowTurns = 8,
        CompactionTruncationTokens = 32_000,
    };

    public static TokenLimits Balanced => new()
    {
        MaxOutputTokens = 4096,
        MaxImagesPerTurn = 5,
        MaxApprovalRounds = 10,
        MaxReplayMessages = 40,
        MaxToolResultChars = 5000,
        MaxStackFrames = 16,
        MaxBrowseMemoryBytes = 2048,
        MaxHitLogEntries = 25,
        MaxSearchResults = 50,
        MaxChatSearchResults = 20,
        FilterRegisters = true,
        DereferenceHookRegisters = false,
        MaxDisassemblyInstructions = 60,
        MaxListRegions = 80,
        MaxDissectFields = 64,
        MaxHexDumpBytes = 256,
        MaxCodeSearchResults = 30,
        MaxTraceFieldResults = 20,
        MaxInspectModules = 40,
        MaxListProcesses = 30,
        MaxSnapshotDiffEntries = 50,
        MaxVehPollHits = 25,
        MaxPatternSearchResults = 10,
        MaxBookmarkResults = 50,
        MaxExportChars = 5000,
        MaxTraceSteps = 50,
        CompactionToolResultMessages = 12,
        CompactionSummarizationTokens = 32_000,
        CompactionSlidingWindowTurns = 15,
        CompactionTruncationTokens = 64_000,
    };

    public static TokenLimits Performance => new()
    {
        MaxOutputTokens = 8192,
        MaxImagesPerTurn = 10,
        MaxApprovalRounds = 15,
        MaxReplayMessages = 80,
        MaxToolResultChars = 10000,
        MaxStackFrames = 32,
        MaxBrowseMemoryBytes = 4096,
        MaxHitLogEntries = 50,
        MaxSearchResults = 100,
        MaxChatSearchResults = 50,
        FilterRegisters = false,
        DereferenceHookRegisters = true,
        MaxDisassemblyInstructions = 120,
        MaxListRegions = 200,
        MaxDissectFields = 128,
        MaxHexDumpBytes = 512,
        MaxCodeSearchResults = 60,
        MaxTraceFieldResults = 40,
        MaxInspectModules = 80,
        MaxListProcesses = 50,
        MaxSnapshotDiffEntries = 100,
        MaxVehPollHits = 50,
        MaxPatternSearchResults = 20,
        MaxBookmarkResults = 100,
        MaxExportChars = 10000,
        MaxTraceSteps = 100,
        CompactionToolResultMessages = 20,
        CompactionSummarizationTokens = 64_000,
        CompactionSlidingWindowTurns = 30,
        CompactionTruncationTokens = 120_000,
    };

    /// <summary>
    /// Resolve effective limits from AppSettings: start with the named profile,
    /// then apply any non-null per-field overrides.
    /// </summary>
    public static TokenLimits Resolve(AppSettings settings)
    {
        var profile = (settings.TokenProfile ?? "balanced").ToLowerInvariant() switch
        {
            "saving" => Saving,
            "performance" => Performance,
            _ => Balanced,
        };

        return new TokenLimits
        {
            MaxOutputTokens = settings.LimitMaxOutputTokens ?? profile.MaxOutputTokens,
            MaxImagesPerTurn = settings.LimitMaxImagesPerTurn ?? profile.MaxImagesPerTurn,
            MaxApprovalRounds = settings.LimitMaxApprovalRounds ?? profile.MaxApprovalRounds,
            MaxReplayMessages = settings.LimitMaxReplayMessages ?? profile.MaxReplayMessages,
            MaxToolResultChars = settings.LimitMaxToolResultChars ?? profile.MaxToolResultChars,
            MaxStackFrames = settings.LimitMaxStackFrames ?? profile.MaxStackFrames,
            MaxBrowseMemoryBytes = settings.LimitMaxBrowseMemoryBytes ?? profile.MaxBrowseMemoryBytes,
            MaxHitLogEntries = settings.LimitMaxHitLogEntries ?? profile.MaxHitLogEntries,
            MaxSearchResults = settings.LimitMaxSearchResults ?? profile.MaxSearchResults,
            MaxChatSearchResults = settings.LimitMaxChatSearchResults ?? profile.MaxChatSearchResults,
            FilterRegisters = settings.LimitFilterRegisters ?? profile.FilterRegisters,
            DereferenceHookRegisters = settings.LimitDereferenceHookRegisters ?? profile.DereferenceHookRegisters,
            // New per-tool caps use profile defaults (no per-field AppSettings overrides yet)
            MaxDisassemblyInstructions = profile.MaxDisassemblyInstructions,
            MaxListRegions = profile.MaxListRegions,
            MaxDissectFields = profile.MaxDissectFields,
            MaxHexDumpBytes = profile.MaxHexDumpBytes,
            MaxCodeSearchResults = profile.MaxCodeSearchResults,
            MaxTraceFieldResults = profile.MaxTraceFieldResults,
            MaxInspectModules = profile.MaxInspectModules,
            MaxListProcesses = profile.MaxListProcesses,
            MaxSnapshotDiffEntries = profile.MaxSnapshotDiffEntries,
            CompactionToolResultMessages = profile.CompactionToolResultMessages,
            CompactionSummarizationTokens = profile.CompactionSummarizationTokens,
            CompactionSlidingWindowTurns = profile.CompactionSlidingWindowTurns,
            CompactionTruncationTokens = profile.CompactionTruncationTokens,
        };
    }

    /// <summary>Get the preset that matches the given profile name.</summary>
    public static TokenLimits ForProfile(string profile) => (profile ?? "balanced").ToLowerInvariant() switch
    {
        "saving" => Saving,
        "performance" => Performance,
        _ => Balanced,
    };

    // ── Truncation helpers ──

    /// <summary>
    /// Truncate a tool result string to <see cref="MaxToolResultChars"/>, appending a
    /// notice so the AI knows data was elided. This is the universal safety net applied
    /// to every tool result before it enters the LLM context window.
    /// </summary>
    public string TruncateToolResult(string result)
    {
        if (result.Length <= MaxToolResultChars) return result;
        var suffix = $"\n... [truncated at {MaxToolResultChars:#,0} of {result.Length:#,0} chars — use narrower parameters to see full data]";
        return string.Concat(result.AsSpan(0, MaxToolResultChars - suffix.Length), suffix);
    }

    /// <summary>
    /// Truncate a tool result to an explicit character limit (for tool-specific caps
    /// that are tighter than the global <see cref="MaxToolResultChars"/>).
    /// </summary>
    public static string Truncate(string result, int maxChars)
    {
        if (result.Length <= maxChars) return result;
        var suffix = $"\n... [truncated at {maxChars:#,0} of {result.Length:#,0} chars]";
        return string.Concat(result.AsSpan(0, maxChars - suffix.Length), suffix);
    }
}
