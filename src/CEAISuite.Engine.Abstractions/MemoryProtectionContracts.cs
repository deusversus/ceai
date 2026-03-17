namespace CEAISuite.Engine.Abstractions;

[Flags]
public enum MemoryProtection
{
    NoAccess          = 0x01,
    ReadOnly          = 0x02,
    ReadWrite         = 0x04,
    WriteCopy         = 0x08,
    Execute           = 0x10,
    ExecuteRead       = 0x20,
    ExecuteReadWrite  = 0x40,
    ExecuteWriteCopy  = 0x80,
    Guard             = 0x100,
    NoCache           = 0x200,
    WriteCombine      = 0x400
}

public sealed record MemoryAllocation(
    nuint BaseAddress,
    long Size,
    MemoryProtection Protection);

public sealed record ProtectionChangeResult(
    nuint Address,
    long Size,
    MemoryProtection OldProtection,
    MemoryProtection NewProtection);

public interface IMemoryProtectionEngine
{
    /// <summary>Change memory page protection for a region.</summary>
    Task<ProtectionChangeResult> ChangeProtectionAsync(
        int processId,
        nuint address,
        long size,
        MemoryProtection newProtection,
        CancellationToken cancellationToken = default);

    /// <summary>Allocate memory in the target process.</summary>
    Task<MemoryAllocation> AllocateAsync(
        int processId,
        long size,
        MemoryProtection protection = MemoryProtection.ExecuteReadWrite,
        nuint preferredAddress = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Free previously allocated memory.</summary>
    Task<bool> FreeAsync(
        int processId,
        nuint address,
        CancellationToken cancellationToken = default);

    /// <summary>Query protection info for an address.</summary>
    Task<MemoryRegionDescriptor> QueryProtectionAsync(
        int processId,
        nuint address,
        CancellationToken cancellationToken = default);
}
