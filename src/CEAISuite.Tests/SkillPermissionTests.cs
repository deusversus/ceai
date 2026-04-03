using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for skill-scoped permission rules — AllowedTools integration with PermissionEngine.
/// </summary>
public class SkillPermissionTests
{
    [Fact]
    public void SkillRules_AllowDangerousTool_WhenActive()
    {
        var engine = new PermissionEngine(DangerousTools.Names);

        // Without skill rules, WriteMemory requires approval
        var before = engine.Evaluate("WriteMemory", null);
        Assert.Equal(PermissionEffect.Ask, before.Effect);

        // Add skill rule that pre-approves WriteMemory
        engine.AddSkillRules("test-skill", [
            new PermissionRule { ToolPattern = "WriteMemory", Effect = PermissionEffect.Allow, Description = "Granted by skill" }
        ]);

        var after = engine.Evaluate("WriteMemory", null);
        Assert.Equal(PermissionEffect.Allow, after.Effect);
    }

    [Fact]
    public void SkillRules_RemovedOnUnload()
    {
        var engine = new PermissionEngine(DangerousTools.Names);
        engine.AddSkillRules("temp-skill", [
            new PermissionRule { ToolPattern = "SetBreakpoint", Effect = PermissionEffect.Allow }
        ]);

        Assert.Equal(PermissionEffect.Allow, engine.Evaluate("SetBreakpoint", null).Effect);

        engine.RemoveSkillRules("temp-skill");

        Assert.Equal(PermissionEffect.Ask, engine.Evaluate("SetBreakpoint", null).Effect);
    }

    [Fact]
    public void UserDenyRule_OverridesSkillAllow()
    {
        var engine = new PermissionEngine(DangerousTools.Names);

        // User rule: deny WriteMemory
        engine.AddRule(new PermissionRule
        {
            ToolPattern = "WriteMemory",
            Effect = PermissionEffect.Deny,
            Description = "User says no"
        });

        // Skill rule: allow WriteMemory
        engine.AddSkillRules("permissive-skill", [
            new PermissionRule { ToolPattern = "WriteMemory", Effect = PermissionEffect.Allow }
        ]);

        // User rule should win (evaluated first)
        var result = engine.Evaluate("WriteMemory", null);
        Assert.Equal(PermissionEffect.Deny, result.Effect);
    }

    [Fact]
    public void SkillAllow_BypassesDangerousToolDefault()
    {
        var engine = new PermissionEngine(DangerousTools.Names);

        // AllocateMemory is in DangerousTools — normally requires Ask
        Assert.Equal(PermissionEffect.Ask, engine.Evaluate("AllocateMemory", null).Effect);

        engine.AddSkillRules("memory-skill", [
            new PermissionRule { ToolPattern = "AllocateMemory", Effect = PermissionEffect.Allow }
        ]);

        // Skill grant should bypass the dangerous default
        Assert.Equal(PermissionEffect.Allow, engine.Evaluate("AllocateMemory", null).Effect);
    }

    [Fact]
    public void RequiresApproval_WithAllowedTools_ReturnsTrue()
    {
        var skill = new SkillDefinition
        {
            Name = "elevated",
            Description = "test",
            Instructions = "test",
            AllowedTools = ["WriteMemory"],
        };
        Assert.True(skill.RequiresApproval);
    }

    [Fact]
    public void RequiresApproval_WithForkContext_ReturnsTrue()
    {
        var skill = new SkillDefinition
        {
            Name = "forked",
            Description = "test",
            Instructions = "test",
            Context = SkillContext.Fork,
        };
        Assert.True(skill.RequiresApproval);
    }

    [Fact]
    public void RequiresApproval_PlainSkill_ReturnsFalse()
    {
        var skill = new SkillDefinition
        {
            Name = "plain",
            Description = "test",
            Instructions = "test",
        };
        Assert.False(skill.RequiresApproval);
    }

    [Fact]
    public void SkillSystem_ElevatedSkill_RequiresConfirmation()
    {
        var system = new SkillSystem();
        system.Register(new SkillDefinition
        {
            Name = "elevated",
            Description = "test",
            Instructions = "do stuff",
            AllowedTools = ["WriteMemory"],
        });

        var result = system.LoadSkill("elevated");
        Assert.Contains("elevated permissions", result);
        Assert.Contains("confirm_load_skill", result);
        Assert.DoesNotContain("elevated", system.ActiveSkills);
    }

    [Fact]
    public void SkillSystem_ConfirmSkillLoad_ActivatesSkill()
    {
        var system = new SkillSystem();
        system.Register(new SkillDefinition
        {
            Name = "elevated",
            Description = "test",
            Instructions = "do stuff",
            AllowedTools = ["WriteMemory"],
        });

        system.LoadSkill("elevated"); // triggers pending approval
        var result = system.ConfirmSkillLoad("elevated");
        Assert.Contains("loaded", result);
        Assert.Contains("elevated", system.ActiveSkills);
    }
}
