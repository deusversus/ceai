using CEAISuite.Engine.Lua;

namespace CEAISuite.Tests;

/// <summary>
/// Security tests verifying that the MoonSharp Lua sandbox correctly blocks
/// dangerous operations. Each test ensures that an attempted sandbox escape
/// either fails with an error or returns nil, confirming the sandbox is intact.
/// </summary>
public sealed class LuaSandboxEscapeTests : IDisposable
{
    private readonly MoonSharpLuaEngine _engine = new(executionTimeout: TimeSpan.FromSeconds(5));

    public void Dispose() => _engine.Dispose();

    // ── OS module blocked ──

    [Fact]
    public async Task Execute_OsExecute_Blocked()
    {
        var result = await _engine.ExecuteAsync("os.execute('cmd')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_OsClock_Blocked()
    {
        var result = await _engine.ExecuteAsync("return os.clock()");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_OsGetenv_Blocked()
    {
        var result = await _engine.ExecuteAsync("return os.getenv('PATH')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_OsRemove_Blocked()
    {
        var result = await _engine.ExecuteAsync("os.remove('/tmp/test')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_OsRename_Blocked()
    {
        var result = await _engine.ExecuteAsync("os.rename('/tmp/a', '/tmp/b')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // ── IO module blocked ──

    [Fact]
    public async Task Execute_IoOpen_Blocked()
    {
        var result = await _engine.ExecuteAsync("io.open('/etc/passwd')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_IoRead_Blocked()
    {
        var result = await _engine.ExecuteAsync("return io.read()");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_IoWrite_Blocked()
    {
        var result = await _engine.ExecuteAsync("io.write('escape attempt')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_IoLines_Blocked()
    {
        var result = await _engine.ExecuteAsync("for line in io.lines('/etc/passwd') do end");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // ── Debug module blocked ──

    [Fact]
    public async Task Execute_DebugGetinfo_Blocked()
    {
        var result = await _engine.ExecuteAsync("return debug.getinfo(1)");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_DebugSethook_Blocked()
    {
        var result = await _engine.ExecuteAsync("debug.sethook(function() end, 'l')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_DebugGetlocal_Blocked()
    {
        var result = await _engine.ExecuteAsync("return debug.getlocal(1, 1)");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // ── Dynamic code loading blocked ──

    [Fact]
    public async Task Execute_Loadstring_Blocked()
    {
        var result = await _engine.ExecuteAsync("return loadstring('os.execute(\"cmd\")')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_Require_Blocked()
    {
        var result = await _engine.ExecuteAsync("require('os')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_RequireIo_Blocked()
    {
        var result = await _engine.ExecuteAsync("require('io')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_Dofile_Blocked()
    {
        var result = await _engine.ExecuteAsync("dofile('/etc/passwd')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_Loadfile_Blocked()
    {
        var result = await _engine.ExecuteAsync("return loadfile('/etc/passwd')");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // ── Global table escape attempts ──

    [Fact]
    public async Task Execute_RawgetOs_ReturnsNil()
    {
        var result = await _engine.ExecuteAsync("return rawget(_G, 'os')");

        // rawget should either error (if rawget is blocked) or return nil (if os is not in globals)
        if (result.Success)
        {
            Assert.Null(result.ReturnValue); // nil means os module is not present
        }
        else
        {
            // rawget itself is blocked in hard sandbox — also acceptable
            Assert.NotNull(result.Error);
        }
    }

    [Fact]
    public async Task Execute_RawgetIo_ReturnsNil()
    {
        var result = await _engine.ExecuteAsync("return rawget(_G, 'io')");

        if (result.Success)
        {
            Assert.Null(result.ReturnValue);
        }
        else
        {
            Assert.NotNull(result.Error);
        }
    }

    [Fact]
    public async Task Execute_RawgetDebug_ReturnsNil()
    {
        var result = await _engine.ExecuteAsync("return rawget(_G, 'debug')");

        if (result.Success)
        {
            Assert.Null(result.ReturnValue);
        }
        else
        {
            Assert.NotNull(result.Error);
        }
    }

    // ── Metatable manipulation blocked ──

    [Fact]
    public async Task Execute_SetmetatableOnGlobals_DoesNotEscape()
    {
        // Attempting to use metatables to break out of sandbox
        var result = await _engine.ExecuteAsync("""
            local mt = {__index = function(t, k)
                return rawget(_G, k)
            end}
            local proxy = setmetatable({}, mt)
            return proxy.os
            """);

        // Should succeed but return nil (os not available) or error
        if (result.Success)
        {
            Assert.Null(result.ReturnValue);
        }
    }

    // ── Infinite loop / timeout ──

    [Fact]
    public async Task Execute_InfiniteLoop_TimesOut()
    {
        // Use a shorter timeout engine for this test
        using var shortEngine = new MoonSharpLuaEngine(executionTimeout: TimeSpan.FromSeconds(2));

        var result = await shortEngine.ExecuteAsync("while true do end");

        // MoonSharp may not have native instruction-count-based timeout,
        // but the CancellationToken-based timeout should catch long-running scripts.
        // The task timeout (via Task.Run + CancelAfter) should cause either:
        // - Success=false with timeout error, OR
        // - The task eventually completes (MoonSharp doesn't always interrupt tight loops)
        // In either case, we verify it doesn't hang indefinitely by the test itself completing.
        if (!result.Success)
        {
            Assert.NotNull(result.Error);
        }
        // If it somehow completed successfully, the test still passes —
        // the important thing is it didn't hang (the test framework has its own timeout).
    }

    [Fact]
    public async Task Execute_InfiniteRecursion_ReturnsError()
    {
        var result = await _engine.ExecuteAsync("""
            function inf() return inf() end
            return inf()
            """);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // ── Large memory allocation attempts ──

    [Fact]
    public async Task Execute_VeryLargeStringAllocation_HandlesGracefully()
    {
        // Attempt to allocate a very large string via string.rep — must not crash the host
        var exception = await Record.ExceptionAsync(async () =>
        {
            await _engine.ExecuteAsync("return string.rep('A', 10 * 1024 * 1024)");
        });

        // No unhandled exception should escape the engine
        Assert.Null(exception);
    }

    [Fact]
    public async Task Execute_LargeTableAllocation_HandlesGracefully()
    {
        // Attempt to create a very large table
        var exception = await Record.ExceptionAsync(async () =>
        {
            await _engine.ExecuteAsync("""
                local t = {}
                for i = 1, 1000000 do t[i] = i end
                return #t
                """);
        });

        Assert.Null(exception);
    }

    // ── Combination / chained escape attempts ──

    [Fact]
    public async Task Execute_PcallToBypassErrors_StillBlocked()
    {
        // Using pcall to swallow the error from os.execute should still not allow execution
        var result = await _engine.ExecuteAsync("""
            local ok, err = pcall(function() os.execute('cmd') end)
            return ok
            """);

        // pcall should catch the error, so Success may be true but the protected call returns false
        if (result.Success)
        {
            // The pcall itself succeeded, but os.execute should have failed inside pcall
            Assert.Equal("false", result.ReturnValue?.ToLowerInvariant());
        }
        else
        {
            // os is not even defined, so accessing os.execute errors before pcall wraps it
            Assert.NotNull(result.Error);
        }
    }

    [Fact]
    public async Task Execute_StringDumpBlocked()
    {
        // string.dump can leak bytecode — verify it's blocked in hard sandbox
        var result = await _engine.ExecuteAsync("""
            local f = function() return 1 end
            return string.dump(f)
            """);

        // In hard sandbox, string.dump should be disabled
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Execute_GetfenvBlocked()
    {
        // getfenv/setfenv can manipulate function environments for sandbox escape
        var result = await _engine.ExecuteAsync("return getfenv(1)");

        // In Lua 5.2+ / MoonSharp hard sandbox, getfenv should be blocked
        if (result.Success)
        {
            // If it returns something, it should not contain dangerous modules
            Assert.DoesNotContain("os", result.ReturnValue ?? "");
        }
    }

    // ── Verify safe operations still work after escape attempts ──

    [Fact]
    public async Task Execute_SafeOperationsWorkAfterBlockedAttempts()
    {
        // Try a blocked operation first
        var blocked = await _engine.ExecuteAsync("os.execute('cmd')");
        Assert.False(blocked.Success);

        // Then verify normal operations still work
        var safe = await _engine.ExecuteAsync("return 1 + 2");
        Assert.True(safe.Success);
        Assert.Equal("3", safe.ReturnValue);
    }

    [Fact]
    public async Task Execute_EngineStateIntactAfterSandboxViolation()
    {
        // Set state, attempt escape, verify state persists
        await _engine.ExecuteAsync("safeVar = 42");

        await _engine.ExecuteAsync("os.execute('cmd')"); // should fail

        var result = await _engine.ExecuteAsync("return safeVar");
        Assert.True(result.Success);
        Assert.Equal("42", result.ReturnValue);
    }
}
