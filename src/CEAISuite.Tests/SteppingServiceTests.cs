using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class SteppingServiceTests
{
    private const int TestPid = 1234;

    private static readonly RegisterSnapshot TestRegisters = new(
        Rax: 0x100, Rbx: 0x200, Rcx: 0x300, Rdx: 0x400,
        Rsi: 0x500, Rdi: 0x600, Rsp: 0x7FFE0000, Rbp: 0x7FFE0010,
        R8: 0x800, R9: 0x900, R10: 0xA00, R11: 0xB00,
        Rip: 0x00401000);

    private static (SteppingService service, StubVehDebugger veh, StubEngineFacade facade) CreateService()
    {
        var veh = new StubVehDebugger();
        var disasm = new StubDisassemblyEngine();
        var facade = new StubEngineFacade();
        var bp = new StubBreakpointEngine();
        var eventBus = new BreakpointEventBus();
        var engine = new Engine.Windows.WindowsSteppingEngine(veh, disasm, facade, bp, eventBus);
        var service = new SteppingService(facade, engine);
        return (service, veh, facade);
    }

    [Fact]
    public void IsAvailable_WithEngine_ReturnsTrue()
    {
        var (svc, _, _) = CreateService();
        Assert.True(svc.IsAvailable);
    }

    [Fact]
    public void IsAvailable_WithoutEngine_ReturnsFalse()
    {
        var facade = new StubEngineFacade();
        var svc = new SteppingService(facade);
        Assert.False(svc.IsAvailable);
    }

    [Fact]
    public async Task StepIn_WithoutAttach_ReturnsError()
    {
        var (svc, _, _) = CreateService();
        // Don't attach to any process

        var result = await svc.StepInAsync(TestPid);

        Assert.False(result.Success);
        Assert.Equal(StoppedReason.Error, result.Reason);
        Assert.Contains("No process attached", result.Error);
    }

    [Fact]
    public async Task StepIn_PidMismatch_ReturnsError()
    {
        var (svc, _, facade) = CreateService();
        await facade.AttachAsync(TestPid); // Attach to 1234

        var result = await svc.StepInAsync(9999); // Request for different PID

        Assert.False(result.Success);
        Assert.Contains("PID mismatch", result.Error);
    }

    [Fact]
    public async Task StepIn_ValidPid_DelegatesToEngine()
    {
        var (svc, veh, facade) = CreateService();
        await facade.AttachAsync(TestPid);
        await veh.InjectAsync(TestPid);
        veh.AddCannedHit(TestPid, new VehHitEvent(
            0x00401001, 42, VehBreakpointType.Trace, 0x4000, TestRegisters, 0));

        var result = await svc.StepInAsync(TestPid);

        Assert.True(result.Success);
        Assert.Equal((nuint)0x00401001, result.NewRip);
    }

    [Fact]
    public void GetState_DefaultsToIdle()
    {
        var (svc, _, _) = CreateService();
        Assert.Equal(SteppingState.Idle, svc.GetState(TestPid));
    }

    [Fact]
    public void GetState_WithoutEngine_ReturnsIdle()
    {
        var facade = new StubEngineFacade();
        var svc = new SteppingService(facade);
        Assert.Equal(SteppingState.Idle, svc.GetState(TestPid));
    }

    [Fact]
    public void FormatStepResult_FailedResult_ContainsError()
    {
        var result = new StepResult(false, 0, null, 0, StoppedReason.Error, Error: "Something went wrong");
        var formatted = SteppingService.FormatStepResult(result);
        Assert.Contains("Something went wrong", formatted);
    }

    [Fact]
    public void FormatStepResult_SuccessResult_ContainsRegisters()
    {
        var result = new StepResult(true, 0x00401000, TestRegisters, 42,
            StoppedReason.StepComplete, Disassembly: "nop");
        var formatted = SteppingService.FormatStepResult(result);

        Assert.Contains("RIP: 0x401000", formatted);
        Assert.Contains("RAX=0x100", formatted);
        Assert.Contains("nop", formatted);
        Assert.Contains("Thread: 42", formatted);
    }
}
