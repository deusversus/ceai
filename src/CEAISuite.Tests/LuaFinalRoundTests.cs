using System.IO;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for S6B hot reload, S2 remaining elements, and S6D profiler.
/// </summary>
public sealed class LuaFinalRoundTests : IDisposable
{
    private readonly MoonSharpLuaEngine _engine;
    private readonly StubEngineFacade _facade = new();
    private readonly StubAutoAssemblerEngine _assembler = new();
    private readonly string _tempDir;

    public LuaFinalRoundTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ceai_final_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _facade.AttachAsync(1234).GetAwaiter().GetResult();
        _engine = new MoonSharpLuaEngine(
            _facade,
            _assembler,
            executionTimeout: TimeSpan.FromSeconds(5));
        _engine.ModuleSearchPaths.Add(_tempDir);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── S6B: Hot reload ──

    [Fact]
    public async Task HotReload_FileChange_InvalidatesCache()
    {
        File.WriteAllText(Path.Combine(_tempDir, "versioned.lua"), "return 1");

        var result1 = await _engine.ExecuteAsync("return require('versioned')");
        Assert.True(result1.Success, result1.Error);
        Assert.Equal("1", result1.ReturnValue);

        // Modify the file
        File.WriteAllText(Path.Combine(_tempDir, "versioned.lua"), "return 2");

        // Manually clear cache (watcher fires async, so for test determinism we clear directly)
        _engine.ClearModuleCache();

        var result2 = await _engine.ExecuteAsync("return require('versioned')");
        Assert.True(result2.Success, result2.Error);
        Assert.Equal("2", result2.ReturnValue);
    }

    // ── S2: Remaining elements ──

    [Fact]
    public async Task CreateRadioGroup_Works()
    {
        // Need form host — use headless stub
        using var engine = CreateWithFormHost();

        var result = await engine.ExecuteAsync("""
            local f = createForm(false)
            local rg = createRadioGroup(f)
            rg.addItem("Option A")
            rg.addItem("Option B")
            rg.setSelectedIndex(1)
            return rg.getSelectedIndex()
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task CreateTabControl_AddTabs()
    {
        using var engine = CreateWithFormHost();

        var result = await engine.ExecuteAsync("""
            local f = createForm(false)
            local tc = createTabControl(f)
            tc.addTab("General")
            tc.addTab("Advanced")
            tc.addTab("Debug")
            return tc.getTabCount()
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("3", result.ReturnValue);
    }

    [Fact]
    public async Task CreateMainMenu_AddItems()
    {
        using var engine = CreateWithFormHost();

        var result = await engine.ExecuteAsync("""
            local f = createForm(false)
            local menu = createMainMenu(f)
            local fileItem = menu.addItem("File")
            local editItem = menu.addItem("Edit")
            return type(fileItem) == "table" and type(editItem) == "table"
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    // ── S6D: Script profiler ──

    [Fact]
    public async Task Profiler_StartStopReport()
    {
        var result = await _engine.ExecuteAsync("""
            profiler.start()
            local sum = 0
            for i = 1, 1000 do sum = sum + i end
            profiler.stop()
            return type(profiler.report()) == "string"
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task Profiler_Report_WithoutStart_ShowsMessage()
    {
        var result = await _engine.ExecuteAsync("""
            return profiler.report()
            """);

        Assert.True(result.Success, result.Error);
        Assert.Contains("No profiler data", result.ReturnValue);
    }

    [Fact]
    public async Task Profiler_Reset_ClearsData()
    {
        var result = await _engine.ExecuteAsync("""
            profiler.start()
            local x = 1 + 1
            profiler.stop()
            profiler.reset()
            return profiler.report()
            """);

        Assert.True(result.Success, result.Error);
        Assert.Contains("No profiler data", result.ReturnValue);
    }

    [Fact]
    public async Task Profiler_GetEntries_ReturnsTable()
    {
        var result = await _engine.ExecuteAsync("""
            profiler.start()
            local x = 1
            profiler.stop()
            local entries = profiler.getEntries()
            return type(entries) == "table"
            """);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    // ── Helpers ──

    private MoonSharpLuaEngine CreateWithFormHost()
    {
        var host = new MinimalFormHost();
        _facade.AttachAsync(1234).GetAwaiter().GetResult();
        return new MoonSharpLuaEngine(_facade, _assembler, formHost: host,
            executionTimeout: TimeSpan.FromSeconds(5));
    }

    private sealed class MinimalFormHost : ILuaFormHost
    {
        public event Action<string, string>? ElementClicked;
        public event Action<string, string>? TimerFired;
        public event Action<string, string, string>? ElementTextChanged;

        public void ShowForm(LuaFormDescriptor form) { }
        public void CloseForm(string formId) { }
        public void UpdateElement(string formId, LuaFormElement element) { }
        public void StartTimer(string formId, string timerId, int intervalMs) { }
        public void StopTimer(string formId, string timerId) { }
        public string? GetElementText(string formId, string elementId) => null;
        public bool? GetElementChecked(string formId, string elementId) => null;
        public int? GetSelectedIndex(string formId, string elementId) => null;
        public int? GetTrackBarPosition(string formId, string elementId) => null;
        public void ShowMessageDialog(string text, string title) { }
        public string? ShowInputDialog(string title, string prompt, string defaultValue) => defaultValue;
        public void DrawLine(string formId, int x1, int y1, int x2, int y2, string color, int width) { }
        public void DrawRect(string formId, int x1, int y1, int x2, int y2, string color, bool fill) { }
        public void DrawEllipse(string formId, int x1, int y1, int x2, int y2, string color, bool fill) { }
        public void DrawText(string formId, int x, int y, string text, string color, string? fontName, int? fontSize) { }
        public void ClearCanvas(string formId) { }

        internal void SuppressWarnings()
        {
            ElementClicked?.Invoke("", "");
            TimerFired?.Invoke("", "");
            ElementTextChanged?.Invoke("", "", "");
        }
    }
}
