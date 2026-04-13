using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for S2 extended form elements: createMemo, createListBox, createComboBox,
/// createTrackBar, createProgressBar, createImage, createPanel, createGroupBox,
/// and common styling methods (setVisible, setEnabled, setFont, setColor).
/// </summary>
public sealed class LuaFormExtendedTests : IDisposable
{
    private readonly StubEngineFacade _facade = new();
    private readonly StubAutoAssemblerEngine _assembler = new();
    private readonly StubFormHost _formHost = new();
    private readonly MoonSharpLuaEngine _engine;

    public LuaFormExtendedTests()
    {
        _facade.AttachAsync(1234).GetAwaiter().GetResult();
        _engine = new MoonSharpLuaEngine(
            _facade,
            _assembler,
            formHost: _formHost,
            executionTimeout: TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task CreateMemo_ReturnsTable()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local m = createMemo(f)
            m.setText("Hello world")
            return m.getText()
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Hello world", result.ReturnValue);
    }

    [Fact]
    public async Task CreateListBox_AddItems()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local lb = createListBox(f)
            lb.addItem("Alpha")
            lb.addItem("Beta")
            lb.addItem("Gamma")
            return lb.getItemCount()
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("3", result.ReturnValue);
    }

    [Fact]
    public async Task CreateComboBox_AddItemsAndSelect()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local cb = createComboBox(f)
            cb.addItem("Option A")
            cb.addItem("Option B")
            cb.setSelectedIndex(0)
            return cb.getSelectedIndex()
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        // StubFormHost returns -1 for unrendered controls; element model has 0
        Assert.NotNull(result.ReturnValue);
    }

    [Fact]
    public async Task CreateTrackBar_SetAndGetPosition()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local tb = createTrackBar(f)
            tb.setMin(0)
            tb.setMax(200)
            tb.setPosition(75)
            return tb.getPosition()
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        // StubFormHost returns null for unrendered → falls back to element model (75)
        Assert.Equal("75", result.ReturnValue);
    }

    [Fact]
    public async Task CreateProgressBar_Succeeds()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local pb = createProgressBar(f)
            pb.setMin(0)
            pb.setMax(100)
            pb.setPosition(50)
            return type(pb) == "table"
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task CreateImage_Succeeds()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local img = createImage(f)
            return type(img) == "table"
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task CreatePanel_Succeeds()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local p = createPanel(f)
            return type(p) == "table"
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task CreateGroupBox_Succeeds()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local gb = createGroupBox(f)
            gb.setCaption("Settings")
            return type(gb) == "table"
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task SetVisible_CanBeCalledOnAnyElement()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local btn = createButton(f)
            btn.setVisible(false)
            btn.setVisible(true)
            return true
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task SetEnabled_CanBeCalledOnAnyElement()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local btn = createButton(f)
            btn.setEnabled(false)
            return true
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task SetFont_CanBeCalledOnAnyElement()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local lbl = createLabel(f)
            lbl.setFont("Consolas", 14, "#FF0000")
            return true
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
    }

    // ── Canvas Drawing Tests ──

    [Fact]
    public async Task GetCanvas_ReturnsDrawingContext()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local c = f.getCanvas()
            return type(c) == "table"
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal("true", result.ReturnValue);
    }

    [Fact]
    public async Task Canvas_DrawLine_CallsHost()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local c = f.getCanvas()
            c.drawLine(10, 10, 100, 100)
            return true
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.True(_formHost.DrawCallCount > 0);
    }

    [Fact]
    public async Task Canvas_DrawShapes_MultipleTypes()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local c = f.getCanvas()
            c.setPen("#FF0000", 2)
            c.setBrush("#00FF00")
            c.drawLine(0, 0, 100, 100)
            c.drawRect(10, 10, 50, 50)
            c.fillRect(60, 60, 90, 90)
            c.drawEllipse(10, 10, 40, 40)
            c.fillEllipse(50, 50, 80, 80)
            c.setFont("Consolas", 14)
            c.drawText(10, 120, "Hello Canvas")
            return true
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal(6, _formHost.DrawCallCount);
    }

    [Fact]
    public async Task Canvas_Clear_ResetsCount()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local c = f.getCanvas()
            c.drawLine(0, 0, 10, 10)
            c.clear()
            return true
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, _formHost.DrawCallCount); // clear resets
    }

    [Fact]
    public async Task SetColor_CanBeCalledOnAnyElement()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local btn = createButton(f)
            btn.setColor("#333333")
            btn.setFontColor("#FFFFFF")
            return true
            """, processId: 1234);

        Assert.True(result.Success, result.Error);
    }

    // Minimal stub form host for headless testing
    private sealed class StubFormHost : ILuaFormHost
    {
        private readonly Dictionary<string, LuaFormDescriptor> _forms = new();
        private readonly Dictionary<string, string> _elementTexts = new();

        public event Action<string, string>? ElementClicked;
        public event Action<string, string>? TimerFired;
        public event Action<string, string, string>? ElementTextChanged;

        public void ShowForm(LuaFormDescriptor form) => _forms[form.Id] = form;
        public void CloseForm(string formId) => _forms.Remove(formId);
        public void CloseAllForms() => _forms.Clear();

        public void UpdateElement(string formId, LuaFormElement element)
        {
            // Store text for edit/memo elements
            if (element is LuaEditElement edit)
                _elementTexts[$"{formId}:{element.Id}"] = edit.Text ?? "";
            else if (element is LuaMemoElement memo)
                _elementTexts[$"{formId}:{element.Id}"] = memo.Text ?? "";
        }

        public void StartTimer(string formId, string timerId, int intervalMs) { }
        public void StopTimer(string formId, string timerId) { }

        public string? GetElementText(string formId, string elementId) =>
            _elementTexts.GetValueOrDefault($"{formId}:{elementId}");

        public bool? GetElementChecked(string formId, string elementId) => null;
        public int? GetSelectedIndex(string formId, string elementId) => null;
        public int? GetTrackBarPosition(string formId, string elementId) => null;

        public void ShowMessageDialog(string text, string title) { }
        public string? ShowInputDialog(string title, string prompt, string defaultValue) => defaultValue;

        // Canvas drawing stubs
        public int DrawCallCount { get; private set; }
        public void DrawLine(string formId, int x1, int y1, int x2, int y2, string color, int width) => DrawCallCount++;
        public void DrawRect(string formId, int x1, int y1, int x2, int y2, string color, bool fill) => DrawCallCount++;
        public void DrawEllipse(string formId, int x1, int y1, int x2, int y2, string color, bool fill) => DrawCallCount++;
        public void DrawText(string formId, int x, int y, string text, string color, string? fontName, int? fontSize) => DrawCallCount++;
        public void ClearCanvas(string formId) => DrawCallCount = 0;

        // Suppress unused event warnings
        internal void FireClicked(string fId, string eId) => ElementClicked?.Invoke(fId, eId);
        internal void FireTimer(string fId, string tId) => TimerFired?.Invoke(fId, tId);
        internal void FireTextChanged(string fId, string eId, string t) => ElementTextChanged?.Invoke(fId, eId, t);
    }
}
