using CEAISuite.Application;

namespace CEAISuite.Tests;

public class TokenLimitsTests
{
    [Fact]
    public void TruncateToolResult_ShortString_ReturnsUnchanged()
    {
        var limits = TokenLimits.Balanced;
        var input = "Short result";
        Assert.Equal(input, limits.TruncateToolResult(input));
    }

    [Fact]
    public void TruncateToolResult_LongString_TruncatesWithNotice()
    {
        var limits = new TokenLimits { MaxToolResultChars = 100 };
        var input = new string('x', 500);
        var result = limits.TruncateToolResult(input);

        Assert.True(result.Length <= 100, $"Result length {result.Length} exceeds limit 100");
        Assert.Contains("truncated", result);
        Assert.Contains("500", result);
    }

    [Fact]
    public void TruncateToolResult_ExactlyAtLimit_ReturnsUnchanged()
    {
        var limits = new TokenLimits { MaxToolResultChars = 50 };
        var input = new string('a', 50);
        Assert.Equal(input, limits.TruncateToolResult(input));
    }

    [Fact]
    public void Truncate_Static_TruncatesAtExplicitLimit()
    {
        var input = new string('z', 200);
        var result = TokenLimits.Truncate(input, 80);

        Assert.True(result.Length <= 80);
        Assert.Contains("truncated", result);
    }

    [Fact]
    public void Truncate_Static_ShortString_ReturnsUnchanged()
    {
        var input = "hello";
        Assert.Equal(input, TokenLimits.Truncate(input, 100));
    }

    [Theory]
    [InlineData("saving")]
    [InlineData("balanced")]
    [InlineData("performance")]
    public void ForProfile_ReturnsValidPreset(string profile)
    {
        var limits = TokenLimits.ForProfile(profile);
        Assert.True(limits.MaxOutputTokens > 0);
        Assert.True(limits.MaxToolResultChars > 0);
        Assert.True(limits.MaxDisassemblyInstructions > 0);
        Assert.True(limits.MaxListRegions > 0);
        Assert.True(limits.MaxDissectFields > 0);
        Assert.True(limits.MaxHexDumpBytes > 0);
        Assert.True(limits.MaxCodeSearchResults > 0);
        Assert.True(limits.MaxTraceFieldResults > 0);
        Assert.True(limits.MaxInspectModules > 0);
        Assert.True(limits.MaxListProcesses > 0);
        Assert.True(limits.MaxSnapshotDiffEntries > 0);
    }

    [Fact]
    public void SavingProfile_HasStricterLimitsThanBalanced()
    {
        var saving = TokenLimits.Saving;
        var balanced = TokenLimits.Balanced;

        Assert.True(saving.MaxToolResultChars < balanced.MaxToolResultChars);
        Assert.True(saving.MaxDisassemblyInstructions < balanced.MaxDisassemblyInstructions);
        Assert.True(saving.MaxListRegions < balanced.MaxListRegions);
        Assert.True(saving.MaxCodeSearchResults < balanced.MaxCodeSearchResults);
        Assert.True(saving.MaxHexDumpBytes < balanced.MaxHexDumpBytes);
    }

    [Fact]
    public void PerformanceProfile_HasLoosestLimits()
    {
        var balanced = TokenLimits.Balanced;
        var performance = TokenLimits.Performance;

        Assert.True(performance.MaxToolResultChars > balanced.MaxToolResultChars);
        Assert.True(performance.MaxDisassemblyInstructions > balanced.MaxDisassemblyInstructions);
        Assert.True(performance.MaxListRegions > balanced.MaxListRegions);
        Assert.True(performance.MaxCodeSearchResults > balanced.MaxCodeSearchResults);
    }

    [Fact]
    public void Resolve_AppliesPerFieldOverrides()
    {
        var settings = new AppSettings
        {
            TokenProfile = "saving",
            LimitMaxToolResultChars = 9999,
        };

        var limits = TokenLimits.Resolve(settings);

        Assert.Equal(9999, limits.MaxToolResultChars);
        Assert.Equal(TokenLimits.Saving.MaxOutputTokens, limits.MaxOutputTokens);
        Assert.Equal(TokenLimits.Saving.MaxDisassemblyInstructions, limits.MaxDisassemblyInstructions);
    }

    [Fact]
    public void Resolve_DefaultProfile_UsesBalanced()
    {
        var settings = new AppSettings();
        var limits = TokenLimits.Resolve(settings);

        Assert.Equal(TokenLimits.Balanced.MaxOutputTokens, limits.MaxOutputTokens);
        Assert.Equal(TokenLimits.Balanced.MaxToolResultChars, limits.MaxToolResultChars);
    }

    // ── New coverage: exact preset values, case-insensitive, null profile, compaction ──

    [Fact]
    public void Saving_Preset_HasExactValues()
    {
        var l = TokenLimits.Saving;
        Assert.Equal(2048, l.MaxOutputTokens);
        Assert.Equal(3, l.MaxImagesPerTurn);
        Assert.Equal(5, l.MaxApprovalRounds);
        Assert.Equal(20, l.MaxReplayMessages);
        Assert.Equal(2000, l.MaxToolResultChars);
        Assert.Equal(8, l.MaxStackFrames);
        Assert.Equal(512, l.MaxBrowseMemoryBytes);
        Assert.Equal(10, l.MaxHitLogEntries);
        Assert.True(l.FilterRegisters);
        Assert.False(l.DereferenceHookRegisters);
    }

    [Fact]
    public void Balanced_Preset_HasExactValues()
    {
        var l = TokenLimits.Balanced;
        Assert.Equal(4096, l.MaxOutputTokens);
        Assert.Equal(5, l.MaxImagesPerTurn);
        Assert.Equal(10, l.MaxApprovalRounds);
        Assert.Equal(40, l.MaxReplayMessages);
        Assert.Equal(5000, l.MaxToolResultChars);
        Assert.Equal(16, l.MaxStackFrames);
        Assert.Equal(2048, l.MaxBrowseMemoryBytes);
    }

    [Fact]
    public void Performance_Preset_HasExactValues()
    {
        var l = TokenLimits.Performance;
        Assert.Equal(8192, l.MaxOutputTokens);
        Assert.Equal(10, l.MaxImagesPerTurn);
        Assert.Equal(15, l.MaxApprovalRounds);
        Assert.Equal(80, l.MaxReplayMessages);
        Assert.Equal(10000, l.MaxToolResultChars);
        Assert.False(l.FilterRegisters);
        Assert.True(l.DereferenceHookRegisters);
    }

    [Fact]
    public void Resolve_NullProfile_DefaultsToBalanced()
    {
        var settings = new AppSettings { TokenProfile = null! };
        var limits = TokenLimits.Resolve(settings);
        Assert.Equal(TokenLimits.Balanced.MaxOutputTokens, limits.MaxOutputTokens);
    }

    [Theory]
    [InlineData("SAVING", 2048)]
    [InlineData("Saving", 2048)]
    [InlineData("PERFORMANCE", 8192)]
    [InlineData("unknown_profile", 4096)]
    public void ForProfile_CaseInsensitive_ReturnsCorrectPreset(string profile, int expected)
    {
        var limits = TokenLimits.ForProfile(profile);
        Assert.Equal(expected, limits.MaxOutputTokens);
    }

    [Fact]
    public void ForProfile_Null_DefaultsToBalanced()
    {
        var limits = TokenLimits.ForProfile(null!);
        Assert.Equal(TokenLimits.Balanced.MaxOutputTokens, limits.MaxOutputTokens);
    }

    [Fact]
    public void Resolve_MultipleOverrides_AllApplied()
    {
        var settings = new AppSettings
        {
            TokenProfile = "balanced",
            LimitMaxOutputTokens = 1234,
            LimitMaxStackFrames = 99,
            LimitFilterRegisters = false,
            LimitDereferenceHookRegisters = true,
            LimitMaxBrowseMemoryBytes = 8192,
        };
        var l = TokenLimits.Resolve(settings);
        Assert.Equal(1234, l.MaxOutputTokens);
        Assert.Equal(99, l.MaxStackFrames);
        Assert.False(l.FilterRegisters);
        Assert.True(l.DereferenceHookRegisters);
        Assert.Equal(8192, l.MaxBrowseMemoryBytes);
        Assert.Equal(5, l.MaxImagesPerTurn);
    }

    [Fact]
    public void Truncate_Static_LongString_ContainsOriginalLength()
    {
        var input = new string('a', 500);
        var result = TokenLimits.Truncate(input, 100);
        Assert.Contains("500", result);
    }

    [Fact]
    public void CompactionPresets_ScaleWithProfile()
    {
        Assert.True(TokenLimits.Saving.CompactionSummarizationTokens < TokenLimits.Balanced.CompactionSummarizationTokens);
        Assert.True(TokenLimits.Balanced.CompactionSummarizationTokens < TokenLimits.Performance.CompactionSummarizationTokens);
        Assert.True(TokenLimits.Saving.CompactionSlidingWindowTurns < TokenLimits.Performance.CompactionSlidingWindowTurns);
    }
}
