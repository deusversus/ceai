using System.IO;
using CEAISuite.Application;
using CEAISuite.Engine.Lua;
using Microsoft.Extensions.Logging.Abstractions;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for AutorunScriptService: listing, enabling/disabling, and execution.
/// </summary>
public sealed class AutorunScriptServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _autorunDir;
    private readonly MoonSharpLuaEngine _engine;
    private readonly AutorunScriptService _service;

    public AutorunScriptServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ceai_autorun_test_{Guid.NewGuid():N}");
        _autorunDir = Path.Combine(_tempDir, "scripts", "autorun");
        Directory.CreateDirectory(_autorunDir);

        _engine = new MoonSharpLuaEngine(executionTimeout: TimeSpan.FromSeconds(5));

        // Use reflection to set the private _autorunDir field for testing
        var service = new AutorunScriptService(_engine, NullLogger<AutorunScriptService>.Instance);
        var field = typeof(AutorunScriptService).GetField("_autorunDir",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(service, _autorunDir);
        _service = service;
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void ListScripts_EmptyDir_ReturnsEmpty()
    {
        // Clear the autorun dir
        foreach (var f in Directory.GetFiles(_autorunDir)) File.Delete(f);
        var scripts = _service.ListScripts();
        Assert.Empty(scripts);
    }

    [Fact]
    public void ListScripts_ReturnsAlphabetically()
    {
        File.WriteAllText(Path.Combine(_autorunDir, "02_second.lua"), "-- second");
        File.WriteAllText(Path.Combine(_autorunDir, "01_first.lua"), "-- first");
        File.WriteAllText(Path.Combine(_autorunDir, "03_third.lua"), "-- third");

        var scripts = _service.ListScripts();
        Assert.Equal(3, scripts.Count);
        Assert.Equal("01_first.lua", scripts[0].Name);
        Assert.Equal("02_second.lua", scripts[1].Name);
        Assert.Equal("03_third.lua", scripts[2].Name);
    }

    [Fact]
    public void SetEnabled_DisablesScript()
    {
        File.WriteAllText(Path.Combine(_autorunDir, "test.lua"), "-- test");

        _service.SetEnabled("test.lua", false);
        var scripts = _service.ListScripts();

        Assert.Single(scripts);
        Assert.False(scripts[0].Enabled);
    }

    [Fact]
    public void SetEnabled_ReEnables()
    {
        File.WriteAllText(Path.Combine(_autorunDir, "test.lua"), "-- test");

        _service.SetEnabled("test.lua", false);
        _service.SetEnabled("test.lua", true);
        var scripts = _service.ListScripts();

        Assert.True(scripts[0].Enabled);
    }

    [Fact]
    public async Task ExecuteAllAsync_RunsEnabledScripts()
    {
        File.WriteAllText(Path.Combine(_autorunDir, "test.lua"), "print('autorun executed')");

        var output = new List<string>();
        _engine.OutputWritten += s => output.Add(s);

        await _service.ExecuteAllAsync();

        Assert.Contains("autorun executed", output);
    }

    [Fact]
    public async Task ExecuteAllAsync_SkipsDisabledScripts()
    {
        File.WriteAllText(Path.Combine(_autorunDir, "disabled.lua"), "print('should not run')");
        _service.SetEnabled("disabled.lua", false);

        var output = new List<string>();
        _engine.OutputWritten += s => output.Add(s);

        await _service.ExecuteAllAsync();

        Assert.DoesNotContain("should not run", output);
    }

    [Fact]
    public async Task ExecuteAllAsync_ContinuesAfterScriptError()
    {
        File.WriteAllText(Path.Combine(_autorunDir, "01_bad.lua"), "error('intentional')");
        File.WriteAllText(Path.Combine(_autorunDir, "02_good.lua"), "print('good')");

        var output = new List<string>();
        _engine.OutputWritten += s => output.Add(s);

        await _service.ExecuteAllAsync();

        // The good script should still run despite the first one failing
        Assert.Contains("good", output);
    }
}
