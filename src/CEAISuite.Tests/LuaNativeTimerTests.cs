using CEAISuite.Engine.Lua;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for S4 createNativeTimer and utility functions.
/// </summary>
public sealed class LuaNativeTimerTests : IDisposable
{
    private readonly MoonSharpLuaEngine _engine;

    public LuaNativeTimerTests()
    {
        _engine = new MoonSharpLuaEngine(executionTimeout: TimeSpan.FromSeconds(10));
    }

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task CreateNativeTimer_ReturnsTable()
    {
        var result = await _engine.ExecuteAsync("""
            local t = createNativeTimer(1000, function() end)
            return type(t) == "table" and type(t._id) == "string"
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task CreateNativeTimer_CanSetEnabled()
    {
        var result = await _engine.ExecuteAsync("""
            local t = createNativeTimer(100, function() end)
            t.setEnabled(false)
            local wasEnabled = t.isEnabled()
            t.setEnabled(true)
            local isNowEnabled = t.isEnabled()
            t.destroy()
            return not wasEnabled and isNowEnabled
            """);

        Assert.True(result.Success, result.Error);
        // After disable: false, after re-enable: true
    }

    [Fact]
    public async Task CreateNativeTimer_Destroy_StopsTimer()
    {
        var result = await _engine.ExecuteAsync("""
            local t = createNativeTimer(100, function() end)
            t.destroy()
            return t.isEnabled()
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("false", result.ReturnValue);
    }

    [Fact]
    public async Task DestroyAllTimers_CleansUp()
    {
        var result = await _engine.ExecuteAsync("""
            createNativeTimer(100, function() end)
            createNativeTimer(200, function() end)
            destroyAllTimers()
            return true
            """);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task CreateNativeTimer_NonFunctionCallback_Throws()
    {
        var result = await _engine.ExecuteAsync("""
            local t = createNativeTimer(100, "not a function")
            """);

        Assert.False(result.Success);
        Assert.Contains("must be a function", result.Error);
    }

    // ── Utility function tests ──

    [Fact]
    public async Task GetCEVersion_ReturnsString()
    {
        var result = await _engine.ExecuteAsync("return getCEVersion()");

        Assert.True(result.Success, result.Error);
        Assert.Contains("CE AI Suite", result.ReturnValue);
    }

    [Fact]
    public async Task GetOperatingSystem_ReturnsString()
    {
        var result = await _engine.ExecuteAsync("return getOperatingSystem()");

        Assert.True(result.Success, result.Error);
        Assert.Contains("Windows", result.ReturnValue);
    }

    [Fact]
    public async Task GetScreenWidth_ReturnsPositiveNumber()
    {
        var result = await _engine.ExecuteAsync("return getScreenWidth() > 0");

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task Md5_ComputesCorrectHash()
    {
        var result = await _engine.ExecuteAsync("""return md5("hello")""");

        Assert.True(result.Success, result.Error);
        Assert.Equal("5d41402abc4b2a76b9719d911017c592", result.ReturnValue);
    }

    [Fact]
    public async Task CreateRef_GetRef_DestroyRef_Lifecycle()
    {
        var result = await _engine.ExecuteAsync("""
            local ref = createRef("my value")
            local val = getRef(ref)
            destroyRef(ref)
            local gone = getRef(ref)
            return val == "my value" and gone == nil
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task OsClock_ReturnsPositiveNumber()
    {
        var result = await _engine.ExecuteAsync("return os_clock() >= 0");

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task GetTickCount64_ReturnsNumber()
    {
        var result = await _engine.ExecuteAsync("return getTickCount64() > 0");

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }
}
