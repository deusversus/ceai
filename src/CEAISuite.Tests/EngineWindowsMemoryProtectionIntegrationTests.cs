using System.Globalization;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;

namespace CEAISuite.Tests;

/// <summary>
/// Integration tests for <see cref="WindowsMemoryProtectionEngine"/> against
/// the real CEAISuite.Tests.Harness process.
/// </summary>
[Trait("Category", "Integration")]
public class EngineWindowsMemoryProtectionIntegrationTests
{
    [Fact]
    public async Task AllocateAsync_ReturnsValidAddress()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var engine = new WindowsMemoryProtectionEngine();

        var allocation = await engine.AllocateAsync(
            harness.ProcessId, 4096, MemoryProtection.ReadWrite,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(nuint.Zero, allocation.BaseAddress);
        Assert.Equal(4096, allocation.Size);
        Assert.Equal(MemoryProtection.ReadWrite, allocation.Protection);

        // Cleanup
        await engine.FreeAsync(harness.ProcessId, allocation.BaseAddress, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FreeAsync_AllocatedMemory_ReturnsTrue()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var engine = new WindowsMemoryProtectionEngine();

        var allocation = await engine.AllocateAsync(
            harness.ProcessId, 4096, MemoryProtection.ReadWrite,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(nuint.Zero, allocation.BaseAddress);

        var freed = await engine.FreeAsync(harness.ProcessId, allocation.BaseAddress, TestContext.Current.CancellationToken);

        Assert.True(freed, "FreeAsync should return true for a valid allocation");
    }

    [Fact]
    public async Task QueryProtectionAsync_AllocatedRegion_ReturnsProperties()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var engine = new WindowsMemoryProtectionEngine();

        // Use the harness ALLOC command which allocates RWX memory
        var allocResp = await harness.SendCommandAsync("ALLOC 4096");
        Assert.NotNull(allocResp);
        Assert.StartsWith("ALLOC_OK:", allocResp);
        var address = nuint.Parse(allocResp.Split(':')[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var region = await engine.QueryProtectionAsync(
            harness.ProcessId, address, TestContext.Current.CancellationToken);

        // The harness allocates with PAGE_EXECUTE_READWRITE
        Assert.True(region.IsReadable, "RWX region should be readable");
        Assert.True(region.IsWritable, "RWX region should be writable");
        Assert.True(region.IsExecutable, "RWX region should be executable");
        Assert.True(region.RegionSize > 0, "Region size should be positive");
    }

    [Fact]
    public async Task ChangeProtectionAsync_ReadOnly_ChangesAndReturnsOldProtection()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var engine = new WindowsMemoryProtectionEngine();

        // Allocate RW memory via the engine
        var allocation = await engine.AllocateAsync(
            harness.ProcessId, 4096, MemoryProtection.ReadWrite,
            cancellationToken: TestContext.Current.CancellationToken);

        // Change from ReadWrite to ReadOnly
        var changeResult = await engine.ChangeProtectionAsync(
            harness.ProcessId, allocation.BaseAddress, 4096, MemoryProtection.ReadOnly,
            TestContext.Current.CancellationToken);

        Assert.Equal(MemoryProtection.ReadWrite, changeResult.OldProtection);
        Assert.Equal(MemoryProtection.ReadOnly, changeResult.NewProtection);

        // Verify the change took effect
        var region = await engine.QueryProtectionAsync(
            harness.ProcessId, allocation.BaseAddress, TestContext.Current.CancellationToken);
        Assert.True(region.IsReadable, "Region should still be readable");
        Assert.False(region.IsWritable, "Region should no longer be writable after changing to ReadOnly");

        // Cleanup
        await engine.FreeAsync(harness.ProcessId, allocation.BaseAddress, TestContext.Current.CancellationToken);
    }
}
