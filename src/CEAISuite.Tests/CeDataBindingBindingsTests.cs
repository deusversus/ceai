using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for S8: Reactive Data Binding — element:bind("Caption", record, "Value") API.
/// </summary>
public sealed class CeDataBindingBindingsTests : IDisposable
{
    private readonly StubEngineFacade _facade = new();
    private readonly StubAutoAssemblerEngine _assembler = new();
    private readonly StubFormHost _formHost = new();
    private readonly StubAddressListProvider _addressList = new();
    private readonly StubDataBindingHost _bindingHost = new();
    private readonly MoonSharpLuaEngine _engine;

    public CeDataBindingBindingsTests()
    {
        _facade.AttachAsync(1234).GetAwaiter().GetResult();
        _addressList.AddCannedRecord(new LuaMemoryRecord("rec1", "Health", "0x1000", "Int32", "100", true, false));
        _addressList.AddCannedRecord(new LuaMemoryRecord("rec2", "Mana", "0x2000", "Int32", "50", false, false));

        _engine = new MoonSharpLuaEngine(
            _facade,
            _assembler,
            formHost: _formHost,
            executionTimeout: TimeSpan.FromSeconds(5),
            addressListProvider: _addressList,
            dataBindingHost: _bindingHost);
    }

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task Bind_UpdatesCaptionOnRefresh()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local lbl = createLabel(f)
            local rec = addresslist_getMemoryRecordByID("rec1")
            lbl:bind("Caption", rec, "Value")
            """, processId: 1234);
        Assert.True(result.Success, result.Error);

        // Initial value is "100", fire refresh to propagate
        _bindingHost.FireRefresh();

        // Check that UpdateElement was called with the correct caption
        Assert.NotNull(_formHost.LastUpdatedElement);
        Assert.Equal("100", _formHost.LastUpdatedElement.Caption);
    }

    [Fact]
    public async Task Bind_DetectsValueChange()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local lbl = createLabel(f)
            local rec = addresslist_getMemoryRecordByID("rec1")
            lbl:bind("Caption", rec, "Value")
            """, processId: 1234);
        Assert.True(result.Success, result.Error);

        // First refresh — sets initial value
        _bindingHost.FireRefresh();
        Assert.Equal("100", _formHost.LastUpdatedElement?.Caption);

        // Change the record value
        _addressList.SetValue("rec1", "200");

        // Second refresh — should detect the change
        _bindingHost.FireRefresh();
        Assert.Equal("200", _formHost.LastUpdatedElement?.Caption);
    }

    [Fact]
    public async Task Bind_DescriptionProperty()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local lbl = createLabel(f)
            local rec = addresslist_getMemoryRecordByID("rec1")
            lbl:bind("Caption", rec, "Description")
            """, processId: 1234);
        Assert.True(result.Success, result.Error);

        _bindingHost.FireRefresh();

        Assert.NotNull(_formHost.LastUpdatedElement);
        Assert.Equal("Health", _formHost.LastUpdatedElement.Caption);
    }

    [Fact]
    public async Task Bind_AddressProperty()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local lbl = createLabel(f)
            local rec = addresslist_getMemoryRecordByID("rec1")
            lbl:bind("Caption", rec, "Address")
            """, processId: 1234);
        Assert.True(result.Success, result.Error);

        _bindingHost.FireRefresh();

        Assert.NotNull(_formHost.LastUpdatedElement);
        Assert.Equal("0x1000", _formHost.LastUpdatedElement.Caption);
    }

    [Fact]
    public async Task Bind_EditText()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local ed = createEdit(f)
            local rec = addresslist_getMemoryRecordByID("rec1")
            ed:bind("Text", rec, "Value")
            """, processId: 1234);
        Assert.True(result.Success, result.Error);

        _bindingHost.FireRefresh();

        Assert.NotNull(_formHost.LastUpdatedElement);
        var edit = Assert.IsType<LuaEditElement>(_formHost.LastUpdatedElement);
        Assert.Equal("100", edit.Text);
    }

    [Fact]
    public async Task Unbind_StopsUpdates()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local lbl = createLabel(f)
            local rec = addresslist_getMemoryRecordByID("rec1")
            lbl:bind("Caption", rec, "Value")
            lbl:unbind("Caption")
            """, processId: 1234);
        Assert.True(result.Success, result.Error);

        // Change value and refresh — the unbound label should NOT trigger UpdateElement
        var countBefore = _formHost.UpdateElementCallCount;
        _addressList.SetValue("rec1", "999");
        _bindingHost.FireRefresh();
        Assert.Equal(countBefore, _formHost.UpdateElementCallCount);
    }

    [Fact]
    public async Task Bind_MaxLimitEnforced()
    {
        // Create 200 bindings (max), then try one more
        var script = new System.Text.StringBuilder();
        script.AppendLine("local f = createForm(false)");
        for (int i = 0; i < 200; i++)
        {
            script.Append(System.Globalization.CultureInfo.InvariantCulture, $"local lbl{i} = createLabel(f)\n");
            script.Append("local rec = addresslist_getMemoryRecordByID('rec1')\n");
            script.Append(System.Globalization.CultureInfo.InvariantCulture, $"lbl{i}:bind('Caption', rec, 'Value')\n");
        }

        var result = await _engine.ExecuteAsync(script.ToString(), processId: 1234);
        Assert.True(result.Success, result.Error);

        // 201st binding should fail
        var overflowResult = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local lbl = createLabel(f)
            local rec = addresslist_getMemoryRecordByID("rec1")
            lbl:bind("Caption", rec, "Value")
            """, processId: 1234);
        Assert.False(overflowResult.Success);
        Assert.Contains("Maximum binding limit", overflowResult.Error);
    }

    [Fact]
    public async Task Bind_FormCloseRemovesBindings()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local lbl = createLabel(f)
            local rec = addresslist_getMemoryRecordByID("rec1")
            lbl:bind("Caption", rec, "Value")
            f:close()
            """, processId: 1234);
        Assert.True(result.Success, result.Error);

        // After close, refresh should not crash and should not trigger updates
        var countBefore = _formHost.UpdateElementCallCount;
        _bindingHost.FireRefresh();
        Assert.Equal(countBefore, _formHost.UpdateElementCallCount);
    }

    [Fact]
    public async Task Bind_NoChangeNoUpdate()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local lbl = createLabel(f)
            local rec = addresslist_getMemoryRecordByID("rec1")
            lbl:bind("Caption", rec, "Value")
            """, processId: 1234);
        Assert.True(result.Success, result.Error);

        // First refresh
        _bindingHost.FireRefresh();
        var updateCount1 = _formHost.UpdateElementCallCount;

        // Second refresh with same value — should NOT call UpdateElement again
        _bindingHost.FireRefresh();
        var updateCount2 = _formHost.UpdateElementCallCount;

        Assert.Equal(updateCount1, updateCount2);
    }

    [Fact]
    public async Task Bind_ActivePropertyAsBool()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local lbl = createLabel(f)
            local rec = addresslist_getMemoryRecordByID("rec1")
            lbl:bind("Caption", rec, "Active")
            """, processId: 1234);
        Assert.True(result.Success, result.Error);

        _bindingHost.FireRefresh();

        Assert.NotNull(_formHost.LastUpdatedElement);
        // rec1 is active=true
        Assert.Equal("True", _formHost.LastUpdatedElement.Caption);
    }

    [Fact]
    public async Task Bind_VisibleProperty()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local lbl = createLabel(f)
            local rec = addresslist_getMemoryRecordByID("rec1")
            lbl:bind("Visible", rec, "Active")
            """, processId: 1234);
        Assert.True(result.Success, result.Error);

        _bindingHost.FireRefresh();

        Assert.NotNull(_formHost.LastUpdatedElement);
        // rec1.Active is true
        Assert.True(_formHost.LastUpdatedElement.Visible);
    }

    [Fact]
    public async Task Bind_EnabledProperty()
    {
        var result = await _engine.ExecuteAsync("""
            local f = createForm(false)
            local lbl = createLabel(f)
            local rec = addresslist_getMemoryRecordByID("rec2")
            lbl:bind("Enabled", rec, "Active")
            """, processId: 1234);
        Assert.True(result.Success, result.Error);

        _bindingHost.FireRefresh();

        Assert.NotNull(_formHost.LastUpdatedElement);
        // rec2.Active is false
        Assert.False(_formHost.LastUpdatedElement.Enabled);
    }

    // ── Stub implementations ──

    private sealed class StubDataBindingHost : ILuaDataBindingHost
    {
        public event Action? RefreshCycleCompleted;
        public void FireRefresh() => RefreshCycleCompleted?.Invoke();
    }

    private sealed class StubFormHost : ILuaFormHost
    {
        private readonly Dictionary<string, LuaFormDescriptor> _forms = new();
        private readonly Dictionary<string, string> _elementTexts = new();

        public int UpdateElementCallCount { get; private set; }
        public LuaFormElement? LastUpdatedElement { get; private set; }
        public string? LastUpdatedFormId { get; private set; }

        public event Action<string, string>? ElementClicked;
        public event Action<string, string>? TimerFired;
        public event Action<string, string, string>? ElementTextChanged;
        public event Action<string, string, string>? ElementChanged;
        public event Action<string>? FormClosed;

        public void ShowForm(LuaFormDescriptor form) => _forms[form.Id] = form;
        public void CloseForm(string formId)
        {
            _forms.Remove(formId);
            FormClosed?.Invoke(formId);
        }
        public void CloseAllForms() => _forms.Clear();

        public void UpdateElement(string formId, LuaFormElement element)
        {
            UpdateElementCallCount++;
            LastUpdatedFormId = formId;
            LastUpdatedElement = element;
            if (element is LuaEditElement edit)
                _elementTexts[$"{formId}:{element.Id}"] = edit.Text ?? "";
            else if (element is LuaMemoElement memo)
                _elementTexts[$"{formId}:{element.Id}"] = memo.Text ?? "";
        }

        public LuaFormDescriptor? GetLastShownForm() =>
            _forms.Values.LastOrDefault();

        public void StartTimer(string formId, string timerId, int intervalMs) { }
        public void StopTimer(string formId, string timerId) { }
        public string? GetElementText(string formId, string elementId) =>
            _elementTexts.GetValueOrDefault($"{formId}:{elementId}");
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
        public void BringToFront(string formId) { }
        public void SetFormProperty(string formId, string property, object? value) { }
        public object? GetFormProperty(string formId, string property) => null;
        public void SetFormTopMost(string formId, bool topMost) { }
        public void CreateDockPanel(LuaDockPanelDescriptor panel) { }
        public void CloseDockPanel(string panelId) { }

        // Suppress unused event warnings
        internal void FireClicked(string fId, string eId) => ElementClicked?.Invoke(fId, eId);
        internal void FireTimer(string fId, string tId) => TimerFired?.Invoke(fId, tId);
        internal void FireTextChanged(string fId, string eId, string t) => ElementTextChanged?.Invoke(fId, eId, t);
        internal void FireElementChanged(string fId, string eId, string v) => ElementChanged?.Invoke(fId, eId, v);
    }
}
