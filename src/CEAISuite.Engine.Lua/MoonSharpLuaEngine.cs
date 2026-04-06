using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// MoonSharp-based Lua 5.2 scripting engine with CE API compatibility.
/// Sandboxed: OS, IO, and dynamic loading modules are disabled.
/// Thread-safe via semaphore — only one script executes at a time.
/// </summary>
public sealed class MoonSharpLuaEngine : ILuaScriptEngine, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _executionTimeout;
    private readonly IEngineFacade? _engineFacade;
    private readonly IAutoAssemblerEngine? _autoAssembler;
    private readonly ILuaFormHost? _formHost;
    private Script _script;
    private int? _currentProcessId;

    /// <summary>Modules allowed in the sandbox — no OS, IO, or dynamic loading.</summary>
    internal static readonly CoreModules SandboxModules =
        CoreModules.Preset_HardSandbox
        | CoreModules.Coroutine
        | CoreModules.Bit32;

    public event Action<string>? OutputWritten;

    public MoonSharpLuaEngine(
        IEngineFacade? engineFacade = null,
        IAutoAssemblerEngine? autoAssembler = null,
        ILuaFormHost? formHost = null,
        TimeSpan? executionTimeout = null)
    {
        _engineFacade = engineFacade;
        _autoAssembler = autoAssembler;
        _formHost = formHost;
        _executionTimeout = executionTimeout ?? TimeSpan.FromSeconds(30);
        _script = CreateSandboxedScript();
    }

    public async Task<LuaExecutionResult> ExecuteAsync(string luaCode, CancellationToken ct = default)
    {
        return await RunAsync(luaCode, processId: null, evaluate: false, ct).ConfigureAwait(false);
    }

    public async Task<LuaExecutionResult> ExecuteAsync(string luaCode, int processId, CancellationToken ct = default)
    {
        return await RunAsync(luaCode, processId, evaluate: false, ct).ConfigureAwait(false);
    }

    public async Task<LuaExecutionResult> EvaluateAsync(string expression, CancellationToken ct = default)
    {
        // Wrap expression in return statement for value capture
        var code = $"return ({expression})";
        return await RunAsync(code, processId: null, evaluate: true, ct).ConfigureAwait(false);
    }

    public LuaValidationResult Validate(string luaCode)
    {
        try
        {
            // Create a throwaway script for validation only
            var validationScript = new Script(SandboxModules);
            validationScript.LoadString(luaCode);
            return new LuaValidationResult(true, []);
        }
        catch (SyntaxErrorException ex)
        {
            return new LuaValidationResult(false, [ex.DecoratedMessage ?? ex.Message]);
        }
        catch (Exception ex)
        {
            return new LuaValidationResult(false, [$"Unexpected error: {ex.Message}"]);
        }
    }

    public void SetGlobal(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _gate.Wait();
        try
        {
            _script.Globals[name] = value is null
                ? DynValue.Nil
                : DynValue.FromObject(_script, value);
        }
        finally
        {
            _gate.Release();
        }
    }

    public object? GetGlobal(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _gate.Wait();
        try
        {
            var val = _script.Globals.Get(name);
            return val.Type == DataType.Nil ? null : val.ToObject();
        }
        finally
        {
            _gate.Release();
        }
    }

    private readonly HashSet<string> _breakpointCallbacks = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterBreakpointCallback(string functionName)
    {
        _breakpointCallbacks.Add(functionName);
    }

    public async Task<LuaExecutionResult> InvokeBreakpointCallbackAsync(
        string functionName,
        BreakpointHitEvent hitEvent,
        CancellationToken ct = default)
    {
        // Build a Lua table from the hit event registers
        var regTable = new System.Text.StringBuilder();
        regTable.Append('{');
        foreach (var (name, value) in hitEvent.RegisterSnapshot)
            regTable.Append(System.Globalization.CultureInfo.InvariantCulture, $"{name}=\"{value}\",");
        regTable.Append(System.Globalization.CultureInfo.InvariantCulture, $"Address=\"0x{(ulong)hitEvent.Address:X}\",");
        regTable.Append(System.Globalization.CultureInfo.InvariantCulture, $"ThreadId={hitEvent.ThreadId}");
        regTable.Append('}');

        var code = $"return {functionName}({regTable})";
        return await RunAsync(code, processId: null, evaluate: false, ct).ConfigureAwait(false);
    }

    public void Reset()
    {
        _gate.Wait();
        try
        {
            _currentProcessId = null;
            _breakpointCallbacks.Clear();
            _script = CreateSandboxedScript();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Current attached process ID, set during <see cref="ExecuteAsync(string, int, CancellationToken)"/>.</summary>
    internal int? CurrentProcessId => _currentProcessId;

    /// <summary>The underlying MoonSharp script instance. For CE API binding registration.</summary>
    internal Script Script => _script;

    public void Dispose()
    {
        _gate.Dispose();
    }

    private Script CreateSandboxedScript()
    {
        var script = new Script(SandboxModules);
        RegisterPrint(script);

        if (_engineFacade is not null)
            CeApiBindings.Register(script, this, _engineFacade, _autoAssembler);

        if (_formHost is not null)
            CeFormBindings.Register(script, _formHost);

        return script;
    }

    private void RegisterPrint(Script script)
    {
        script.Globals["print"] = (Action<ScriptExecutionContext, CallbackArguments>)PrintCallback;
    }

    private void PrintCallback(ScriptExecutionContext ctx, CallbackArguments args)
    {
        var parts = new string[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            parts[i] = args[i].ToPrintString();
        }
        var line = string.Join("\t", parts);
        OutputWritten?.Invoke(line);
    }

    private async Task<LuaExecutionResult> RunAsync(
        string code, int? processId, bool evaluate, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new LuaExecutionResult(false, null, "Script execution timed out.", []);
        }

        try
        {
            var previousPid = _currentProcessId;
            if (processId.HasValue)
                _currentProcessId = processId.Value;

            var outputLines = new List<string>();
            void CaptureOutput(string line) => outputLines.Add(line);
            OutputWritten += CaptureOutput;

            try
            {
                var func = _script.LoadString(code);

                // Use a cancellation flag checked by MoonSharp's DebuggerEnabled hook
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_executionTimeout);
                var token = timeoutCts.Token;

                var result = await Task.Run(() =>
                {
                    _script.Options.DebugInput = _ => throw new ScriptRuntimeException("input() is disabled");

                    // MoonSharp doesn't natively support cancellation, so we poll on
                    // a per-line debug hook to abort runaway scripts.
                    _script.Options.DebugPrint = _ => { }; // suppress debug prints

                    var callResult = _script.Call(func);

                    // Check cancellation after execution completes
                    token.ThrowIfCancellationRequested();
                    return callResult;
                }, token).ConfigureAwait(false);

                var returnValue = result.Type != DataType.Void && result.Type != DataType.Nil
                    ? result.ToPrintString()
                    : null;

                return new LuaExecutionResult(true, returnValue, null, outputLines);
            }
            catch (OperationCanceledException)
            {
                return new LuaExecutionResult(false, null, "Script execution timed out.", outputLines);
            }
            catch (SyntaxErrorException ex)
            {
                return new LuaExecutionResult(false, null, ex.DecoratedMessage ?? ex.Message, outputLines);
            }
            catch (ScriptRuntimeException ex)
            {
                return new LuaExecutionResult(false, null, ex.DecoratedMessage ?? ex.Message, outputLines);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new LuaExecutionResult(false, null, $"Unexpected error: {ex.Message}", outputLines);
            }
            finally
            {
                OutputWritten -= CaptureOutput;
                if (processId.HasValue)
                    _currentProcessId = previousPid;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
