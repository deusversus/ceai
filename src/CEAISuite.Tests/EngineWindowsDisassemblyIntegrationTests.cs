using System.Globalization;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Tests;

/// <summary>
/// Integration tests for <see cref="WindowsDisassemblyEngine"/> against the
/// real CEAISuite.Tests.Harness process.
/// </summary>
[Trait("Category", "Integration")]
public class EngineWindowsDisassemblyIntegrationTests
{
    [Fact]
    public async Task DisassembleAsync_HarnessEntryPoint_ReturnsInstructions()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var facade = new WindowsEngineFacade(NullLogger<WindowsEngineFacade>.Instance);
        var disasm = new WindowsDisassemblyEngine();

        var attachment = await facade.AttachAsync(harness.ProcessId, TestContext.Current.CancellationToken);

        // Find the main module (the harness exe itself)
        var mainModule = attachment.Modules.FirstOrDefault(m =>
            m.Name.Contains("CEAISuite.Tests.Harness", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(mainModule);

        var result = await disasm.DisassembleAsync(
            harness.ProcessId, mainModule.BaseAddress, maxInstructions: 10,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Instructions);
        Assert.True(result.TotalBytesDisassembled > 0, "Should have disassembled some bytes");
        Assert.Equal(mainModule.BaseAddress, result.StartAddress);

        // Each instruction should have a non-empty mnemonic
        foreach (var instr in result.Instructions)
        {
            Assert.False(string.IsNullOrWhiteSpace(instr.Mnemonic), "Instruction mnemonic should not be empty");
            Assert.True(instr.Length > 0, "Instruction length should be positive");
        }
    }

    [Fact]
    public async Task DisassembleAsync_AllocatedMemory_HandlesNonCode()
    {
        await using var harness = await TestHarnessProcess.StartAsync(TestContext.Current.CancellationToken);
        var disasm = new WindowsDisassemblyEngine();

        // Allocate memory filled with 0xDE (not valid code)
        var allocResp = await harness.SendCommandAsync("ALLOC 256");
        Assert.NotNull(allocResp);
        Assert.StartsWith("ALLOC_OK:", allocResp);
        var address = nuint.Parse(allocResp.Split(':')[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var result = await disasm.DisassembleAsync(
            harness.ProcessId, address, maxInstructions: 10,
            cancellationToken: TestContext.Current.CancellationToken);

        // Should return instructions (possibly db entries for invalid opcodes, or
        // actual decoded instructions since 0xDE is FISUB on x86)
        Assert.NotEmpty(result.Instructions);
        Assert.True(result.TotalBytesDisassembled > 0, "Should have produced output for non-code bytes");
    }
}
