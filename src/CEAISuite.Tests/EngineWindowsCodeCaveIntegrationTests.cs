using System.Globalization;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Tests;

/// <summary>
/// Integration tests for <see cref="WindowsCodeCaveEngine"/> against the real
/// CEAISuite.Tests.Harness process. Tests the code cave allocation and
/// memory read/write capabilities without installing full JMP hooks (which
/// require specific code layout at the target address).
/// </summary>
[Trait("Category", "Integration")]
public class EngineWindowsCodeCaveIntegrationTests
{
    [Fact]
    public async Task AllocateCodeCave_HarnessProcess_ReturnsValidAddress()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var protectionEngine = new WindowsMemoryProtectionEngine();

        // Allocate executable memory in the harness process (simulates code cave allocation)
        var allocation = await protectionEngine.AllocateAsync(
            harness.ProcessId, 4096, MemoryProtection.ExecuteReadWrite,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(nuint.Zero, allocation.BaseAddress);
        Assert.Equal(4096, allocation.Size);
        Assert.Equal(MemoryProtection.ExecuteReadWrite, allocation.Protection);

        // Verify the allocation is queryable
        var region = await protectionEngine.QueryProtectionAsync(
            harness.ProcessId, allocation.BaseAddress, TestContext.Current.CancellationToken);
        Assert.True(region.IsReadable);
        Assert.True(region.IsWritable);
        Assert.True(region.IsExecutable);

        // Cleanup
        await protectionEngine.FreeAsync(harness.ProcessId, allocation.BaseAddress, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WriteAndReadCodeCave()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var protectionEngine = new WindowsMemoryProtectionEngine();
        var facade = new WindowsEngineFacade(NullLogger<WindowsEngineFacade>.Instance);
        await facade.AttachAsync(harness.ProcessId, TestContext.Current.CancellationToken);

        // Allocate executable memory (code cave)
        var allocation = await protectionEngine.AllocateAsync(
            harness.ProcessId, 4096, MemoryProtection.ExecuteReadWrite,
            cancellationToken: TestContext.Current.CancellationToken);

        // Write a byte pattern that looks like code (NOP sled + INT3)
        var caveBytes = new byte[]
        {
            0x90, 0x90, 0x90, 0x90, // NOP NOP NOP NOP
            0x90, 0x90, 0x90, 0x90, // NOP NOP NOP NOP
            0xCC,                     // INT3
            0xC3                      // RET
        };

        var written = await facade.WriteBytesAsync(
            harness.ProcessId, allocation.BaseAddress, caveBytes,
            TestContext.Current.CancellationToken);
        Assert.Equal(caveBytes.Length, written);

        // Read it back and verify
        var readResult = await facade.ReadMemoryAsync(
            harness.ProcessId, allocation.BaseAddress, caveBytes.Length,
            TestContext.Current.CancellationToken);
        Assert.Equal(caveBytes, readResult.Bytes.ToArray());

        // Bonus: disassemble the code cave to verify it contains valid instructions
        var disasm = new WindowsDisassemblyEngine();
        var disasmResult = await disasm.DisassembleAsync(
            harness.ProcessId, allocation.BaseAddress, maxInstructions: 10,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(disasmResult.Instructions);
        // Should find NOP instructions
        Assert.Contains(disasmResult.Instructions, i =>
            i.Mnemonic.Equals("nop", StringComparison.OrdinalIgnoreCase));

        // Cleanup
        await protectionEngine.FreeAsync(harness.ProcessId, allocation.BaseAddress, TestContext.Current.CancellationToken);
    }
}
