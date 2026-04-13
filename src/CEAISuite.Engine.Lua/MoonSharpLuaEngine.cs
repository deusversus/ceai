using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;

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
    private readonly IDisassemblyEngine? _disassemblyEngine;
    private readonly IBreakpointEngine? _breakpointEngine;
    private readonly IScanEngine? _scanEngine;
    private readonly IMemoryProtectionEngine? _memoryProtectionEngine;
    private readonly Dictionary<string, DynValue> _moduleCache = new(StringComparer.OrdinalIgnoreCase);
    private Script _script;
    private int? _currentProcessId;

    /// <summary>
    /// Additional directories to search for Lua modules via require().
    /// Defaults to %LOCALAPPDATA%/CEAISuite/scripts/lib/.
    /// </summary>
    public List<string> ModuleSearchPaths { get; } = [];

    /// <summary>Modules allowed in the sandbox — no OS, IO, or dynamic loading.</summary>
    internal static readonly CoreModules SandboxModules =
        CoreModules.Preset_HardSandbox
        | CoreModules.Coroutine
        | CoreModules.Bit32;

    public event Action<string>? OutputWritten;

    /// <summary>
    /// Maximum number of instructions a script may execute before being terminated.
    /// Zero means unlimited (default). When set, a debug hook counts each instruction
    /// and throws <see cref="ScriptRuntimeException"/> once the limit is exceeded.
    /// </summary>
    /// <remarks>
    /// Memory limits: MoonSharp has no per-script memory tracking API. Memory is managed
    /// by .NET's GC, and true per-script quotas would require OS-level process isolation
    /// (AppDomain is deprecated in .NET Core+). The current approach — execution timeout
    /// plus instruction counting — is the standard mitigation for MoonSharp and effectively
    /// bounds resource consumption for runaway scripts.
    /// </remarks>
    public long MaxInstructions { get; set; }

    public MoonSharpLuaEngine(
        IEngineFacade? engineFacade = null,
        IAutoAssemblerEngine? autoAssembler = null,
        ILuaFormHost? formHost = null,
        TimeSpan? executionTimeout = null,
        long maxInstructions = 0,
        IDisassemblyEngine? disassemblyEngine = null,
        IBreakpointEngine? breakpointEngine = null,
        IScanEngine? scanEngine = null,
        IMemoryProtectionEngine? memoryProtectionEngine = null)
    {
        _engineFacade = engineFacade;
        _autoAssembler = autoAssembler;
        _formHost = formHost;
        _disassemblyEngine = disassemblyEngine;
        _breakpointEngine = breakpointEngine;
        _scanEngine = scanEngine;
        _memoryProtectionEngine = memoryProtectionEngine;
        _executionTimeout = executionTimeout ?? TimeSpan.FromSeconds(30);
        MaxInstructions = maxInstructions;
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

    public async Task SetGlobalAsync(string name, object? value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
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

    public async Task<object?> GetGlobalAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
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
            _moduleCache.Clear();
            _script = CreateSandboxedScript();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _currentProcessId = null;
            _breakpointCallbacks.Clear();
            _moduleCache.Clear();
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

        // S3: Controlled require() module loader
        RegisterRequire(script);

        // Always register data conversion utilities (no engine dependency)
        LuaDataConversionBindings.Register(script);

        if (_engineFacade is not null)
        {
            CeApiBindings.Register(script, this, _engineFacade, _autoAssembler, _formHost);
            LuaModuleBindings.Register(script, this, _engineFacade, _scanEngine, _autoAssembler);

            if (_disassemblyEngine is not null)
                LuaDisassemblyBindings.Register(script, this, _disassemblyEngine, _engineFacade, _autoAssembler);

            if (_breakpointEngine is not null)
                LuaDebuggerBindings.Register(script, this, _breakpointEngine, _engineFacade, _autoAssembler);

            if (_scanEngine is not null)
                LuaScanBindings.Register(script, this, _scanEngine, _engineFacade, _autoAssembler);

            if (_memoryProtectionEngine is not null)
                LuaMemoryManagementBindings.Register(script, this, _memoryProtectionEngine, _engineFacade, _autoAssembler);
        }

        if (_formHost is not null)
        {
            var formBindings = new CeFormBindings();
            formBindings.Register(script, _formHost);
        }

        // Attach instruction-limit debugger when a limit is configured
        if (MaxInstructions > 0)
        {
            _instructionDebugger = new InstructionLimitDebugger(MaxInstructions);
            script.AttachDebugger(_instructionDebugger);
            script.DebuggerEnabled = true;
        }
        else
        {
            _instructionDebugger = null;
        }

        return script;
    }

    private InstructionLimitDebugger? _instructionDebugger;

    /// <summary>
    /// Registers a sandboxed require() function that loads Lua modules from whitelisted directories only.
    /// Modules are cached after first load (standard Lua package.loaded semantics).
    /// </summary>
    private void RegisterRequire(Script script)
    {
        script.Globals["require"] = (Func<string, DynValue>)(moduleName =>
        {
            // Check cache first
            if (_moduleCache.TryGetValue(moduleName, out var cached))
                return cached;

            // Build search paths
            var searchDirs = new List<string>();
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var defaultLibPath = Path.Combine(localAppData, "CEAISuite", "scripts", "lib");
            if (Directory.Exists(defaultLibPath))
                searchDirs.Add(defaultLibPath);
            searchDirs.AddRange(ModuleSearchPaths.Where(Directory.Exists));

            // Search for the module file
            var fileName = moduleName.Replace('.', Path.DirectorySeparatorChar) + ".lua";
            string? foundPath = null;
            foreach (var dir in searchDirs)
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    foundPath = candidate;
                    break;
                }
            }

            if (foundPath is null)
                throw new ScriptRuntimeException($"module '{moduleName}' not found in search paths");

            // Security: verify the resolved path is actually inside a search directory
            var fullPath = Path.GetFullPath(foundPath);
            if (!searchDirs.Any(dir => fullPath.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)))
                throw new ScriptRuntimeException($"module '{moduleName}' path escapes search directories");

            // Load and execute the module
            var source = File.ReadAllText(fullPath);
            var result = script.DoString(source, codeFriendlyName: moduleName);

            // Cache the result (nil becomes true, matching Lua convention)
            var moduleValue = result.Type == DataType.Nil ? DynValue.True : result;
            _moduleCache[moduleName] = moduleValue;
            return moduleValue;
        });
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

                // Reset instruction counter before each execution
                _instructionDebugger?.Reset();

                // Use a cancellation flag checked by MoonSharp's DebuggerEnabled hook
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_executionTimeout);
                var token = timeoutCts.Token;

                var result = await Task.Run(() =>
                {
                    _script.Options.DebugInput = _ => throw new ScriptRuntimeException("input() is disabled");
                    _script.Options.DebugPrint = _ => { };
                    var callResult = _script.Call(func);
                    token.ThrowIfCancellationRequested();
                    return callResult;
                }, CancellationToken.None).WaitAsync(token).ConfigureAwait(false);

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
