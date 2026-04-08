using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for DangerousTools completeness and PermissionEngine integration.
/// Ensures all [Destructive] tools are properly gated.
/// </summary>
public class DangerousToolsTests
{
    [Fact]
    public void DangerousTools_ContainsAllDestructiveTools()
    {
        // Every tool that can modify target process state must be in the set
        var expected = new[]
        {
            "WriteMemory", "SetBreakpoint", "RemoveBreakpoint",
            "InstallCodeCaveHook", "RemoveCodeCaveHook",
            "ToggleScript", "ForceDetachAndCleanup",
            "EmergencyRestorePageProtection", "ChangeMemoryProtection",
            "AllocateMemory", "FreeMemory", "RollbackTransaction",
            "RegisterBreakpointLuaCallback",
            "ExecuteAutoAssemblerScript", "DisableAutoAssemblerScript",
        };

        foreach (var tool in expected)
        {
            Assert.True(DangerousTools.Names.Contains(tool),
                $"Tool '{tool}' should be in DangerousTools.Names but is missing.");
        }
    }

    [Fact]
    public void DangerousTools_ExcludesRecoveryTools()
    {
        // UndoWrite/RedoWrite are recovery operations — should NOT require approval
        Assert.DoesNotContain("UndoWrite", DangerousTools.Names);
        Assert.DoesNotContain("RedoWrite", DangerousTools.Names);
    }

    [Fact]
    public void PermissionEngine_DangerousTool_RequiresApproval()
    {
        var engine = new PermissionEngine(DangerousTools.Names);
        var result = engine.Evaluate("WriteMemory", null);
        Assert.Equal(PermissionEffect.Ask, result.Effect);
    }

    [Fact]
    public void PermissionEngine_SafeTool_AllowedByDefault()
    {
        var engine = new PermissionEngine(DangerousTools.Names);
        var result = engine.Evaluate("ReadMemory", null);
        Assert.Equal(PermissionEffect.Allow, result.Effect);
    }

    [Fact]
    public void PermissionEngine_AllDangerousTools_RequireApproval()
    {
        var engine = new PermissionEngine(DangerousTools.Names);
        foreach (var tool in DangerousTools.Names)
        {
            var result = engine.Evaluate(tool, null);
            Assert.Equal(PermissionEffect.Ask, result.Effect);
        }
    }
}
