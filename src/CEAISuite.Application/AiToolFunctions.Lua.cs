using System.ComponentModel;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public partial class AiToolFunctions
{
    /// <summary>
    /// Execute a Lua script and return its output. Supports CE API functions
    /// (readInteger, writeFloat, getAddress, etc.) when a process is attached.
    /// </summary>
    [Description("Execute a Lua script. Use for complex automation, CE table scripts, or multi-step memory operations.")]
    public async Task<string> ExecuteLuaScript(
        [Description("Lua script code to execute")] string code,
        [Description("Process ID (uses attached process if omitted)")] int? processId = null)
    {
        if (luaEngine is null)
            return "Lua engine is not available.";

        var pid = processId ?? engineFacade.AttachedProcessId;
        var result = pid.HasValue
            ? await luaEngine.ExecuteAsync(code, pid.Value).ConfigureAwait(false)
            : await luaEngine.ExecuteAsync(code).ConfigureAwait(false);

        if (!result.Success)
            return $"Lua error: {result.Error}";

        var parts = new List<string>();
        if (result.OutputLines.Count > 0)
            parts.Add(string.Join("\n", result.OutputLines));
        if (result.ReturnValue is not null)
            parts.Add($"Return: {result.ReturnValue}");
        return parts.Count > 0 ? string.Join("\n", parts) : "Script executed successfully (no output).";
    }

    /// <summary>Validate Lua script syntax without executing it.</summary>
    [CEAISuite.Application.AgentLoop.ReadOnlyTool]
    [Description("Validate Lua script syntax without executing. Returns valid/invalid with error details.")]
    public Task<string> ValidateLuaScript(
        [Description("Lua script code to validate")] string code)
    {
        if (luaEngine is null)
            return Task.FromResult("Lua engine is not available.");

        var result = luaEngine.Validate(code);
        return Task.FromResult(result.IsValid
            ? "Valid Lua syntax."
            : $"Invalid: {string.Join("; ", result.Errors)}");
    }

    /// <summary>Evaluate a single Lua expression and return its result.</summary>
    [CEAISuite.Application.AgentLoop.ReadOnlyTool]
    [Description("Evaluate a Lua expression and return the result. Useful for quick calculations or reading values.")]
    public async Task<string> EvaluateLuaExpression(
        [Description("Lua expression to evaluate (e.g., 'readInteger(getAddress(\"game.exe+0x1234\"))')")] string expression)
    {
        if (luaEngine is null)
            return "Lua engine is not available.";

        var result = await luaEngine.EvaluateAsync(expression).ConfigureAwait(false);
        return result.Success
            ? result.ReturnValue ?? "nil"
            : $"Lua error: {result.Error}";
    }
}
