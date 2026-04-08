using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

public class ModelSwitcherTests
{
    private static ModelConfig MakeModel(string id, int contextTokens = 200_000) =>
        new() { ModelId = id, MaxContextTokens = contextTokens };

    private static ModelSwitcher CreateSwitcher(params string[] modelIds) =>
        new(modelIds.Select(id => MakeModel(id)));

    // ── Default state ────────────────────────────────────────────────

    [Fact]
    public void DefaultState_PrimaryModelIsActive()
    {
        var switcher = CreateSwitcher("claude-sonnet", "claude-haiku");
        Assert.Equal(0, switcher.CurrentIndex);
        Assert.Equal("claude-sonnet", switcher.CurrentModel.ModelId);
    }

    [Fact]
    public void Constructor_NoModels_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ModelSwitcher([]));
    }

    [Fact]
    public void Models_ReturnsAllConfiguredModels()
    {
        var switcher = CreateSwitcher("a", "b", "c");
        Assert.Equal(3, switcher.Models.Count);
        Assert.Equal("a", switcher.Models[0].ModelId);
        Assert.Equal("b", switcher.Models[1].ModelId);
        Assert.Equal("c", switcher.Models[2].ModelId);
    }

    // ── SwitchToModel ────────────────────────────────────────────────

    [Fact]
    public void SwitchToModel_KnownModel_ReturnsTrueAndSwitches()
    {
        var switcher = CreateSwitcher("claude-sonnet", "claude-haiku", "gpt-4o");

        bool result = switcher.SwitchToModel("gpt-4o");

        Assert.True(result);
        Assert.Equal("gpt-4o", switcher.CurrentModel.ModelId);
        Assert.Equal(2, switcher.CurrentIndex);
    }

    [Fact]
    public void SwitchToModel_UnknownModel_ReturnsFalseAndKeepsCurrent()
    {
        var switcher = CreateSwitcher("claude-sonnet", "claude-haiku");

        bool result = switcher.SwitchToModel("nonexistent-model");

        Assert.False(result);
        Assert.Equal("claude-sonnet", switcher.CurrentModel.ModelId);
    }

    [Fact]
    public void SwitchToModel_CaseInsensitive()
    {
        var switcher = CreateSwitcher("Claude-Sonnet", "Claude-Haiku");

        bool result = switcher.SwitchToModel("claude-haiku");

        Assert.True(result);
        Assert.Equal("Claude-Haiku", switcher.CurrentModel.ModelId);
    }

    // ── FallbackToNext ───────────────────────────────────────────────

    [Fact]
    public void FallbackToNext_AdvancesToNextModel()
    {
        var switcher = CreateSwitcher("primary", "secondary", "tertiary");

        var next = switcher.FallbackToNext();

        Assert.NotNull(next);
        Assert.Equal("secondary", next.ModelId);
        Assert.Equal(1, switcher.CurrentIndex);
    }

    [Fact]
    public void FallbackToNext_MultipleCalls_AdvancesSequentially()
    {
        var switcher = CreateSwitcher("a", "b", "c");

        var first = switcher.FallbackToNext();
        var second = switcher.FallbackToNext();

        Assert.NotNull(first);
        Assert.Equal("b", first.ModelId);
        Assert.NotNull(second);
        Assert.Equal("c", second.ModelId);
    }

    [Fact]
    public void FallbackToNext_AtEndOfChain_ReturnsNull()
    {
        var switcher = CreateSwitcher("a", "b");

        switcher.FallbackToNext(); // move to "b"
        var result = switcher.FallbackToNext(); // already at end

        Assert.Null(result);
        Assert.Equal("b", switcher.CurrentModel.ModelId);
    }

    [Fact]
    public void FallbackToNext_SingleModel_ReturnsNull()
    {
        var switcher = CreateSwitcher("only-model");

        var result = switcher.FallbackToNext();

        Assert.Null(result);
        Assert.Equal("only-model", switcher.CurrentModel.ModelId);
    }

    // ── ResetToPrimary ───────────────────────────────────────────────

    [Fact]
    public void ResetToPrimary_ReturnsToFirstModel()
    {
        var switcher = CreateSwitcher("primary", "secondary", "tertiary");
        switcher.FallbackToNext();
        switcher.FallbackToNext();
        Assert.Equal(2, switcher.CurrentIndex);

        switcher.ResetToPrimary();

        Assert.Equal(0, switcher.CurrentIndex);
        Assert.Equal("primary", switcher.CurrentModel.ModelId);
    }

    [Fact]
    public void ResetToPrimary_WhenAlreadyPrimary_NoOp()
    {
        var switcher = CreateSwitcher("primary", "secondary");

        switcher.ResetToPrimary();

        Assert.Equal(0, switcher.CurrentIndex);
        Assert.Equal("primary", switcher.CurrentModel.ModelId);
    }

    // ── TriggerCooldown ──────────────────────────────────────────────

    [Fact]
    public void TriggerCooldown_SwitchesToFallbackModel()
    {
        var switcher = CreateSwitcher("fast", "slow");

        switcher.TriggerCooldown(TimeSpan.FromMinutes(5));

        Assert.Equal("slow", switcher.CurrentModel.ModelId);
        Assert.Equal(1, switcher.CurrentIndex);
    }

    [Fact]
    public void TriggerCooldown_SingleModel_StaysOnSameModel()
    {
        var switcher = CreateSwitcher("only-model");

        switcher.TriggerCooldown(TimeSpan.FromMinutes(5));

        Assert.Equal("only-model", switcher.CurrentModel.ModelId);
        Assert.Equal(0, switcher.CurrentIndex);
    }

    [Fact]
    public void TriggerCooldown_AtEndOfChain_StaysOnCurrentModel()
    {
        var switcher = CreateSwitcher("a", "b");
        switcher.SwitchToModel("b");

        switcher.TriggerCooldown(TimeSpan.FromMinutes(5));

        Assert.Equal("b", switcher.CurrentModel.ModelId);
    }

    // ── GetStatusSummary ─────────────────────────────────────────────

    [Fact]
    public void GetStatusSummary_ReturnsNonEmptyString()
    {
        var switcher = CreateSwitcher("claude-sonnet", "claude-haiku");

        var summary = switcher.GetStatusSummary();

        Assert.False(string.IsNullOrWhiteSpace(summary));
    }

    [Fact]
    public void GetStatusSummary_ContainsModelIds()
    {
        var switcher = CreateSwitcher("claude-sonnet", "claude-haiku");

        var summary = switcher.GetStatusSummary();

        Assert.Contains("claude-sonnet", summary);
        Assert.Contains("claude-haiku", summary);
    }

    [Fact]
    public void GetStatusSummary_IndicatesActiveModel()
    {
        var switcher = CreateSwitcher("claude-sonnet", "claude-haiku");
        switcher.SwitchToModel("claude-haiku");

        var summary = switcher.GetStatusSummary();

        Assert.Contains("active: claude-haiku", summary);
    }

    [Fact]
    public void GetStatusSummary_ShowsModelCount()
    {
        var switcher = CreateSwitcher("a", "b", "c");

        var summary = switcher.GetStatusSummary();

        Assert.Contains("3 models", summary);
    }

    // ── Edge case: single model ──────────────────────────────────────

    [Fact]
    public void SingleModel_FallbackAndReset_WorkCorrectly()
    {
        var switcher = CreateSwitcher("sole-model");

        Assert.Null(switcher.FallbackToNext());
        Assert.Equal("sole-model", switcher.CurrentModel.ModelId);

        switcher.ResetToPrimary();
        Assert.Equal("sole-model", switcher.CurrentModel.ModelId);
    }

    [Fact]
    public void SingleModel_SwitchToSelf_ReturnsTrue()
    {
        var switcher = CreateSwitcher("sole-model");

        Assert.True(switcher.SwitchToModel("sole-model"));
        Assert.Equal(0, switcher.CurrentIndex);
    }

    // ── Log callback ─────────────────────────────────────────────────

    [Fact]
    public void LogCallback_IsInvokedOnSwitch()
    {
        var logMessages = new List<(string Level, string Message)>();
        var switcher = new ModelSwitcher(
            [MakeModel("a"), MakeModel("b")],
            log: (level, msg) => logMessages.Add((level, msg)));

        switcher.SwitchToModel("b");

        Assert.NotEmpty(logMessages);
        Assert.Contains(logMessages, l => l.Message.Contains('b'));
    }
}
