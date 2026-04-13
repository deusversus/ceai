using System.IO;
using CEAISuite.Engine.Lua;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for S3 controlled require() module loader.
/// </summary>
public sealed class LuaModuleLoaderTests : IDisposable
{
    private readonly MoonSharpLuaEngine _engine;
    private readonly string _tempDir;

    public LuaModuleLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ceai_test_lua_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _engine = new MoonSharpLuaEngine(executionTimeout: TimeSpan.FromSeconds(5));
        _engine.ModuleSearchPaths.Add(_tempDir);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { /* cleanup best-effort */ }
    }

    [Fact]
    public async Task Require_LoadsModuleFromSearchPath()
    {
        File.WriteAllText(Path.Combine(_tempDir, "helpers.lua"), """
            local M = {}
            function M.greet(name)
                return "Hello, " .. name
            end
            return M
            """);

        var result = await _engine.ExecuteAsync("""
            local helpers = require("helpers")
            return helpers.greet("World")
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Hello, World", result.ReturnValue);
    }

    [Fact]
    public async Task Require_CachesModuleOnSecondCall()
    {
        File.WriteAllText(Path.Combine(_tempDir, "counter.lua"), """
            local M = {}
            M.count = 0
            M.count = M.count + 1
            return M
            """);

        var result = await _engine.ExecuteAsync("""
            local c1 = require("counter")
            local c2 = require("counter")
            return c1 == c2
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task Require_NotFoundModule_ReturnsError()
    {
        var result = await _engine.ExecuteAsync("""
            local x = require("nonexistent_module")
            """);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task Require_NestedDots_ResolvesToSubdirectory()
    {
        var subDir = Path.Combine(_tempDir, "utils");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "math.lua"), """
            local M = {}
            M.add = function(a, b) return a + b end
            return M
            """);

        var result = await _engine.ExecuteAsync("""
            local umath = require("utils.math")
            return umath.add(3, 4)
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("7", result.ReturnValue);
    }

    [Fact]
    public async Task Require_PathTraversal_Blocked()
    {
        // Create a file outside the search path
        var parentFile = Path.Combine(Path.GetTempPath(), "evil.lua");
        try
        {
            File.WriteAllText(parentFile, "return 'pwned'");

            var result = await _engine.ExecuteAsync("""
                local x = require("../evil")
                """);

            Assert.False(result.Success);
            Assert.True(
                result.Error!.Contains("not found") || result.Error.Contains("escapes"),
                $"Expected path security error, got: {result.Error}");
        }
        finally
        {
            try { File.Delete(parentFile); } catch { }
        }
    }

    [Fact]
    public async Task Reset_ClearsModuleCache()
    {
        File.WriteAllText(Path.Combine(_tempDir, "tracked.lua"), "return true");

        await _engine.ExecuteAsync("require('tracked')");
        _engine.Reset();
        _engine.ModuleSearchPaths.Add(_tempDir); // re-add after reset

        // After reset, require should reload the file
        File.WriteAllText(Path.Combine(_tempDir, "tracked.lua"), "return 42");
        var result = await _engine.ExecuteAsync("return require('tracked')");

        Assert.True(result.Success, result.Error);
        Assert.Equal("42", result.ReturnValue);
    }
}
