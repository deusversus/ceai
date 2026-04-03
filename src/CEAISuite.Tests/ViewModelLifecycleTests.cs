using System.Runtime.Versioning;
using CEAISuite.Desktop.Services;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for ViewModel lifecycle patterns — ProcessContext event subscription cleanup
/// and the Interlocked.CompareExchange reentrancy guard pattern used in MainViewModel.
/// </summary>
[SupportedOSPlatform("windows")]
public class ViewModelLifecycleTests
{
    // ── ProcessContext event subscription lifecycle ──

    [Fact]
    public void ProcessContext_Subscribe_ReceivesEvents()
    {
        var ctx = new ProcessContext();
        int fireCount = 0;
        ctx.ProcessChanged += () => fireCount++;

        ctx.Attach(new Application.ProcessInspectionOverview(
            123, "test.exe", "x64", [], null, null, null, "Attached"));

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void ProcessContext_Unsubscribe_StopsReceivingEvents()
    {
        var ctx = new ProcessContext();
        int fireCount = 0;
        void Handler() => fireCount++;

        ctx.ProcessChanged += Handler;
        ctx.Attach(new Application.ProcessInspectionOverview(
            1, "a.exe", "x64", [], null, null, null, "ok"));
        Assert.Equal(1, fireCount);

        // Unsubscribe (mirrors OnClosing pattern)
        ctx.ProcessChanged -= Handler;

        ctx.Detach();
        Assert.Equal(1, fireCount); // should NOT have incremented
    }

    [Fact]
    public void ProcessContext_Detach_ClearsInspection()
    {
        var ctx = new ProcessContext();
        ctx.Attach(new Application.ProcessInspectionOverview(
            42, "game.exe", "x64", [], null, null, null, "ok"));
        Assert.NotNull(ctx.CurrentInspection);

        ctx.Detach();
        Assert.Null(ctx.CurrentInspection);
        Assert.Null(ctx.AttachedProcessId);
    }

    // ── Reentrancy guard pattern (Interlocked.CompareExchange) ──
    // Tests the same atomic pattern used in MainViewModel._isAttaching

    [Fact]
    public async Task ReentrancyGuard_PreventsConcurrentExecution()
    {
        int isExecuting = 0;
        int concurrentAttempts = 0;
        int successfulExecutions = 0;
        var gate = new TaskCompletionSource();

        // Simulate the MainViewModel.InspectSelectedProcessAsync pattern
        async Task SimulateAttach()
        {
            if (Interlocked.CompareExchange(ref isExecuting, 1, 0) != 0)
            {
                Interlocked.Increment(ref concurrentAttempts);
                return;
            }

            try
            {
                Interlocked.Increment(ref successfulExecutions);
                await gate.Task; // hold the lock
            }
            finally
            {
                Interlocked.Exchange(ref isExecuting, 0);
            }
        }

        // Start first execution — it will block on the gate
        var first = SimulateAttach();

        // Try concurrent executions — they should all be rejected
        var concurrent = Enumerable.Range(0, 5).Select(_ => SimulateAttach()).ToArray();
        await Task.WhenAll(concurrent);

        Assert.Equal(5, concurrentAttempts);
        Assert.Equal(1, successfulExecutions);

        // Release the gate
        gate.SetResult();
        await first;

        // Now a new execution should succeed
        var second = SimulateAttach();
        gate = new TaskCompletionSource();
        // Let it complete immediately since gate is already set
        Assert.Equal(2, successfulExecutions);
    }

    [Fact]
    public async Task ReentrancyGuard_ReleasesOnException()
    {
        int isExecuting = 0;

        async Task SimulateAttachWithError()
        {
            if (Interlocked.CompareExchange(ref isExecuting, 1, 0) != 0)
                return;

            try
            {
                await Task.Yield();
                throw new InvalidOperationException("simulated error");
            }
            finally
            {
                Interlocked.Exchange(ref isExecuting, 0);
            }
        }

        // First call throws
        await Assert.ThrowsAsync<InvalidOperationException>(SimulateAttachWithError);

        // Guard should be released — second call should execute (and also throw)
        Assert.Equal(0, isExecuting);
        await Assert.ThrowsAsync<InvalidOperationException>(SimulateAttachWithError);
    }

    [Fact]
    public void ProcessContext_MultipleSubscribers_AllNotified()
    {
        var ctx = new ProcessContext();
        int count1 = 0, count2 = 0;
        ctx.ProcessChanged += () => count1++;
        ctx.ProcessChanged += () => count2++;

        ctx.Attach(new Application.ProcessInspectionOverview(
            1, "test.exe", "x64", [], null, null, null, "ok"));

        Assert.Equal(1, count1);
        Assert.Equal(1, count2);
    }

    [Fact]
    public void ProcessContext_UnsubscribeOne_OtherStillFires()
    {
        var ctx = new ProcessContext();
        int count1 = 0, count2 = 0;
        void Handler1() => count1++;
        void Handler2() => count2++;

        ctx.ProcessChanged += Handler1;
        ctx.ProcessChanged += Handler2;

        ctx.ProcessChanged -= Handler1;

        ctx.Attach(new Application.ProcessInspectionOverview(
            1, "test.exe", "x64", [], null, null, null, "ok"));

        Assert.Equal(0, count1); // unsubscribed
        Assert.Equal(1, count2); // still subscribed
    }
}
