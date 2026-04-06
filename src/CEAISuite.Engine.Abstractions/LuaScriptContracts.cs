namespace CEAISuite.Engine.Abstractions;

/// <summary>Result of executing a Lua script or expression.</summary>
public sealed record LuaExecutionResult(
    bool Success,
    string? ReturnValue,
    string? Error,
    IReadOnlyList<string> OutputLines);

/// <summary>Result of validating Lua syntax without execution.</summary>
public sealed record LuaValidationResult(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>
/// Engine for executing Lua scripts with optional CE API bindings.
/// Sandboxed by default — OS, IO, and dynamic loading modules are disabled.
/// </summary>
public interface ILuaScriptEngine
{
    /// <summary>Execute a Lua script without process context (no memory operations).</summary>
    Task<LuaExecutionResult> ExecuteAsync(string luaCode, CancellationToken ct = default);

    /// <summary>Execute a Lua script with process context, enabling memory read/write operations.</summary>
    Task<LuaExecutionResult> ExecuteAsync(string luaCode, int processId, CancellationToken ct = default);

    /// <summary>Evaluate a single Lua expression and return its value as a string.</summary>
    Task<LuaExecutionResult> EvaluateAsync(string expression, CancellationToken ct = default);

    /// <summary>Validate Lua syntax without executing.</summary>
    LuaValidationResult Validate(string luaCode);

    /// <summary>Set a global variable accessible from Lua scripts.</summary>
    void SetGlobal(string name, object? value);

    /// <summary>Get the current value of a Lua global variable.</summary>
    object? GetGlobal(string name);

    /// <summary>Reset the Lua state, clearing all variables and loaded scripts.</summary>
    void Reset();

    /// <summary>Fired when a Lua script calls print() or writes output.</summary>
    event Action<string>? OutputWritten;

    /// <summary>Register a Lua function name as a breakpoint callback.</summary>
    void RegisterBreakpointCallback(string functionName);

    /// <summary>Invoke a registered breakpoint callback with hit event data.</summary>
    Task<LuaExecutionResult> InvokeBreakpointCallbackAsync(
        string functionName,
        BreakpointHitEvent hitEvent,
        CancellationToken ct = default);
}
