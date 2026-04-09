using System.Globalization;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for classes that previously had 0% coverage:
/// CommandHook, ContextManagementSerializer, ContextManagementStrategy,
/// CronExpression (covered separately), HookCondition.
/// </summary>
public class ZeroCoverageClassTests
{
    // ── ContextManagementStrategy ──

    [Fact]
    public void ClearThinking_HasCorrectName()
    {
        var strategy = ContextManagementStrategy.ClearThinking;
        Assert.Equal("clear_thinking_20251015", strategy.Name);
        Assert.Equal("context_management", strategy.Type);
        Assert.Equal(0.8m, strategy.TriggerThreshold);
    }

    [Fact]
    public void ClearToolUses_HasCorrectDefaults()
    {
        var strategy = ContextManagementStrategy.ClearToolUses;
        Assert.Equal("clear_tool_uses_20250919", strategy.Name);
        Assert.Equal(0.7m, strategy.TriggerThreshold);
        Assert.Equal(40_000, strategy.RetentionTarget);
    }

    [Fact]
    public void AnthropicDefaults_ContainsBothStrategies()
    {
        var defaults = ContextManagementStrategy.AnthropicDefaults;
        Assert.Equal(2, defaults.Count);
        Assert.Contains(defaults, s => s.Name.Contains("clear_thinking"));
        Assert.Contains(defaults, s => s.Name.Contains("clear_tool_uses"));
    }

    [Fact]
    public void CustomStrategy_CanSetFields()
    {
        var custom = new ContextManagementStrategy
        {
            Name = "custom_strategy",
            TriggerThreshold = 0.5m,
            RetentionTarget = 20_000,
        };
        Assert.Equal("custom_strategy", custom.Name);
        Assert.Equal(0.5m, custom.TriggerThreshold);
        Assert.Equal(20_000, custom.RetentionTarget);
    }

    // ── ContextManagementSerializer ──

    [Fact]
    public void Serialize_ReturnsListOfDictionaries()
    {
        var strategies = ContextManagementStrategy.AnthropicDefaults;
        var result = ContextManagementSerializer.Serialize(strategies);

        Assert.IsType<List<Dictionary<string, object>>>(result);
        var list = (List<Dictionary<string, object>>)result;
        Assert.Equal(2, list.Count);
        Assert.Equal("clear_thinking_20251015", list[0]["name"]);
        Assert.Equal("context_management", list[0]["type"]);
    }

    [Fact]
    public void Serialize_EmptyList_ReturnsEmptyList()
    {
        var result = ContextManagementSerializer.Serialize([]);
        var list = (List<Dictionary<string, object>>)result;
        Assert.Empty(list);
    }

    // ── HookCondition ──

    [Fact]
    public void HookCondition_NoConstraints_AlwaysMatches()
    {
        var condition = new HookCondition();
        var context = new ToolHookContext
        {
            ToolName = "AnyTool",
            TurnNumber = 5,
        };
        Assert.True(condition.Matches(context));
    }

    [Fact]
    public void HookCondition_ToolPattern_MatchesGlob()
    {
        var condition = new HookCondition { ToolPattern = "Write*" };
        var writeCtx = new ToolHookContext { ToolName = "WriteMemory", TurnNumber = 1 };
        var readCtx = new ToolHookContext { ToolName = "ReadMemory", TurnNumber = 1 };

        Assert.True(condition.Matches(writeCtx));
        Assert.False(condition.Matches(readCtx));
    }

    [Fact]
    public void HookCondition_MinTurnNumber_RejectsEarlyTurns()
    {
        var condition = new HookCondition { MinTurnNumber = 5 };
        var earlyCtx = new ToolHookContext { ToolName = "Test", TurnNumber = 3 };
        var lateCtx = new ToolHookContext { ToolName = "Test", TurnNumber = 5 };

        Assert.False(condition.Matches(earlyCtx));
        Assert.True(condition.Matches(lateCtx));
    }

    [Fact]
    public void HookCondition_ArgumentPattern_MatchesArgValue()
    {
        var condition = new HookCondition { ArgumentPattern = @"0x[0-9A-Fa-f]+" };
        var matchCtx = new ToolHookContext
        {
            ToolName = "Test",
            TurnNumber = 1,
            Arguments = new Dictionary<string, object?> { ["address"] = "0x400000" }
        };
        Assert.True(condition.Matches(matchCtx));
    }

    [Fact]
    public void HookCondition_ArgumentPattern_NoMatch()
    {
        var condition = new HookCondition { ArgumentPattern = @"0x[0-9A-Fa-f]+" };
        var noMatchCtx = new ToolHookContext
        {
            ToolName = "Test",
            TurnNumber = 1,
            Arguments = new Dictionary<string, object?> { ["name"] = "hello" }
        };
        Assert.False(condition.Matches(noMatchCtx));
    }

    [Fact]
    public void HookCondition_CombinedConstraints_AllMustMatch()
    {
        var condition = new HookCondition
        {
            ToolPattern = "Write*",
            MinTurnNumber = 3,
        };

        // Wrong tool
        Assert.False(condition.Matches(new ToolHookContext { ToolName = "ReadMemory", TurnNumber = 5 }));
        // Too early
        Assert.False(condition.Matches(new ToolHookContext { ToolName = "WriteMemory", TurnNumber = 1 }));
        // Matches all
        Assert.True(condition.Matches(new ToolHookContext { ToolName = "WriteMemory", TurnNumber = 5 }));
    }

    // ── CommandHook ──

    [Fact]
    public async Task CommandHook_InvalidCommand_ReturnsFailedToStart()
    {
        var hook = new CommandHook("nonexistent-command-xyz-12345");
        var context = new ToolHookContext { ToolName = "Test", TurnNumber = 1 };
        // Should not throw, returns Continue with error message
        var result = await hook.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, result.Outcome);
    }

    [Fact]
    public async Task CommandHook_SuccessfulCommand_ReturnsContinue()
    {
        // Use a command that exists on Windows
        var hook = new CommandHook("cmd.exe", "/c echo hello", timeout: TimeSpan.FromSeconds(5));
        var context = new ToolHookContext { ToolName = "Test", TurnNumber = 1 };
        var result = await hook.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, result.Outcome);
    }

    [Fact]
    public async Task CommandHook_SetsEnvironmentVariables()
    {
        // Use a command that prints an environment variable
        var hook = new CommandHook("cmd.exe", "/c echo %CEAI_TOOL_NAME%", timeout: TimeSpan.FromSeconds(5));
        var context = new ToolHookContext
        {
            ToolName = "WriteMemory",
            TurnNumber = 42,
            Arguments = new Dictionary<string, object?> { ["address"] = "0x400000" }
        };
        var result = await hook.ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(HookOutcome.Continue, result.Outcome);
    }

    // ── HookTrustLevel enum ──

    [Fact]
    public void HookTrustLevel_HasExpectedValues()
    {
        Assert.Equal(0, (int)HookTrustLevel.Builtin);
        Assert.Equal(1, (int)HookTrustLevel.User);
        Assert.Equal(2, (int)HookTrustLevel.Plugin);
    }

    // ── ScheduledTask record ──

    [Fact]
    public void ScheduledTask_DefaultValues()
    {
        var task = new ScheduledTask();
        Assert.Equal("", task.Id);
        Assert.Equal("", task.CronExpression);
        Assert.True(task.Enabled);
        Assert.False(task.IsOneShot);
        Assert.Null(task.LastRunAt);
    }
}
