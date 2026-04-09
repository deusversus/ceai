using System.IO;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Deep tests for PermissionEngine: rule evaluation, modes, denial tracking,
/// skill rules, persistence, and glob matching.
/// </summary>
public class PermissionEngineDeepTests : IDisposable
{
    private readonly HashSet<string> _dangerousTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "WriteMemory", "SetBreakpoint", "InstallCodeCaveHook"
    };

    private readonly string _tempDir;

    public PermissionEngineDeepTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ceai-perm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private PermissionEngine CreateEngine(ToolAttributeCache? cache = null)
        => new(_dangerousTools, attributeCache: cache);

    // ── Basic Evaluation ──

    [Fact]
    public void Evaluate_SafeTool_AllowedByDefault()
    {
        var engine = CreateEngine();
        var decision = engine.Evaluate("ReadMemory", null);
        Assert.Equal(PermissionEffect.Allow, decision.Effect);
    }

    [Fact]
    public void Evaluate_DangerousTool_AskedByDefault()
    {
        var engine = CreateEngine();
        var decision = engine.Evaluate("WriteMemory", null);
        Assert.Equal(PermissionEffect.Ask, decision.Effect);
    }

    [Fact]
    public void Evaluate_ExplicitAllowRule_Overrides()
    {
        var engine = CreateEngine();
        engine.AddRule(new PermissionRule
        {
            ToolPattern = "WriteMemory",
            Effect = PermissionEffect.Allow,
            Description = "Auto-approve writes"
        });
        var decision = engine.Evaluate("WriteMemory", null);
        Assert.Equal(PermissionEffect.Allow, decision.Effect);
        Assert.NotNull(decision.MatchedRule);
    }

    [Fact]
    public void Evaluate_ExplicitDenyRule_Blocks()
    {
        var engine = CreateEngine();
        engine.AddRule(new PermissionRule
        {
            ToolPattern = "ReadMemory",
            Effect = PermissionEffect.Deny,
        });
        var decision = engine.Evaluate("ReadMemory", null);
        Assert.Equal(PermissionEffect.Deny, decision.Effect);
    }

    [Fact]
    public void Evaluate_WildcardRule_MatchesAll()
    {
        var engine = CreateEngine();
        engine.AddRule(new PermissionRule
        {
            ToolPattern = "*",
            Effect = PermissionEffect.Deny,
        });
        var decision = engine.Evaluate("AnyTool", null);
        Assert.Equal(PermissionEffect.Deny, decision.Effect);
    }

    [Fact]
    public void Evaluate_GlobPattern_MatchesPrefix()
    {
        var engine = CreateEngine();
        engine.AddRule(new PermissionRule
        {
            ToolPattern = "Read*",
            Effect = PermissionEffect.Allow,
        });
        Assert.Equal(PermissionEffect.Allow, engine.Evaluate("ReadMemory", null).Effect);
        Assert.Equal(PermissionEffect.Allow, engine.Evaluate("ReadValue", null).Effect);
        // Non-matching
        Assert.Equal(PermissionEffect.Allow, engine.Evaluate("ListProcesses", null).Effect); // default allow
    }

    [Fact]
    public void Evaluate_ArgumentPattern_MatchesValue()
    {
        var engine = CreateEngine();
        engine.AddRule(new PermissionRule
        {
            ToolPattern = "WriteMemory",
            Effect = PermissionEffect.Allow,
            ArgumentPatterns = new() { ["address"] = "0x400*" }
        });

        var args1 = new Dictionary<string, object?> { ["address"] = "0x400100" };
        Assert.Equal(PermissionEffect.Allow, engine.Evaluate("WriteMemory", args1).Effect);

        var args2 = new Dictionary<string, object?> { ["address"] = "0x700100" };
        Assert.Equal(PermissionEffect.Ask, engine.Evaluate("WriteMemory", args2).Effect); // falls to dangerous default
    }

    [Fact]
    public void Evaluate_ArgumentPattern_MissingArg_NoMatch()
    {
        var engine = CreateEngine();
        engine.AddRule(new PermissionRule
        {
            ToolPattern = "WriteMemory",
            Effect = PermissionEffect.Allow,
            ArgumentPatterns = new() { ["address"] = "0x400*" }
        });

        // No arguments at all
        Assert.Equal(PermissionEffect.Ask, engine.Evaluate("WriteMemory", null).Effect);
    }

    // ── Permission Modes ──

    [Fact]
    public void UnrestrictedMode_AllowsEverything()
    {
        var engine = CreateEngine();
        engine.ActiveMode = PermissionMode.Unrestricted;
        Assert.Equal(PermissionEffect.Allow, engine.Evaluate("WriteMemory", null).Effect);
        Assert.Equal(PermissionEffect.Allow, engine.Evaluate("SetBreakpoint", null).Effect);
    }

    [Fact]
    public void ReadOnlyMode_DeniesNonReadOnly()
    {
        var cache = new ToolAttributeCache();
        cache.ScanType(typeof(CEAISuite.Application.AiToolFunctions));
        var engine = new PermissionEngine(_dangerousTools, attributeCache: cache);
        engine.ActiveMode = PermissionMode.ReadOnly;

        // WriteMemory is marked [Destructive], not [ReadOnlyTool], so should be denied
        var decision = engine.Evaluate("WriteMemory", null);
        Assert.Equal(PermissionEffect.Deny, decision.Effect);
    }

    [Fact]
    public void NormalMode_FallsThrough()
    {
        var engine = CreateEngine();
        engine.ActiveMode = PermissionMode.Normal;
        // Safe tool allowed
        Assert.Equal(PermissionEffect.Allow, engine.Evaluate("ReadMemory", null).Effect);
        // Dangerous tool asked
        Assert.Equal(PermissionEffect.Ask, engine.Evaluate("WriteMemory", null).Effect);
    }

    // ── Rule Management ──

    [Fact]
    public void AddRules_BulkAdd()
    {
        var engine = CreateEngine();
        engine.AddRules(new[]
        {
            new PermissionRule { ToolPattern = "Read*", Effect = PermissionEffect.Allow },
            new PermissionRule { ToolPattern = "Write*", Effect = PermissionEffect.Ask },
        });
        Assert.Equal(2, engine.RuleCount);
    }

    [Fact]
    public void ClearRules_RemovesAll()
    {
        var engine = CreateEngine();
        engine.AddRule(new PermissionRule { ToolPattern = "*", Effect = PermissionEffect.Deny });
        Assert.Equal(1, engine.RuleCount);
        engine.ClearRules();
        Assert.Equal(0, engine.RuleCount);
    }

    [Fact]
    public void Rules_Snapshot_ReturnsAllRules()
    {
        var engine = CreateEngine();
        engine.AddRule(new PermissionRule { ToolPattern = "Test", Effect = PermissionEffect.Allow });
        var rules = engine.Rules;
        Assert.Single(rules);
    }

    // ── Skill Rules ──

    [Fact]
    public void SkillRules_LowerPriorityThanUserRules()
    {
        var engine = CreateEngine();
        // User rule: deny WriteMemory
        engine.AddRule(new PermissionRule { ToolPattern = "WriteMemory", Effect = PermissionEffect.Deny });
        // Skill rule: allow WriteMemory
        engine.AddSkillRules("myskill", new[]
        {
            new PermissionRule { ToolPattern = "WriteMemory", Effect = PermissionEffect.Allow }
        });

        // User rule wins
        var decision = engine.Evaluate("WriteMemory", null);
        Assert.Equal(PermissionEffect.Deny, decision.Effect);
    }

    [Fact]
    public void SkillRules_ApplyWhenNoUserRule()
    {
        var engine = CreateEngine();
        engine.AddSkillRules("myskill", new[]
        {
            new PermissionRule { ToolPattern = "WriteMemory", Effect = PermissionEffect.Allow }
        });

        // No user rule, skill rule auto-allows
        var decision = engine.Evaluate("WriteMemory", null);
        Assert.Equal(PermissionEffect.Allow, decision.Effect);
    }

    [Fact]
    public void RemoveSkillRules_RevertsToDangerousDefault()
    {
        var engine = CreateEngine();
        engine.AddSkillRules("myskill", new[]
        {
            new PermissionRule { ToolPattern = "WriteMemory", Effect = PermissionEffect.Allow }
        });
        engine.RemoveSkillRules("myskill");

        var decision = engine.Evaluate("WriteMemory", null);
        Assert.Equal(PermissionEffect.Ask, decision.Effect);
    }

    // ── Denial Tracking ──

    [Fact]
    public void TrackDenial_IncrementsCount()
    {
        var engine = CreateEngine();
        Assert.Equal(0, engine.GetDenialCount("WriteMemory"));
        engine.TrackDenial("WriteMemory");
        Assert.Equal(1, engine.GetDenialCount("WriteMemory"));
        engine.TrackDenial("WriteMemory");
        Assert.Equal(2, engine.GetDenialCount("WriteMemory"));
    }

    [Fact]
    public void ShouldEscalateDenial_AtThreshold()
    {
        var engine = CreateEngine();
        engine.DenialEscalationThreshold = 3;
        engine.TrackDenial("WriteMemory");
        engine.TrackDenial("WriteMemory");
        Assert.False(engine.ShouldEscalateDenial("WriteMemory"));
        engine.TrackDenial("WriteMemory");
        Assert.True(engine.ShouldEscalateDenial("WriteMemory"));
    }

    [Fact]
    public void ResetDenialTracking_ClearsAll()
    {
        var engine = CreateEngine();
        engine.TrackDenial("WriteMemory");
        engine.TrackDenial("SetBreakpoint");
        engine.ResetDenialTracking();
        Assert.Equal(0, engine.GetDenialCount("WriteMemory"));
        Assert.Equal(0, engine.GetDenialCount("SetBreakpoint"));
    }

    // ── Persistence ──

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var path = Path.Combine(_tempDir, "rules.json");
        var engine1 = CreateEngine();
        engine1.AddRule(new PermissionRule { ToolPattern = "WriteMemory", Effect = PermissionEffect.Allow, Description = "test rule" });
        engine1.SaveRules(path);

        var engine2 = CreateEngine();
        engine2.LoadRules(path);
        Assert.Equal(1, engine2.RuleCount);
        var decision = engine2.Evaluate("WriteMemory", null);
        Assert.Equal(PermissionEffect.Allow, decision.Effect);
    }

    [Fact]
    public void LoadRules_NonexistentFile_DoesNotThrow()
    {
        var engine = CreateEngine();
        engine.LoadRules(Path.Combine(_tempDir, "nonexistent.json"));
        Assert.Equal(0, engine.RuleCount);
    }

    // ── PermissionRule.Matches ──

    [Fact]
    public void PermissionRule_GlobMatch_QuestionMark()
    {
        Assert.True(PermissionRule.GlobMatch("ReadMemory", "Read?emory"));
        Assert.False(PermissionRule.GlobMatch("ReadXYMemory", "Read?emory"));
    }

    [Fact]
    public void PermissionRule_GlobMatch_CaseInsensitive()
    {
        Assert.True(PermissionRule.GlobMatch("readmemory", "ReadMemory"));
        Assert.True(PermissionRule.GlobMatch("READMEMORY", "readmemory"));
    }

    [Fact]
    public void PermissionRule_ToString_IncludesPattern()
    {
        var rule = new PermissionRule
        {
            ToolPattern = "Write*",
            Effect = PermissionEffect.Ask,
            Description = "Ask for writes"
        };
        var str = rule.ToString();
        Assert.Contains("Write*", str);
        Assert.Contains("Ask for writes", str);
    }

    [Fact]
    public void PermissionRule_Matches_NoArgs_NoArgPatterns_MatchesToolOnly()
    {
        var rule = new PermissionRule { ToolPattern = "ReadMemory", Effect = PermissionEffect.Allow };
        Assert.True(rule.Matches("ReadMemory", null));
    }
}
