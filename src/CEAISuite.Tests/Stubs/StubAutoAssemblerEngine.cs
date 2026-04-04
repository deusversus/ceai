using System.Collections.Concurrent;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubAutoAssemblerEngine : IAutoAssemblerEngine
{
    public ScriptParseResult NextParseResult { get; set; } = new(true, [], [], "[ENABLE]", "[DISABLE]");
    public ScriptExecutionResult NextEnableResult { get; set; } = new(true, null, [], []);
    public ScriptExecutionResult NextDisableResult { get; set; } = new(true, null, [], []);

    private readonly ConcurrentDictionary<string, nuint> _symbols = new(StringComparer.OrdinalIgnoreCase);

    public ScriptParseResult Parse(string script) => NextParseResult;

    public Task<ScriptExecutionResult> EnableAsync(int processId, string script, CancellationToken ct = default)
        => Task.FromResult(NextEnableResult);

    public Task<ScriptExecutionResult> DisableAsync(int processId, string script, CancellationToken ct = default)
        => Task.FromResult(NextDisableResult);

    public IReadOnlyList<RegisteredSymbol> GetRegisteredSymbols() =>
        _symbols.Select(kv => new RegisteredSymbol(kv.Key, kv.Value)).ToList();

    public nuint? ResolveSymbol(string name) =>
        _symbols.TryGetValue(name, out var addr) ? addr : null;

    public void RegisterSymbol(string name, nuint address) => _symbols[name] = address;
    public void UnregisterSymbol(string name) => _symbols.TryRemove(name, out _);
}
