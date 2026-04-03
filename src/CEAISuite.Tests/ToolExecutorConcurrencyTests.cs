using System.Threading.Channels;
using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
using Microsoft.Extensions.AI;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for ToolExecutor parallel batch execution — concurrency throttling,
/// failure isolation, cancellation propagation, and correct result ordering.
/// </summary>
public class ToolExecutorConcurrencyTests
{
    private static ToolExecutor CreateExecutor(
        List<AITool> tools,
        ToolAttributeCache? cache = null,
        IReadOnlySet<string>? dangerousTools = null)
    {
        cache ??= new ToolAttributeCache();
        var options = new AgentLoopOptions
        {
            SystemPrompt = "test",
            Tools = tools,
            Limits = new TokenLimits(),
            ToolResultStore = new ToolResultStore(),
            DangerousToolNames = dangerousTools ?? new HashSet<string>(),
        };
        return new ToolExecutor(options, cache);
    }

    private static FunctionCallContent MakeCall(string name, string? callId = null)
        => new(callId ?? Guid.NewGuid().ToString("N"), name);

    private static ChannelWriter<AgentStreamEvent> CreateChannel()
        => Channel.CreateUnbounded<AgentStreamEvent>().Writer;

    /// <summary>
    /// Creates an AIFunction from a delegate with the given name, marked as concurrency-safe
    /// in the attribute cache.
    /// </summary>
    private static (AIFunction Fn, ToolAttributeCache Cache) CreateConcurrentTool(
        string name,
        Func<Task<string>> handler,
        ToolAttributeCache? existingCache = null)
    {
        var fn = AIFunctionFactory.Create(handler, name);
        var cache = existingCache ?? new ToolAttributeCache();
        // Manually register metadata marking this tool as concurrency-safe
        RegisterConcurrencySafe(cache, name);
        return (fn, cache);
    }

    private static void RegisterConcurrencySafe(ToolAttributeCache cache, string name)
    {
        // Use reflection to set the private _cache dictionary directly,
        // since we can't use attributes on dynamic functions
        var field = typeof(ToolAttributeCache).GetField("_cache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = (Dictionary<string, ToolMetadata>)field.GetValue(cache)!;
        dict[name] = new ToolMetadata
        {
            Name = name,
            IsConcurrencySafe = true,
            IsReadOnly = false,
            IsDestructive = false,
        };
    }

    [Fact]
    public async Task ParallelBatch_AllResults_ReturnedInOrder()
    {
        var cache = new ToolAttributeCache();
        var tools = new List<AITool>();
        var names = new[] { "tool_a", "tool_b", "tool_c" };

        foreach (var name in names)
        {
            var localName = name;
            var fn = AIFunctionFactory.Create(() => Task.FromResult($"result_{localName}"), localName);
            tools.Add(fn);
            RegisterConcurrencySafe(cache, localName);
        }

        var executor = CreateExecutor(tools, cache);
        var calls = names.Select(n => MakeCall(n)).ToList();
        var results = await executor.ExecuteAsync(calls, CreateChannel(), CancellationToken.None);

        Assert.Equal(3, results.Count);
        for (int i = 0; i < names.Length; i++)
        {
            Assert.Equal(names[i], results[i].Call.Name);
            Assert.Contains($"result_{names[i]}", results[i].Result.Result?.ToString());
        }
    }

    [Fact]
    public async Task ParallelBatch_SemaphoreThrottles_Concurrency()
    {
        // Verify that at most MaxParallelTools (10) tasks run simultaneously
        var cache = new ToolAttributeCache();
        var tools = new List<AITool>();
        int peakConcurrency = 0;
        int currentConcurrency = 0;
        var gate = new SemaphoreSlim(0, 1); // will be released immediately
        var toolCount = 15; // more than MaxParallelTools

        for (int i = 0; i < toolCount; i++)
        {
            var name = $"concurrent_{i}";
            var fn = AIFunctionFactory.Create(async () =>
            {
                var c = Interlocked.Increment(ref currentConcurrency);
                // Track peak
                int oldPeak;
                do { oldPeak = peakConcurrency; }
                while (c > oldPeak && Interlocked.CompareExchange(ref peakConcurrency, c, oldPeak) != oldPeak);

                await Task.Delay(50); // simulate work
                Interlocked.Decrement(ref currentConcurrency);
                return "ok";
            }, name);
            tools.Add(fn);
            RegisterConcurrencySafe(cache, name);
        }

        var executor = CreateExecutor(tools, cache);
        var calls = Enumerable.Range(0, toolCount).Select(i => MakeCall($"concurrent_{i}")).ToList();
        var results = await executor.ExecuteAsync(calls, CreateChannel(), CancellationToken.None);

        Assert.Equal(toolCount, results.Count);
        Assert.True(peakConcurrency <= ToolExecutor.MaxParallelTools,
            $"Peak concurrency {peakConcurrency} exceeded MaxParallelTools {ToolExecutor.MaxParallelTools}");
        // Verify some parallelism actually occurred (peak > 1 with 15 tools)
        Assert.True(peakConcurrency > 1, $"Expected parallel execution but peak was {peakConcurrency}");
    }

    [Fact]
    public async Task ParallelBatch_OneFailure_DoesNotBlockSiblings()
    {
        var cache = new ToolAttributeCache();
        var tools = new List<AITool>();

        // Tool that succeeds
        var goodTool = AIFunctionFactory.Create(() => Task.FromResult("success"), "good_tool");
        tools.Add(goodTool);
        RegisterConcurrencySafe(cache, "good_tool");

        // Tool that throws
        var badTool = AIFunctionFactory.Create(new Func<Task<string>>(() =>
            throw new InvalidOperationException("deliberate failure")), "bad_tool");
        tools.Add(badTool);
        RegisterConcurrencySafe(cache, "bad_tool");

        // Another tool that succeeds
        var anotherGood = AIFunctionFactory.Create(() => Task.FromResult("also success"), "another_good");
        tools.Add(anotherGood);
        RegisterConcurrencySafe(cache, "another_good");

        var executor = CreateExecutor(tools, cache);
        var calls = new List<FunctionCallContent>
        {
            MakeCall("good_tool"),
            MakeCall("bad_tool"),
            MakeCall("another_good"),
        };

        var results = await executor.ExecuteAsync(calls, CreateChannel(), CancellationToken.None);

        // All 3 results should be present
        Assert.Equal(3, results.Count);

        // good_tool succeeded
        Assert.False(results[0].IsError);
        Assert.Contains("success", results[0].Result.Result?.ToString());

        // bad_tool has error
        Assert.True(results[1].IsError);
        Assert.Contains("deliberate failure", results[1].Result.Result?.ToString());

        // another_good succeeded despite bad_tool failure
        Assert.False(results[2].IsError);
        Assert.Contains("also success", results[2].Result.Result?.ToString());
    }

    [Fact]
    public async Task CancellationToken_CancelsInFlightTools()
    {
        var cache = new ToolAttributeCache();
        var tools = new List<AITool>();
        var toolStarted = new TaskCompletionSource();

        var slowTool = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            toolStarted.SetResult();
            // Wait indefinitely until cancelled
            await Task.Delay(Timeout.Infinite, ct);
            return "should not reach";
        }, "slow_tool");
        tools.Add(slowTool);
        RegisterConcurrencySafe(cache, "slow_tool");

        var executor = CreateExecutor(tools, cache);
        using var cts = new CancellationTokenSource();

        var calls = new List<FunctionCallContent> { MakeCall("slow_tool") };
        var executeTask = executor.ExecuteAsync(calls, CreateChannel(), cts.Token);

        // Wait for tool to start, then cancel
        await toolStarted.Task;
        cts.Cancel();

        // Cancellation should propagate — either as TaskCanceledException or OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executeTask);
    }

    [Fact]
    public async Task SerialTool_FlushesParallelBatch()
    {
        // Verify that a non-concurrent-safe tool causes the parallel batch to flush first
        var cache = new ToolAttributeCache();
        var tools = new List<AITool>();
        var executionOrder = new List<string>();
        var orderLock = new object();

        // Two concurrent-safe tools
        for (int i = 0; i < 2; i++)
        {
            var name = $"parallel_{i}";
            var localName = name;
            var fn = AIFunctionFactory.Create(async () =>
            {
                await Task.Delay(10);
                lock (orderLock) executionOrder.Add(localName);
                return "ok";
            }, localName);
            tools.Add(fn);
            RegisterConcurrencySafe(cache, localName);
        }

        // One serial tool (not in cache → defaults to non-concurrent-safe)
        var serialTool = AIFunctionFactory.Create(() =>
        {
            lock (orderLock) executionOrder.Add("serial");
            return Task.FromResult("serial_ok");
        }, "serial_tool");
        tools.Add(serialTool);
        // Don't register as concurrent-safe — it will run serially

        var executor = CreateExecutor(tools, cache);
        var calls = new List<FunctionCallContent>
        {
            MakeCall("parallel_0"),
            MakeCall("parallel_1"),
            MakeCall("serial_tool"),
        };

        var results = await executor.ExecuteAsync(calls, CreateChannel(), CancellationToken.None);

        Assert.Equal(3, results.Count);
        // Serial tool must run after both parallel tools
        var serialIdx = executionOrder.IndexOf("serial");
        Assert.Equal(2, serialIdx); // should be last
    }
}
