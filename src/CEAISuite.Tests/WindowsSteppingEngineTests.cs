using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class WindowsSteppingEngineTests
{
    private const int TestPid = 9999;
    private const int TestThreadId = 42;

    private static readonly RegisterSnapshot TestRegisters = new(
        Rax: 0x100, Rbx: 0x200, Rcx: 0x300, Rdx: 0x400,
        Rsi: 0x500, Rdi: 0x600, Rsp: 0x7FFE0000, Rbp: 0x7FFE0010,
        R8: 0x800, R9: 0x900, R10: 0xA00, R11: 0xB00,
        Rip: 0x00401000);

    private static (WindowsSteppingEngine engine, StubVehDebugger veh, StubDisassemblyEngine disasm,
        StubEngineFacade facade, StubBreakpointEngine bp, BreakpointEventBus eventBus) CreateEngine()
    {
        var veh = new StubVehDebugger();
        var disasm = new StubDisassemblyEngine();
        var facade = new StubEngineFacade();
        var bp = new StubBreakpointEngine();
        var eventBus = new BreakpointEventBus();

        var engine = new WindowsSteppingEngine(veh, disasm, facade, bp, eventBus);
        return (engine, veh, disasm, facade, bp, eventBus);
    }

    // ─── StepIn Tests ──────────────────────────────────────────────────

    [Fact]
    public async Task StepIn_WithInjectedAgent_ReturnsSingleTraceEntry()
    {
        var (engine, veh, _, _, _, _) = CreateEngine();
        await veh.InjectAsync(TestPid);

        // Add a canned trace hit
        veh.AddCannedHit(TestPid, new VehHitEvent(
            0x00401001, TestThreadId, VehBreakpointType.Trace, 0x4000, TestRegisters, 0));

        var result = await engine.StepInAsync(TestPid, TestThreadId);

        Assert.True(result.Success);
        Assert.Equal((nuint)0x00401001, result.NewRip);
        Assert.Equal(TestThreadId, result.ThreadId);
        Assert.Equal(StoppedReason.StepComplete, result.Reason);
        Assert.NotNull(result.Registers);
    }

    [Fact]
    public async Task StepIn_AutoInjectsAgent_WhenNotInjected()
    {
        var (engine, veh, _, _, _, _) = CreateEngine();

        // Don't inject agent — StepIn should auto-inject
        // But the stub won't have pending trace hits so it will report "no entries"
        var result = await engine.StepInAsync(TestPid, TestThreadId);

        // Auto-inject should have been attempted
        var status = veh.GetStatus(TestPid);
        Assert.True(status.IsInjected);
    }

    [Fact]
    public async Task StepIn_NoTraceEntries_ReturnsTimeout()
    {
        var (engine, veh, _, _, _, _) = CreateEngine();
        await veh.InjectAsync(TestPid);
        // Don't add any trace hits

        var result = await engine.StepInAsync(TestPid, TestThreadId);

        Assert.False(result.Success);
        Assert.Equal(StoppedReason.Timeout, result.Reason);
        Assert.Contains("No trace entries", result.Error);
    }

    [Fact]
    public async Task StepIn_SetsStateSuspendedOnSuccess()
    {
        var (engine, veh, _, _, _, _) = CreateEngine();
        await veh.InjectAsync(TestPid);
        veh.AddCannedHit(TestPid, new VehHitEvent(
            0x00401001, TestThreadId, VehBreakpointType.Trace, 0x4000, TestRegisters, 0));

        await engine.StepInAsync(TestPid, TestThreadId);

        Assert.Equal(SteppingState.Suspended, engine.GetState(TestPid));
    }

    [Fact]
    public async Task StepIn_PublishesStepCompletedEvent()
    {
        var (engine, veh, _, _, _, eventBus) = CreateEngine();
        await veh.InjectAsync(TestPid);
        veh.AddCannedHit(TestPid, new VehHitEvent(
            0x00401001, TestThreadId, VehBreakpointType.Trace, 0x4000, TestRegisters, 0));

        StepCompletedEvent? received = null;
        using var sub = eventBus.Subscribe(evt =>
        {
            if (evt is StepCompletedEvent sce)
                received = sce;
        });

        await engine.StepInAsync(TestPid, TestThreadId);

        Assert.NotNull(received);
        Assert.Equal((nuint)0x00401001, received!.NewRip);
        Assert.Equal(StoppedReason.StepComplete, received.Reason);
    }

    // ─── StepOver Tests ────────────────────────────────────────────────

    [Fact]
    public async Task StepOver_FallsBackToStepIn()
    {
        // MVP: StepOver behaves as StepIn
        var (engine, veh, _, _, _, _) = CreateEngine();
        await veh.InjectAsync(TestPid);
        veh.AddCannedHit(TestPid, new VehHitEvent(
            0x00401001, TestThreadId, VehBreakpointType.Trace, 0x4000, TestRegisters, 0));

        var result = await engine.StepOverAsync(TestPid, TestThreadId);

        Assert.True(result.Success);
        Assert.Equal(StoppedReason.StepComplete, result.Reason);
    }

    // ─── StepOut Tests ─────────────────────────────────────────────────

    [Fact]
    public async Task StepOut_ReadsReturnAddressFromStack()
    {
        var (engine, veh, _, facade, _, _) = CreateEngine();
        await veh.InjectAsync(TestPid);

        // First step-in to get RSP
        var regsWithRsp = new RegisterSnapshot(
            Rax: 0x100, Rbx: 0x200, Rcx: 0x300, Rdx: 0x400,
            Rsi: 0x500, Rdi: 0x600, Rsp: 0x7FFE0000, Rbp: 0x7FFE0010,
            R8: 0x800, R9: 0x900, R10: 0xA00, R11: 0xB00,
            Rip: 0x00401010);
        veh.AddCannedHit(TestPid, new VehHitEvent(
            0x00401010, TestThreadId, VehBreakpointType.Trace, 0x4000, regsWithRsp, 0));

        // The return address at [RSP] = 0x00402000
        var returnAddr = BitConverter.GetBytes((ulong)0x00402000);
        facade.WriteMemoryDirect(0x7FFE0000, returnAddr);

        // Add a hit at the return address
        veh.AddCannedHit(TestPid, new VehHitEvent(
            0x00402000, TestThreadId, VehBreakpointType.Execute, 0, regsWithRsp, 0));

        var result = await engine.StepOutAsync(TestPid, TestThreadId, timeoutMs: 5000);

        // Should succeed (hit at the return address)
        Assert.True(result.Success);
        Assert.Equal((nuint)0x00402000, result.NewRip);
    }

    // ─── Continue Tests ────────────────────────────────────────────────

    [Fact]
    public async Task Continue_WaitsForBreakpointHit()
    {
        var (engine, veh, _, _, _, _) = CreateEngine();
        await veh.InjectAsync(TestPid);
        veh.AddCannedHit(TestPid, new VehHitEvent(
            0x00401050, TestThreadId, VehBreakpointType.Execute, 0x1, TestRegisters, 0));

        var result = await engine.ContinueAsync(TestPid);

        Assert.True(result.Success);
        Assert.Equal(StoppedReason.BreakpointHit, result.Reason);
        Assert.Equal((nuint)0x00401050, result.NewRip);
    }

    [Fact]
    public async Task Continue_SkipsTraceEntries()
    {
        var (engine, veh, _, _, _, _) = CreateEngine();
        await veh.InjectAsync(TestPid);

        // Add a trace hit (should be skipped) and then an execute hit
        veh.AddCannedHit(TestPid, new VehHitEvent(
            0x00401001, TestThreadId, VehBreakpointType.Trace, 0x4000, TestRegisters, 0));
        veh.AddCannedHit(TestPid, new VehHitEvent(
            0x00401050, TestThreadId, VehBreakpointType.Execute, 0x1, TestRegisters, 0));

        var result = await engine.ContinueAsync(TestPid);

        Assert.True(result.Success);
        Assert.Equal((nuint)0x00401050, result.NewRip); // Skipped the trace entry
    }

    [Fact]
    public async Task Continue_SetsStateToRunningThenSuspended()
    {
        var (engine, veh, _, _, _, _) = CreateEngine();
        await veh.InjectAsync(TestPid);
        veh.AddCannedHit(TestPid, new VehHitEvent(
            0x00401050, TestThreadId, VehBreakpointType.Execute, 0x1, TestRegisters, 0));

        await engine.ContinueAsync(TestPid);

        Assert.Equal(SteppingState.Suspended, engine.GetState(TestPid));
    }

    // ─── State Tests ───────────────────────────────────────────────────

    [Fact]
    public void GetState_DefaultsToIdle()
    {
        var (engine, _, _, _, _, _) = CreateEngine();
        Assert.Equal(SteppingState.Idle, engine.GetState(TestPid));
    }

    [Fact]
    public async Task GetState_ErrorResetsToIdle()
    {
        var (engine, veh, _, _, _, _) = CreateEngine();
        // Don't inject agent — VEH trace will fail
        // But auto-inject will succeed, so add no hits to trigger timeout
        var result = await engine.StepInAsync(TestPid, TestThreadId);

        Assert.Equal(SteppingState.Idle, engine.GetState(TestPid));
    }

    [Fact]
    public async Task GetCurrentState_ReturnsNullWhenNotSuspended()
    {
        var (engine, _, _, _, _, _) = CreateEngine();
        var state = await engine.GetCurrentStateAsync(TestPid);
        Assert.Null(state);
    }
}
