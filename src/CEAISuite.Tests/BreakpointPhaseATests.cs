using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Windows;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Phase A: Breakpoint engine hardening tests.
/// Covers condition evaluation (A2), Lua callback isolation (A6),
/// throttle cooldown state (A4), and lifecycle status tracking.
/// </summary>
public class BreakpointPhaseATests
{
    // ── A2: VehConditionEvaluator.EvaluateFromDictionary ──

    [Fact]
    public void EvaluateFromDictionary_RegisterCompare_EqualMatch_ReturnsTrue()
    {
        var cond = new BreakpointCondition("RAX == 0x1000", BreakpointConditionType.RegisterCompare);
        var regs = new Dictionary<string, string> { ["RAX"] = "0x1000" };

        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0));
    }

    [Fact]
    public void EvaluateFromDictionary_RegisterCompare_NotEqual_ReturnsFalse()
    {
        var cond = new BreakpointCondition("RAX == 0x1000", BreakpointConditionType.RegisterCompare);
        var regs = new Dictionary<string, string> { ["RAX"] = "0x2000" };

        Assert.False(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0));
    }

    [Fact]
    public void EvaluateFromDictionary_RegisterCompare_GreaterThan()
    {
        var cond = new BreakpointCondition("RCX > 100", BreakpointConditionType.RegisterCompare);
        var regs = new Dictionary<string, string> { ["RCX"] = "0xC8" }; // 200

        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0));
    }

    [Fact]
    public void EvaluateFromDictionary_RegisterCompare_LessThanOrEqual()
    {
        var cond = new BreakpointCondition("RDX <= 50", BreakpointConditionType.RegisterCompare);
        var regs = new Dictionary<string, string> { ["RDX"] = "0x32" }; // 50

        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0));
    }

    [Fact]
    public void EvaluateFromDictionary_RegisterCompare_32BitAlias()
    {
        // EAX alias should work via dictionary lookup (stored as "EAX" in snapshot)
        var cond = new BreakpointCondition("EAX == 0xFF", BreakpointConditionType.RegisterCompare);
        var regs = new Dictionary<string, string> { ["EAX"] = "0xFF" };

        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0));
    }

    [Fact]
    public void EvaluateFromDictionary_RegisterCompare_UnknownRegister_PassesThrough()
    {
        var cond = new BreakpointCondition("XMM0 == 0x1", BreakpointConditionType.RegisterCompare);
        var regs = new Dictionary<string, string> { ["RAX"] = "0x0" };

        // Unknown register → pass through (fail-open)
        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0));
    }

    [Fact]
    public void EvaluateFromDictionary_MalformedExpression_PassesThrough()
    {
        var cond = new BreakpointCondition("garbage!@#$%", BreakpointConditionType.RegisterCompare);
        var regs = new Dictionary<string, string> { ["RAX"] = "0x0" };

        // Fail-open: unparseable expression passes through
        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0));
    }

    [Fact]
    public void EvaluateFromDictionary_HitCount_ThresholdReached()
    {
        var cond = new BreakpointCondition(">= 5", BreakpointConditionType.HitCount);
        var regs = new Dictionary<string, string>();

        Assert.False(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 3));
        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 5));
        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 10));
    }

    [Fact]
    public void EvaluateFromDictionary_HitCount_EveryNthHit()
    {
        var cond = new BreakpointCondition("% 10", BreakpointConditionType.HitCount);
        var regs = new Dictionary<string, string>();

        Assert.False(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 7));
        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 10));
        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 20));
    }

    [Fact]
    public void EvaluateFromDictionary_MemoryCompare_WithNullReader_PassesThrough()
    {
        var cond = new BreakpointCondition("0x1000 == 0x42", BreakpointConditionType.MemoryCompare);
        var regs = new Dictionary<string, string>();

        // No memory reader available → fail-open
        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0, null));
    }

    [Fact]
    public void EvaluateFromDictionary_MemoryCompare_WithReader_Matches()
    {
        var cond = new BreakpointCondition("0x1000 == 0x42", BreakpointConditionType.MemoryCompare);
        var regs = new Dictionary<string, string>();
        byte[]? ReadMem(nuint addr, int size) => addr == 0x1000 ? new byte[] { 0x42, 0, 0, 0, 0, 0, 0, 0 } : null;

        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0, ReadMem));
    }

    [Fact]
    public void EvaluateFromDictionary_MemoryCompare_WithReader_NoMatch()
    {
        var cond = new BreakpointCondition("0x1000 == 0x42", BreakpointConditionType.MemoryCompare);
        var regs = new Dictionary<string, string>();
        byte[]? ReadMem(nuint addr, int size) => new byte[] { 0xFF, 0, 0, 0, 0, 0, 0, 0 };

        Assert.False(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0, ReadMem));
    }

    [Fact]
    public void EvaluateFromDictionary_UnknownConditionType_PassesThrough()
    {
        var cond = new BreakpointCondition("anything", (BreakpointConditionType)999);
        var regs = new Dictionary<string, string>();

        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0));
    }

    [Fact]
    public void EvaluateFromDictionary_EmptyRegisters_PassesThrough()
    {
        var cond = new BreakpointCondition("RAX == 0x100", BreakpointConditionType.RegisterCompare);
        var regs = new Dictionary<string, string>(); // empty

        // Unknown register → pass through
        Assert.True(VehConditionEvaluator.EvaluateFromDictionary(cond, regs, 0));
    }

    // ── A6: Lua callback exception isolation ──

    [Fact]
    public async Task LuaCallback_ThrowingCallback_DoesNotKillSubsequentHits()
    {
        var throwCount = 0;
        var luaEngine = new ThrowingLuaEngine(() =>
        {
            throwCount++;
            throw new InvalidOperationException("Lua error");
        });
        var engine = new StubBreakpointEngine();
        var service = new BreakpointService(engine, luaEngine);

        // Set up a breakpoint with hits
        var bp = await engine.SetBreakpointAsync(1234, (nuint)0x1000, BreakpointType.Software);
        engine.AddCannedHits(bp.Id,
            new BreakpointHitEvent(bp.Id, (nuint)0x1000, 1, DateTimeOffset.UtcNow, new Dictionary<string, string>()),
            new BreakpointHitEvent(bp.Id, (nuint)0x1000, 1, DateTimeOffset.UtcNow, new Dictionary<string, string>()));

        service.RegisterLuaCallback(bp.Id, "onHit");

        // GetHitLog invokes callbacks — should not throw despite Lua errors
        var hits = await service.GetHitLogAsync(bp.Id);

        Assert.Equal(2, hits.Count);
        Assert.Equal(2, throwCount); // callback attempted for each hit despite throwing
    }

    [Fact]
    public async Task LuaCallback_AutoUnregistersAfterMaxConsecutiveFailures()
    {
        var throwCount = 0;
        var luaEngine = new ThrowingLuaEngine(() =>
        {
            throwCount++;
            throw new InvalidOperationException("Lua error");
        });
        var engine = new StubBreakpointEngine();
        var service = new BreakpointService(engine, luaEngine);

        var bp = await engine.SetBreakpointAsync(1234, (nuint)0x1000, BreakpointType.Software);

        // Add enough hits to trigger 3+ failures
        var hits = Enumerable.Range(0, 5)
            .Select(_ => new BreakpointHitEvent(bp.Id, (nuint)0x1000, 1, DateTimeOffset.UtcNow, new Dictionary<string, string>()))
            .ToArray();
        engine.AddCannedHits(bp.Id, hits);

        service.RegisterLuaCallback(bp.Id, "onHit");

        await service.GetHitLogAsync(bp.Id);

        // After 3 consecutive failures, callback should be auto-unregistered
        // Subsequent GetHitLog should not invoke the callback
        throwCount = 0;
        engine.AddCannedHits(bp.Id, hits); // reset hit log
        await service.GetHitLogAsync(bp.Id);

        // throwCount should be 0 because callback was unregistered
        Assert.Equal(0, throwCount);
    }

    // ── A4: Throttle cooldown state ──

    [Fact]
    public void BreakpointLifecycleStatus_ThrottleDisabled_Exists()
    {
        Assert.Equal(3, (int)BreakpointLifecycleStatus.ThrottleDisabled);
    }

    [Fact]
    public void LifecycleStatus_TrackingWorks()
    {
        var service = new BreakpointService(new StubBreakpointEngine());

        service.UpdateLifecycleStatus("bp-1", BreakpointLifecycleStatus.Active);
        Assert.Equal(BreakpointLifecycleStatus.Active, service.GetLifecycleStatus("bp-1"));

        service.UpdateLifecycleStatus("bp-1", BreakpointLifecycleStatus.ThrottleDisabled);
        Assert.Equal(BreakpointLifecycleStatus.ThrottleDisabled, service.GetLifecycleStatus("bp-1"));
    }

    [Fact]
    public void LifecycleStatus_ConcurrentUpdates_NoCorruption()
    {
        using var service = new BreakpointService(new StubBreakpointEngine());
        var statuses = Enum.GetValues<BreakpointLifecycleStatus>();

        // Concurrent updates to different BPs
        Parallel.For(0, 100, i =>
        {
            var bpId = $"bp-{i}";
            var status = statuses[i % statuses.Length];
            service.UpdateLifecycleStatus(bpId, status);
            var read = service.GetLifecycleStatus(bpId);
            // Value should be one of the valid statuses (no corruption)
            Assert.True(Enum.IsDefined(read), $"Got invalid lifecycle status: {read}");
        });
    }

    [Fact]
    public void LifecycleStatus_UnknownBp_ReturnsArmed()
    {
        var service = new BreakpointService(new StubBreakpointEngine());
        Assert.Equal(BreakpointLifecycleStatus.Armed, service.GetLifecycleStatus("nonexistent"));
    }

    // ── Concurrent operations (A7) ──

    [Fact]
    public async Task ConcurrentListBreakpoints_DoesNotThrow()
    {
        var engine = new StubBreakpointEngine();
        var service = new BreakpointService(engine);

        // Set up breakpoints sequentially first
        for (int i = 0; i < 10; i++)
            await service.SetBreakpointAsync(1234, $"0x{0x1000 + i * 4:X}", BreakpointType.HardwareExecute);

        // Concurrent list calls — should not throw
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => service.ListBreakpointsAsync(1234)))
            .ToArray();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(results, list => Assert.True(list.Count > 0));
    }

    // ── Helpers ──

    /// <summary>Lua engine stub that throws on callback invocation for testing isolation.</summary>
    private sealed class ThrowingLuaEngine : ILuaScriptEngine
    {
        private readonly Action _throwAction;

        public ThrowingLuaEngine(Action throwAction) => _throwAction = throwAction;

        public void RegisterBreakpointCallback(string functionName) { }

        public Task<LuaExecutionResult> InvokeBreakpointCallbackAsync(string functionName, BreakpointHitEvent hitEvent,
            CancellationToken ct = default)
        {
            _throwAction();
            return Task.FromResult(new LuaExecutionResult(true, null, null, []));
        }

        // ── Other ILuaScriptEngine members — not used in these tests ──
        private static readonly LuaExecutionResult SuccessResult = new(true, null, null, []);
        private static readonly LuaValidationResult ValidResult = new(true, []);

        public Task<LuaExecutionResult> ExecuteAsync(string luaCode, CancellationToken ct = default) => Task.FromResult(SuccessResult);
        public Task<LuaExecutionResult> ExecuteAsync(string luaCode, int processId, CancellationToken ct = default) => Task.FromResult(SuccessResult);
        public Task<LuaExecutionResult> EvaluateAsync(string expression, CancellationToken ct = default) => Task.FromResult(SuccessResult);
        public LuaValidationResult Validate(string luaCode) => ValidResult;
        public void SetGlobal(string name, object? value) { }
        public object? GetGlobal(string name) => null;
        public void Reset() { }
        public Task SetGlobalAsync(string name, object? value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<object?> GetGlobalAsync(string name, CancellationToken ct = default) => Task.FromResult<object?>(null);
        public Task ResetAsync(CancellationToken ct = default) => Task.CompletedTask;
#pragma warning disable CS0067 // Event never used — required by interface
        public event Action<string>? OutputWritten;
#pragma warning restore CS0067
    }
}
