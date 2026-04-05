namespace CEAISuite.Engine.Abstractions;

/// <summary>Resolved symbol information for an address.</summary>
public sealed record SymbolInfo(
    string FunctionName,
    string ModuleName,
    ulong Displacement)
{
    /// <summary>Formatted display: "module!Function+0x1A" or "module!Function" if displacement is 0.</summary>
    public string DisplayName => Displacement > 0
        ? $"{ModuleName}!{FunctionName}+0x{Displacement:X}"
        : $"{ModuleName}!{FunctionName}";
}

/// <summary>Engine for loading debug symbols and resolving addresses to function names.</summary>
public interface ISymbolEngine
{
    /// <summary>Load symbols for a module (PDB/exports). Returns true if symbols were found.</summary>
    Task<bool> LoadSymbolsForModuleAsync(int processId, string moduleName, nuint baseAddress, long size);

    /// <summary>Resolve an address to a symbol. Returns null if no symbol found.</summary>
    SymbolInfo? ResolveAddress(nuint address);

    /// <summary>Release symbol resources for a process.</summary>
    void Cleanup(int processId);
}
