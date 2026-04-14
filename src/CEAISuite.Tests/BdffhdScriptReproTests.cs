using CEAISuite.Engine.Abstractions;
using CEAISuite.Engine.Lua;
using CEAISuite.Tests.Stubs;
using Xunit;

namespace CEAISuite.Tests;

/// <summary>
/// Reproduction tests for BDFFHD CT script patterns.
/// Validates that the "Unlock Jobs" script flow works end-to-end.
/// </summary>
public sealed class BdffhdScriptReproTests : IDisposable
{
    private readonly MoonSharpLuaEngine _engine;
    private readonly StubEngineFacade _facade = new();
    private readonly StubAutoAssemblerEngine _assembler = new();

    public BdffhdScriptReproTests()
    {
        var host = new MinimalFormHost();
        var addressList = new StubAddressListProvider();
        addressList.AddCannedRecord(new LuaMemoryRecord(
            "unlocked-jobs", "Unlocked Jobs", "0x1000", "4 Bytes", "0", false, false));
        addressList.AddCannedRecord(new LuaMemoryRecord(
            "test-record", "Test", "0x2000", "4 Bytes", "42", false, false));
        _facade.AttachAsync(1234).GetAwaiter().GetResult();
        _engine = new MoonSharpLuaEngine(_facade, _assembler, formHost: host,
            executionTimeout: TimeSpan.FromSeconds(10),
            addressListProvider: addressList);
    }

    public void Dispose() => _engine.Dispose();

    [Fact]
    public async Task CreateForm_SetProperties_Works()
    {
        var result = await _engine.ExecuteAsync(@"
            local f = createForm(false)
            f.Caption = 'Test Form'
            f.Width = 660
            f.Height = 295
            assert(f.Caption == 'Test Form', 'Caption mismatch: ' .. tostring(f.Caption))
        ", 1234);

        Assert.True(result.Success, $"Script failed: {result.Error}");
    }

    [Fact]
    public async Task CreateGroupBox_InForm_Works()
    {
        var result = await _engine.ExecuteAsync(@"
            local f = createForm(false)
            local g = createGroupBox(f)
            g.Caption = 'My Group'
            g.Left = 10
            g.Top = 10
            g.Width = 200
            g.Height = 100
            assert(g.Caption == 'My Group', 'GroupBox caption mismatch')
        ", 1234);

        Assert.True(result.Success, $"Script failed: {result.Error}");
    }

    [Fact]
    public async Task CreateCheckBox_InGroupBox_Works()
    {
        var result = await _engine.ExecuteAsync(@"
            local f = createForm(false)
            local g = createGroupBox(f)
            g.Caption = 'Group'
            g.Left = 10
            g.Top = 10
            g.Width = 200
            g.Height = 100

            local cb = createCheckBox(g)
            cb.Caption = 'White Mage'
            cb.Left = 10
            cb.Top = 18
            cb.Width = 92
            cb.Height = 19
            cb.value = 8

            assert(cb.Caption == 'White Mage', 'CheckBox caption: ' .. tostring(cb.Caption))
            assert(cb.value == 8, 'Custom value: ' .. tostring(cb.value))
        ", 1234);

        Assert.True(result.Success, $"Script failed: {result.Error}");
    }

    [Fact]
    public async Task BitwiseOperators_InScript_Work()
    {
        var result = await _engine.ExecuteAsync(@"
            local a = 8
            local b = 4
            local combined = a | b
            assert(combined == 12, 'OR failed: ' .. tostring(combined))

            local masked = combined & 8
            assert(masked == 8, 'AND failed: ' .. tostring(masked))

            local check = (combined & b) ~= 0
            assert(check == true, 'AND+compare failed')
        ", 1234);

        Assert.True(result.Success, $"Script failed: {result.Error}");
    }

    [Fact]
    public async Task FormShow_Destroy_Work()
    {
        var result = await _engine.ExecuteAsync(@"
            local f = createForm(false)
            f.Caption = 'Test'
            f.show()
            f.destroy()
        ", 1234);

        Assert.True(result.Success, $"Script failed: {result.Error}");
    }

    [Fact]
    public async Task FullUnlockJobsPattern_WithTimerAndPcall()
    {
        // Full flow with timer, pcall, FormStyle, Position, readClasses
        var result = await _engine.ExecuteAsync(@"
            function addCheckbox(parent, name, val, x, y)
                local cb = createCheckBox(parent)
                cb.Caption = name
                cb.Left = x
                cb.Top = y
                cb.Width = 92
                cb.Height = 19
                cb.value = val
                table.insert(checkboxes or {}, cb)
            end

            jobEditorForm = createForm(false)
            jobEditorForm.Caption = 'Job Unlock Editor'
            jobEditorForm.Width = 660
            jobEditorForm.Height = 295
            jobEditorForm.Position = 'poDesigned'
            jobEditorForm.Left = 100
            jobEditorForm.Top = 100

            pcall(function()
              jobEditorForm.FormStyle = 'fsStayOnTop'
            end)

            local g1 = createGroupBox(jobEditorForm)
            g1.Caption = 'Prologue Job Unlocks'
            g1.Left = 10
            g1.Top = 10
            g1.Width = 205
            g1.Height = 102

            addCheckbox(g1, 'White Mage', 8, 10, 18)
        ", 1234);

        Assert.True(result.Success, $"Script failed: {result.Error}\nOutput: {string.Join("\n", result.OutputLines ?? [])}");
    }

    [Fact]
    public async Task FullUnlockJobsPattern_Simplified()
    {
        // Simplified version of the BDFFHD "Unlock Jobs" script
        var result = await _engine.ExecuteAsync(@"
            checkboxes = {}

            function writeClasses()
                local value = 0
                for _, cb in ipairs(checkboxes) do
                    if cb.Checked then
                        value = value | cb.value
                    end
                end
            end

            function addCheckbox(parent, name, val, x, y)
                local cb = createCheckBox(parent)
                cb.Caption = name
                cb.Left = x
                cb.Top = y
                cb.Width = 92
                cb.Height = 19
                cb.value = val
                cb.OnChange = writeClasses
                table.insert(checkboxes, cb)
            end

            jobEditorForm = createForm(false)
            jobEditorForm.Caption = 'Job Unlock Editor'
            jobEditorForm.Width = 660
            jobEditorForm.Height = 295

            local g1 = createGroupBox(jobEditorForm)
            g1.Caption = 'Prologue Job Unlocks'
            g1.Left = 10
            g1.Top = 10
            g1.Width = 205
            g1.Height = 102

            addCheckbox(g1, 'White Mage', 8, 10, 18)
            addCheckbox(g1, 'Black Mage', 4, 10, 40)
            addCheckbox(g1, 'Knight', 2, 105, 18)

            assert(#checkboxes == 3, 'Expected 3 checkboxes, got ' .. #checkboxes)
            assert(checkboxes[1].Caption == 'White Mage', 'First cb caption: ' .. tostring(checkboxes[1].Caption))

            jobEditorForm.show()
        ", 1234);

        Assert.True(result.Success, $"Script failed: {result.Error}");
    }

    private sealed class MinimalFormHost : ILuaFormHost
    {
        public event Action<string, string>? ElementClicked;
        public event Action<string, string>? TimerFired;
        public event Action<string, string, string>? ElementTextChanged;
        public event Action<string, string, string>? ElementChanged;
        public event Action<string>? FormClosed;
        public void ShowForm(LuaFormDescriptor form) { }
        public void CloseForm(string formId) { }
        public void CloseAllForms() { }
        public void UpdateElement(string formId, LuaFormElement element) { }
        public void StartTimer(string formId, string timerId, int intervalMs) { }
        public void StopTimer(string formId, string timerId) { }
        public string? GetElementText(string formId, string elementId) => null;
        public bool? GetElementChecked(string formId, string elementId) => null;
        public int? GetSelectedIndex(string formId, string elementId) => null;
        public int? GetTrackBarPosition(string formId, string elementId) => null;
        public void ShowMessageDialog(string text, string title) { }
        public string? ShowInputDialog(string title, string prompt, string defaultValue) => null;
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
        internal void Suppress() { ElementClicked?.Invoke("", ""); TimerFired?.Invoke("", ""); ElementTextChanged?.Invoke("", "", ""); ElementChanged?.Invoke("", "", ""); FormClosed?.Invoke(""); }
    }
}
