namespace CEAISuite.Application;

/// <summary>
/// Configurable token-efficiency limits used by AiOperatorService and AiToolFunctions.
/// Three presets (Saving, Balanced, Performance) plus per-field overrides via AppSettings.
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
    public bool DereferenceHookRegisters { get; init; } = false;

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
        };
    }

    /// <summary>Get the preset that matches the given profile name.</summary>
    public static TokenLimits ForProfile(string profile) => (profile ?? "balanced").ToLowerInvariant() switch
    {
        "saving" => Saving,
        "performance" => Performance,
        _ => Balanced,
    };
}
