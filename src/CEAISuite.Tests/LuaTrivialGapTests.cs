using System.IO;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for the "trivial" gap functions: getCurrentMemScan, executeCode,
/// injectDLL, reinitializeSymbolhandler, createPopupMenu, createSplitter.
/// </summary>
public sealed class LuaTrivialGapTests : IDisposable
{
    private readonly StubEngineFacade _facade = new();
    private readonly StubAutoAssemblerEngine _assembler = new();
    private readonly StubScanEngine _scanner = new();
    private readonly StubSymbolEngine _symbolEngine = new();
    private readonly MoonSharpLuaEngine _engine;

    public LuaTrivialGapTests()
    {
        _facade.AttachAsync(1234).GetAwaiter().GetResult();
        _engine = new MoonSharpLuaEngine(
            _facade,
            _assembler,
            executionTimeout: TimeSpan.FromSeconds(5),
            scanEngine: _scanner,
            symbolEngine: _symbolEngine);
    }

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task GetCurrentMemScan_BeforeCreate_ReturnsNil()
    {
        var result = await _engine.ExecuteAsync("""
            return getCurrentMemScan() == nil
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task GetCurrentMemScan_AfterCreate_ReturnsScan()
    {
        _scanner.NextScanResult = new ScanResultSet(
            "test", 1234,
            new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            [], 10, 40960, DateTimeOffset.UtcNow);

        var result = await _engine.ExecuteAsync("""
            local scan = createMemScan()
            scan.firstScan(scan, "exact", "int32", "100")
            local current = getCurrentMemScan()
            return current ~= nil
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task ExecuteCode_DelegatesToAA()
    {
        var result = await _engine.ExecuteAsync("""
            executeCode(0x400000)
            return true
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task InjectDLL_DelegatesToAA()
    {
        var result = await _engine.ExecuteAsync("""
            injectDLL("C:\\test.dll")
            return true
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task ReinitializeSymbolhandler_Runs()
    {
        var result = await _engine.ExecuteAsync("""
            reinitializeSymbolhandler()
            return true
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task CreatePopupMenu_Works()
    {
        using var engine = CreateWithFormHost();

        var result = await engine.ExecuteAsync("""
            local f = createForm(false)
            local pm = createPopupMenu(f)
            local item = pm.addItem("Copy")
            return type(item) == "table"
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task CreateSplitter_Works()
    {
        using var engine = CreateWithFormHost();

        var result = await engine.ExecuteAsync("""
            local f = createForm(false)
            local sp = createSplitter(f)
            sp.setVertical(true)
            return type(sp) == "table"
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    private MoonSharpLuaEngine CreateWithFormHost()
    {
        _facade.AttachAsync(1234).GetAwaiter().GetResult();
        return new MoonSharpLuaEngine(_facade, _assembler, formHost: new MinimalFormHost(),
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
        internal void Suppress() { ElementClicked?.Invoke("",""); TimerFired?.Invoke("",""); ElementTextChanged?.Invoke("","",""); }
    }
}
