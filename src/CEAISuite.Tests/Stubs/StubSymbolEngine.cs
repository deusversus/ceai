using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubSymbolEngine : ISymbolEngine
{
    private readonly Dictionary<nuint, SymbolInfo> _symbols = new();

    public void AddSymbol(nuint address, string functionName, string moduleName, ulong displacement = 0) =>
        _symbols[address] = new SymbolInfo(functionName, moduleName, displacement);

    public Task<bool> LoadSymbolsForModuleAsync(int processId, string moduleName, nuint baseAddress, long size) =>
        Task.FromResult(true);

    public SymbolInfo? ResolveAddress(nuint address) =>
        _symbols.TryGetValue(address, out var info) ? info : null;

    public void Cleanup(int processId) => _symbols.Clear();
}
