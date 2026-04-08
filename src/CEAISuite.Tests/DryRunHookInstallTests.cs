using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for AiToolFunctions.DryRunHookInstall edge cases:
/// valid analysis, invalid addresses, null engine, dead process, already-hooked address.
/// </summary>
public sealed class DryRunHookInstallTests
{
    /// <summary>Use the current test process PID so IsProcessAlive returns true.</summary>
    private static readonly int AlivePid = Environment.ProcessId;

    private readonly StubEngineFacade _facade = new();
    private readonly StubDisassemblyEngine _disasmEngine = new();
    private readonly StubScanEngine _scanEngine = new();
    private readonly StubSessionRepository _sessionRepo = new();
    private readonly StubCodeCaveEngine _codeCaveEngine = new();

    /// <summary>
    /// Build an AiToolFunctions with configurable optional engines.
    /// </summary>
    private AiToolFunctions BuildSut(
        ICodeCaveEngine? codeCave = null,
        InlineStubMemoryProtectionEngine? memProtection = null)
    {
        var addressTable = new AddressTableService(_facade);
        var disasmService = new DisassemblyService(_disasmEngine);
        var scanService = new ScanService(_scanEngine);
        var dashboardService = new WorkspaceDashboardService(_facade, _sessionRepo);
        var scriptGenService = new ScriptGenerationService();

        return new AiToolFunctions(
            engineFacade: _facade,
            dashboardService: dashboardService,
            scanService: scanService,
            addressTableService: addressTable,
            disassemblyService: disasmService,
            scriptGenerationService: scriptGenService,
            codeCaveEngine: codeCave,
            memoryProtectionEngine: memProtection);
    }

    // ── A: Normal case — valid address returns byte-change preview ──

    [Fact]
    public async Task DryRunHookInstall_ValidAddress_ReturnsBytePreview()
    {
        // StubDisassemblyEngine returns instructions starting at 0x7FF00100
        // with enough bytes (5+4+5+3+3+2+4+6+5+4+1 = 42 bytes total) to exceed the 14-byte JMP minimum.
        // Seed memory at that address so ReadMemoryAsync returns non-zero bytes.
        _facade.WriteMemoryDirect(
            (nuint)0x7FF00100,
            new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x83, 0xEC, 0x20, 0xE8, 0x12, 0x34, 0x56, 0x00, 0x48, 0x8B });

        var sut = BuildSut(codeCave: _codeCaveEngine);
        var result = await sut.DryRunHookInstall(processId: AlivePid, address: "0x7FF00100");

        // Should contain dry-run header and stolen bytes info
        Assert.Contains("Dry-Run Hook Preview", result, StringComparison.Ordinal);
        Assert.Contains("Stolen Bytes", result, StringComparison.Ordinal);
        Assert.Contains("Instructions to be relocated", result, StringComparison.Ordinal);
        Assert.Contains("Bytes to be overwritten", result, StringComparison.Ordinal);
        Assert.Contains("Estimated Trampoline Size", result, StringComparison.Ordinal);
        Assert.Contains("SAFE TO HOOK", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunHookInstall_ValidAddress_ShowsRipRelativeWarning()
    {
        // The stub's 8th instruction (call [rip+3000h]) contains RIP-relative addressing.
        // We need enough instructions to reach it. If stolen bytes >= 14, we stop at instruction 3
        // (5+4+5 = 14), so we never reach the RIP-relative one. Let's use instructions that are shorter.
        _disasmEngine.NextInstructions =
        [
            new(0x7FF00100, "90", "nop", "", 1),
            new(0x7FF00101, "90", "nop", "", 1),
            new(0x7FF00102, "90", "nop", "", 1),
            new(0x7FF00103, "90", "nop", "", 1),
            new(0x7FF00104, "90", "nop", "", 1),
            new(0x7FF00105, "90", "nop", "", 1),
            new(0x7FF00106, "90", "nop", "", 1),
            new(0x7FF00107, "90", "nop", "", 1),
            new(0x7FF00108, "90", "nop", "", 1),
            new(0x7FF00109, "FF 15 00 30 00 00", "call", "[rip+3000h]", 6),
        ];

        _facade.WriteMemoryDirect((nuint)0x7FF00100, new byte[20]);

        var sut = BuildSut(codeCave: _codeCaveEngine);
        var result = await sut.DryRunHookInstall(processId: AlivePid, address: "0x7FF00100");

        // 9 nops (9 bytes) + call [rip+3000h] (6 bytes) = 15 bytes >= 14
        Assert.Contains("RIP-Relative Instructions", result, StringComparison.Ordinal);
        Assert.Contains("displacement adjustment", result, StringComparison.Ordinal);
    }

    // ── B: Invalid address — address 0 or very high address ──

    [Fact]
    public async Task DryRunHookInstall_AddressZero_ThrowsOrReturnsError()
    {
        // Address "0" is a valid decimal parse (nuint 0), but the process is PID 1000
        // which the stub says is alive (from ListProcessesAsync it returns PIDs 1000, 2000, 3000).
        // The disassembly stub should still work, and we get a preview for address 0.
        // However, ParseAddress("0") yields nuint 0. The method catches all exceptions.
        _facade.WriteMemoryDirect((nuint)0, new byte[20]);

        var sut = BuildSut(codeCave: _codeCaveEngine);
        var result = await sut.DryRunHookInstall(processId: AlivePid, address: "0");

        // Should not crash — either returns a preview or an error message
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public async Task DryRunHookInstall_NullAddress_ReturnsError()
    {
        var sut = BuildSut(codeCave: _codeCaveEngine);

        // ParseAddress throws on null/whitespace — caught by the outer try-catch
        var result = await sut.DryRunHookInstall(processId: AlivePid, address: "");

        Assert.Contains("failed", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DryRunHookInstall_InvalidHexAddress_ReturnsError()
    {
        var sut = BuildSut(codeCave: _codeCaveEngine);

        var result = await sut.DryRunHookInstall(processId: AlivePid, address: "0xZZZZ");

        Assert.Contains("failed", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── C: Null/missing code cave engine ──

    [Fact]
    public async Task DryRunHookInstall_NullCodeCaveEngine_StillPerformsDryRun()
    {
        // DryRunHookInstall does NOT check codeCaveEngine — it only uses disassemblyService
        // and engineFacade. The code cave engine is only used by InstallCodeCaveHook.
        // So a dry run should still succeed even without the code cave engine.
        _facade.WriteMemoryDirect((nuint)0x7FF00100, new byte[20]);

        var sut = BuildSut(codeCave: null);
        var result = await sut.DryRunHookInstall(processId: AlivePid, address: "0x7FF00100");

        // Should still produce a valid dry-run preview since it only needs disassembly + memory read
        Assert.Contains("Dry-Run Hook Preview", result, StringComparison.Ordinal);
        Assert.Contains("Stolen Bytes", result, StringComparison.Ordinal);
    }

    // ── D: Process not alive ──

    [Fact]
    public async Task DryRunHookInstall_DeadProcess_ReturnsNotRunning()
    {
        // Use a PID that is not in the system (very unlikely to exist)
        var sut = BuildSut(codeCave: _codeCaveEngine);
        var result = await sut.DryRunHookInstall(processId: 99999, address: "0x7FF00100");

        Assert.Contains("no longer running", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DryRunHookInstall_NegativeProcessId_ReturnsNotRunning()
    {
        var sut = BuildSut(codeCave: _codeCaveEngine);
        var result = await sut.DryRunHookInstall(processId: -1, address: "0x7FF00100");

        // Negative PID will cause GetProcessById to throw, which IsProcessAlive catches and returns false
        Assert.Contains("no longer running", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── E: Non-executable memory region ──

    [Fact]
    public async Task DryRunHookInstall_NonExecutableRegion_ReportsCannotHook()
    {
        _facade.WriteMemoryDirect((nuint)0x7FF00100, new byte[20]);

        var memProtection = new InlineStubMemoryProtectionEngine
        {
            NextRegion = new MemoryRegionDescriptor(
                (nuint)0x7FF00000, 4096,
                IsReadable: true, IsWritable: true, IsExecutable: false)
        };

        var sut = BuildSut(codeCave: _codeCaveEngine, memProtection: memProtection);
        var result = await sut.DryRunHookInstall(processId: AlivePid, address: "0x7FF00100");

        Assert.Contains("NOT executable", result, StringComparison.Ordinal);
        Assert.Contains("DO NOT HOOK", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunHookInstall_ExecutableRegion_ReportsExecutable()
    {
        _facade.WriteMemoryDirect((nuint)0x7FF00100, new byte[20]);

        var memProtection = new InlineStubMemoryProtectionEngine
        {
            NextRegion = new MemoryRegionDescriptor(
                (nuint)0x7FF00000, 4096,
                IsReadable: true, IsWritable: false, IsExecutable: true)
        };

        var sut = BuildSut(codeCave: _codeCaveEngine, memProtection: memProtection);
        var result = await sut.DryRunHookInstall(processId: AlivePid, address: "0x7FF00100");

        Assert.Contains("Region is executable", result, StringComparison.Ordinal);
        Assert.Contains("SAFE TO HOOK", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunHookInstall_InsufficientBytes_ReportsCannotHook()
    {
        // Only 1 instruction with 1 byte — not enough for 14-byte JMP
        _disasmEngine.NextInstructions =
        [
            new(0x7FF00100, "C3", "ret", "", 1)
        ];

        _facade.WriteMemoryDirect((nuint)0x7FF00100, new byte[] { 0xC3 });

        var sut = BuildSut(codeCave: _codeCaveEngine);
        var result = await sut.DryRunHookInstall(processId: AlivePid, address: "0x7FF00100");

        Assert.Contains("Insufficient bytes", result, StringComparison.Ordinal);
        Assert.Contains("DO NOT HOOK", result, StringComparison.Ordinal);
    }

    // ── Inline stub for IMemoryProtectionEngine ──

    internal sealed class InlineStubMemoryProtectionEngine : IMemoryProtectionEngine
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
}
