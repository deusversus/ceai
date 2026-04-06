using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubLuaScriptEngine : ILuaScriptEngine
{
    public LuaExecutionResult NextExecuteResult { get; set; } = new(true, null, null, []);
    public LuaValidationResult NextValidateResult { get; set; } = new(true, []);
    public LuaExecutionResult NextEvaluateResult { get; set; } = new(true, "nil", null, []);

    public int LastProcessId { get; private set; }
    public string? LastExecutedCode { get; private set; }
    public string? LastEvaluatedExpression { get; private set; }
    public int ExecuteCallCount { get; private set; }

    private readonly Dictionary<string, object?> _globals = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? OutputWritten;

    public Task<LuaExecutionResult> ExecuteAsync(string luaCode, CancellationToken ct = default)
    {
        LastExecutedCode = luaCode;
        ExecuteCallCount++;
        return Task.FromResult(NextExecuteResult);
    }

    public Task<LuaExecutionResult> ExecuteAsync(string luaCode, int processId, CancellationToken ct = default)
    {
        LastExecutedCode = luaCode;
        LastProcessId = processId;
        ExecuteCallCount++;
        return Task.FromResult(NextExecuteResult);
    }

    public Task<LuaExecutionResult> EvaluateAsync(string expression, CancellationToken ct = default)
    {
        LastEvaluatedExpression = expression;
        return Task.FromResult(NextEvaluateResult);
    }

    public LuaValidationResult Validate(string luaCode) => NextValidateResult;

    public void SetGlobal(string name, object? value) => _globals[name] = value;
    public object? GetGlobal(string name) => _globals.TryGetValue(name, out var v) ? v : null;

    public void Reset()
    {
        _globals.Clear();
        ExecuteCallCount = 0;
        LastExecutedCode = null;
        LastEvaluatedExpression = null;
    }

    public LuaExecutionResult NextBreakpointCallbackResult { get; set; } = new(true, "true", null, []);
    public string? LastBreakpointCallbackName { get; private set; }
    private readonly HashSet<string> _registeredCallbacks = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterBreakpointCallback(string functionName)
    {
        _registeredCallbacks.Add(functionName);
    }

    public Task<LuaExecutionResult> InvokeBreakpointCallbackAsync(
        string functionName, BreakpointHitEvent hitEvent, CancellationToken ct = default)
    {
        LastBreakpointCallbackName = functionName;
        return Task.FromResult(NextBreakpointCallbackResult);
    }

    public bool IsCallbackRegistered(string name) => _registeredCallbacks.Contains(name);

    public void SimulateOutput(string line) => OutputWritten?.Invoke(line);
}
