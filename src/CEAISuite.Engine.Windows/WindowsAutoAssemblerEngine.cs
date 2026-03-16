using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Engine.Windows;

/// <summary>
/// Auto Assembler engine for parsing and executing Cheat Engine AA scripts.
/// Uses Keystone assembler for x86/x64 instruction encoding and Windows API for process memory manipulation.
/// </summary>
public sealed class WindowsAutoAssemblerEngine : IAutoAssemblerEngine
{
    public Task<ScriptExecutionResult> EnableAsync(int processId, string script, CancellationToken ct = default)
    {
        return Task.FromResult(new ScriptExecutionResult(
            false, "Auto Assembler engine not yet implemented.",
            Array.Empty<ScriptAllocation>(), Array.Empty<ScriptPatch>()));
    }

    public Task<ScriptExecutionResult> DisableAsync(int processId, string script, CancellationToken ct = default)
    {
        return Task.FromResult(new ScriptExecutionResult(
            false, "Auto Assembler engine not yet implemented.",
            Array.Empty<ScriptAllocation>(), Array.Empty<ScriptPatch>()));
    }

    public ScriptParseResult Parse(string script)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        string? enableSection = null;
        string? disableSection = null;

        if (string.IsNullOrWhiteSpace(script))
        {
            errors.Add("Script is empty.");
            return new ScriptParseResult(false, errors, warnings, null, null);
        }

        // Split into [ENABLE] and [DISABLE] sections
        var lines = script.Split('\n').Select(l => l.Trim()).ToList();
        var currentSection = (string?)null;
        var enableLines = new List<string>();
        var disableLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.Equals("[ENABLE]", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = "enable";
                continue;
            }
            if (line.Equals("[DISABLE]", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = "disable";
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (currentSection == "enable") enableLines.Add(line);
            else if (currentSection == "disable") disableLines.Add(line);
        }

        if (enableLines.Count > 0) enableSection = string.Join("\n", enableLines);
        if (disableLines.Count > 0) disableSection = string.Join("\n", disableLines);

        if (enableSection is null && disableSection is null)
            errors.Add("No [ENABLE] or [DISABLE] section found.");

        // Check for LuaCall (unsupported)
        if (script.Contains("LuaCall", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Script contains LuaCall() which is not supported (CE Lua engine required).");

        return new ScriptParseResult(errors.Count == 0, errors, warnings, enableSection, disableSection);
    }
}
