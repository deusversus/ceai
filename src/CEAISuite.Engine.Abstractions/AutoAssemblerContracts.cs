namespace CEAISuite.Engine.Abstractions;

/// <summary>Result of enabling or disabling an Auto Assembler script.</summary>
public sealed record ScriptExecutionResult(
    bool Success,
    string? Error,
    IReadOnlyList<ScriptAllocation> Allocations,
    IReadOnlyList<ScriptPatch> Patches);

/// <summary>A block of memory allocated in the target process by an AA script.</summary>
public sealed record ScriptAllocation(string Name, nuint Address, int Size);

/// <summary>A patch applied to the target process (original bytes saved for restore).</summary>
public sealed record ScriptPatch(nuint Address, byte[] OriginalBytes, byte[] NewBytes);

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
}
