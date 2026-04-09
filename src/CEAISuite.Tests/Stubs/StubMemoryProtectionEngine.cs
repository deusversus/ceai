using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubMemoryProtectionEngine : IMemoryProtectionEngine
{
    public MemoryRegionDescriptor NextRegion { get; set; } = new(
        (nuint)0x7FF00000, 4096,
        IsReadable: true, IsWritable: false, IsExecutable: true);

    public Task<ProtectionChangeResult> ChangeProtectionAsync(
        int processId, nuint address, long size, MemoryProtection newProtection,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ProtectionChangeResult(address, size, MemoryProtection.ExecuteRead, newProtection));

    public Task<MemoryAllocation> AllocateAsync(
        int processId, long size, MemoryProtection protection = MemoryProtection.ExecuteReadWrite,
        nuint preferredAddress = 0, CancellationToken cancellationToken = default)
        => Task.FromResult(new MemoryAllocation(preferredAddress == 0 ? (nuint)0xDEAD0000 : preferredAddress, size, protection));

    public Task<bool> FreeAsync(
        int processId, nuint address, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<MemoryRegionDescriptor> QueryProtectionAsync(
        int processId, nuint address, CancellationToken cancellationToken = default)
        => Task.FromResult(NextRegion);
}
