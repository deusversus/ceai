using System.ComponentModel;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public partial class AiToolFunctions
{
    /// <summary>
    /// Execute a Lua script and return its output. Supports CE API functions
    /// (readInteger, writeFloat, getAddress, etc.) when a process is attached.
    /// </summary>
    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
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
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
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
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
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

    /// <summary>Set a global variable in the Lua state.</summary>
    [Destructive]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [SearchHint("global", "variable", "lua state", "set variable")]
    [Description("Set a global variable in the Lua state. Useful for configuring Lua scripts before execution.")]
    public async Task<string> SetLuaGlobal(
        [Description("Variable name")] string name,
        [Description("Value to set")] string value,
        [Description("Value type: string, number, boolean, nil")] string type = "string")
    {
        if (luaEngine is null) return "Lua engine is not available.";
        if (string.IsNullOrWhiteSpace(name)) return "Variable name is required.";

        object? parsed = type.ToLowerInvariant() switch
        {
            "number" => double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null,
            "boolean" or "bool" => bool.TryParse(value, out var b) ? b : null,
            "nil" => null,
            _ => value // string
        };

        if (type.ToLowerInvariant() is "number" && parsed is null)
            return $"Cannot parse '{value}' as a number.";
        if (type.ToLowerInvariant() is "boolean" or "bool" && parsed is null)
            return $"Cannot parse '{value}' as a boolean. Use 'true' or 'false'.";

        await luaEngine.SetGlobalAsync(name, parsed).ConfigureAwait(false);
        return $"Lua global '{name}' set to {(parsed is null ? "nil" : $"{parsed} ({parsed.GetType().Name})")}.";
    }

    /// <summary>Get a global variable from the Lua state.</summary>
    [CEAISuite.Application.AgentLoop.ReadOnlyTool]
    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [SearchHint("global", "variable", "lua state", "get variable")]
    [Description("Get a global variable from the Lua state. Returns its value and type.")]
    public async Task<string> GetLuaGlobal(
        [Description("Variable name")] string name)
    {
        if (luaEngine is null) return "Lua engine is not available.";
        if (string.IsNullOrWhiteSpace(name)) return "Variable name is required.";

        var value = await luaEngine.GetGlobalAsync(name).ConfigureAwait(false);
        if (value is null)
            return $"'{name}' = nil";
        return $"'{name}' = {value} ({value.GetType().Name})";
    }
}
