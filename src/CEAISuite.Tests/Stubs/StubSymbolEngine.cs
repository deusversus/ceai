using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubSymbolEngine : ISymbolEngine
{
    private readonly Dictionary<nuint, SymbolInfo> _symbols = new();
    private readonly Dictionary<nuint, SourceLineInfo> _sourceLines = new();

    public void AddSymbol(nuint address, string functionName, string moduleName, ulong displacement = 0) =>
        _symbols[address] = new SymbolInfo(functionName, moduleName, displacement);

    public void AddSourceLine(nuint address, string fileName, int lineNumber) =>
        _sourceLines[address] = new SourceLineInfo(fileName, lineNumber, address);

    public Task<bool> LoadSymbolsForModuleAsync(int processId, string moduleName, nuint baseAddress, long size) =>
        Task.FromResult(true);

    public SymbolInfo? ResolveAddress(nuint address) =>
        _symbols.TryGetValue(address, out var info) ? info : null;

    public SourceLineInfo? ResolveSourceLine(nuint address) =>
        _sourceLines.TryGetValue(address, out var info) ? info : null;

    public void Cleanup(int processId) { _symbols.Clear(); _sourceLines.Clear(); }
}
