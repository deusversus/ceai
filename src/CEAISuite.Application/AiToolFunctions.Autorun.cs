using System.ComponentModel;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Application;

public sealed partial class AiToolFunctions
{
    /// <summary>List all autorun Lua scripts and their enabled/disabled status.</summary>
    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("List autorun Lua scripts from the scripts/autorun/ directory with their enabled status.")]
    public Task<string> ListAutorunScripts()
    {
        if (autorunService is null)
            return Task.FromResult("Autorun service is not available.");

        var scripts = autorunService.ListScripts();
        if (scripts.Count == 0)
            return Task.FromResult($"No autorun scripts found.\nDirectory: {autorunService.GetAutorunDirectory()}");

        var lines = scripts.Select(s => $"  {(s.Enabled ? "✓" : "✗")} {s.Name}");
        return Task.FromResult($"Autorun scripts ({scripts.Count}):\n{string.Join("\n", lines)}\n\nDirectory: {autorunService.GetAutorunDirectory()}");
    }

    /// <summary>Enable or disable an autorun script.</summary>
    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Enable or disable an autorun Lua script by filename.")]
    public Task<string> SetAutorunEnabled(
        [Description("Script filename (e.g., '01_init.lua')")] string scriptName,
        [Description("true to enable, false to disable")] bool enabled)
    {
        if (autorunService is null)
            return Task.FromResult("Autorun service is not available.");

        autorunService.SetEnabled(scriptName, enabled);
        return Task.FromResult($"Autorun script '{scriptName}' {(enabled ? "enabled" : "disabled")}.");
    }
}
