namespace CEAISuite.Engine.Abstractions;

/// <summary>Result of enabling or disabling an Auto Assembler script.</summary>
public sealed record ScriptExecutionResult(
    bool Success,
    string? Error,
    IReadOnlyList<ScriptAllocation> Allocations,
    IReadOnlyList<ScriptPatch> Patches,
    IReadOnlyList<RegisteredSymbol>? RegisteredSymbols = null);

/// <summary>A block of memory allocated in the target process by an AA script.</summary>
public sealed record ScriptAllocation(string Name, nuint Address, int Size);

/// <summary>A patch applied to the target process (original bytes saved for restore).</summary>
public sealed record ScriptPatch(nuint Address, byte[] OriginalBytes, byte[] NewBytes);

/// <summary>A symbol registered via the registersymbol() AA directive.</summary>
public sealed record RegisteredSymbol(string Name, nuint Address);

/// <summary>Result of parsing/validating an AA script without executing it.</summary>
public sealed record ScriptParseResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string? EnableSection,
    string? DisableSection);

/// <summary>
/// Engine for parsing and executing Cheat Engine Auto Assembler scripts.
/// Supports alloc/dealloc, define, label, assert, db, nop, and x86/x64 assembly via Keystone.
/// </summary>
public interface IAutoAssemblerEngine
{
    /// <summary>Execute the [ENABLE] section of an AA script against a target process.</summary>
    Task<ScriptExecutionResult> EnableAsync(int processId, string script, CancellationToken ct = default);

    /// <summary>Execute the [DISABLE] section of an AA script against a target process.</summary>
    Task<ScriptExecutionResult> DisableAsync(int processId, string script, CancellationToken ct = default);

    /// <summary>Parse and validate an AA script without executing it.</summary>
    ScriptParseResult Parse(string script);

    /// <summary>Get all symbols registered via registersymbol() across all scripts.</summary>
    IReadOnlyList<RegisteredSymbol> GetRegisteredSymbols();

    /// <summary>Resolve a single symbol name to its address. Returns null if not found.</summary>
    nuint? ResolveSymbol(string name);
}
