using System.Collections.Concurrent;
using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Registers CE form designer functions (createForm, createButton, etc.) into MoonSharp.
/// Forms are represented as Lua tables with method bindings; the actual WPF rendering
/// is delegated to <see cref="ILuaFormHost"/>.
/// </summary>
internal sealed class CeFormBindings : IDisposable
{
    private int _nextFormId;
    private int _nextElementId;
    private readonly ConcurrentDictionary<string, LuaFormDescriptor> _forms = new();
    private readonly ConcurrentDictionary<string, DynValue> _callbacks = new();
    private ILuaFormHost? _subscribedHost;
    private Action<string, string>? _clickHandler;
    private Action<string, string>? _timerHandler;
    private Action<string, string, string>? _changeHandler;
    private Action<string>? _closeHandler;

    private MoonSharpLuaEngine? _engine;

    /// <summary>Data binding engine for reactive element↔record bindings. Set by <see cref="MoonSharpLuaEngine"/>.</summary>
    internal CeDataBindingBindings? DataBindings { get; set; }

    /// <summary>Shared form descriptors — also used by <see cref="CeDockPanelBindings"/> for dock panels.</summary>
    internal ConcurrentDictionary<string, LuaFormDescriptor> Forms => _forms;

    /// <summary>Shared callback registry — also used by <see cref="CeDockPanelBindings"/> for dock panel events.</summary>
    internal ConcurrentDictionary<string, DynValue> Callbacks => _callbacks;

    /// <summary>Thread-safe element ID generator — shared with <see cref="CeDockPanelBindings"/>.</summary>
    internal int NextElementId() => Interlocked.Increment(ref _nextElementId);

    /// <summary>
    /// Resolve the parent of an element. If parentTable is a form, returns (formId, null).
    /// If parentTable is a container element (groupbox, panel), returns (formId, parentElementId).
    /// </summary>
    private static (string formId, string? parentElementId) ResolveParent(Table parentTable)
    {
        var parentType = parentTable.Get("_type").String;
        if (parentType is "form" or "dockpanel")
            return (parentTable.Get("_id").String, null);
        // Parent is a container element (groupbox, panel) — use its _formId and set ParentElementId
        return (parentTable.Get("_formId").String, parentTable.Get("_id").String);
    }

    public void Register(Script script, ILuaFormHost formHost, MoonSharpLuaEngine? engine = null)
    {
        _engine = engine;

        // Unsubscribe from previous host to prevent handler stacking on Reset
        Unsubscribe();
        _forms.Clear();
        _callbacks.Clear();

        script.Globals["createForm"] = (Func<DynValue, DynValue>)(visibleArg =>
        {
            var formId = $"form_{Interlocked.Increment(ref _nextFormId)}";
            var form = new LuaFormDescriptor(formId, "Lua Form", 400, 300, new List<LuaFormElement>());
            _forms[formId] = form;

            var formTable = new Table(script);
            formTable["_id"] = formId;
            formTable["_type"] = "form";

            formTable["show"] = (Action)(() => formHost.ShowForm(_forms.GetValueOrDefault(formId) ?? form));
            formTable["hide"] = (Action)(() => formHost.CloseForm(formId));
            // close/destroy are identical in CE — share implementation with double-call guard
            Action closeForm = () =>
            {
                if (_forms.TryRemove(formId, out _))
                    formHost.CloseForm(formId);
            };
            formTable["close"] = closeForm;
            formTable["destroy"] = closeForm;
            formTable["bringToFront"] = (Action)(() =>
            {
                if (_forms.TryGetValue(formId, out var f))
                    formHost.ShowForm(f);
            });
            formTable["setFocus"] = (Action)(() =>
            {
                // Stub — full focus management added in S5 via ILuaFormHost.BringToFront
                if (_forms.TryGetValue(formId, out var f))
                    formHost.ShowForm(f);
            });
            formTable["setCaption"] = (Action<string>)(caption =>
            {
                if (_forms.TryGetValue(formId, out var f))
                    _forms[formId] = f with { Title = caption };
            });
            formTable["setSize"] = (Action<int, int>)((w, h) =>
            {
                if (_forms.TryGetValue(formId, out var f))
                    _forms[formId] = f with { Width = w, Height = h };
            });

            // Canvas drawing API: form:getCanvas() returns a drawing context table
            formTable["getCanvas"] = (Func<DynValue>)(() =>
            {
                var canvas = new Table(script);

                // Drawing state
                var penColor = "#000000";
                var penWidth = 1;
                var brushColor = "#000000";
                var fontName = "Segoe UI";
                var fontSize = 12;

                canvas["setPen"] = (Action<string, DynValue>)((color, widthArg) =>
                {
                    penColor = color;
                    if (!widthArg.IsNil()) penWidth = (int)widthArg.Number;
                });

                canvas["setBrush"] = (Action<string>)(color => brushColor = color);

                canvas["setFont"] = (Action<string, DynValue>)((name, sizeArg) =>
                {
                    fontName = name;
                    if (!sizeArg.IsNil()) fontSize = (int)sizeArg.Number;
                });

                canvas["drawLine"] = (Action<int, int, int, int>)((x1, y1, x2, y2) =>
                    formHost.DrawLine(formId, x1, y1, x2, y2, penColor, penWidth));

                canvas["drawRect"] = (Action<int, int, int, int>)((x1, y1, x2, y2) =>
                    formHost.DrawRect(formId, x1, y1, x2, y2, penColor, false));

                canvas["fillRect"] = (Action<int, int, int, int>)((x1, y1, x2, y2) =>
                    formHost.DrawRect(formId, x1, y1, x2, y2, brushColor, true));

                canvas["drawEllipse"] = (Action<int, int, int, int>)((x1, y1, x2, y2) =>
                    formHost.DrawEllipse(formId, x1, y1, x2, y2, penColor, false));

                canvas["fillEllipse"] = (Action<int, int, int, int>)((x1, y1, x2, y2) =>
                    formHost.DrawEllipse(formId, x1, y1, x2, y2, brushColor, true));

                canvas["drawText"] = (Action<int, int, string>)((x, y, text) =>
                    formHost.DrawText(formId, x, y, text, penColor, fontName, fontSize));

                canvas["clear"] = (Action)(() => formHost.ClearCanvas(formId));

                return DynValue.NewTable(canvas);
            });

            // ── S3a: Apply CePropertyProxy to form table ──
            var fId = formId; // capture for closures
            var formProps = CePropertyProxy.CreatePropertyMap();

            formProps["Caption"] = CePropertyProxy.ReadWrite(
                () => _forms.TryGetValue(fId, out var f) ? DynValue.NewString(f.Title) : DynValue.Nil,
                v => { if (_forms.TryGetValue(fId, out var f)) _forms[fId] = f with { Title = v.String }; formHost.SetFormProperty(fId, "caption", v.String); });
            formProps["Title"] = formProps["Caption"];

            formProps["Width"] = CePropertyProxy.ReadWrite(
                () => _forms.TryGetValue(fId, out var f) ? DynValue.NewNumber(f.Width) : DynValue.Nil,
                v => { if (_forms.TryGetValue(fId, out var f)) _forms[fId] = f with { Width = CePropertyProxy.ToInt(v) }; formHost.SetFormProperty(fId, "width", CePropertyProxy.ToInt(v)); });

            formProps["Height"] = CePropertyProxy.ReadWrite(
                () => _forms.TryGetValue(fId, out var f) ? DynValue.NewNumber(f.Height) : DynValue.Nil,
                v => { if (_forms.TryGetValue(fId, out var f)) _forms[fId] = f with { Height = CePropertyProxy.ToInt(v) }; formHost.SetFormProperty(fId, "height", CePropertyProxy.ToInt(v)); });

            formProps["Left"] = CePropertyProxy.ReadWrite(
                () => { var val = formHost.GetFormProperty(fId, "left"); return val is int i ? DynValue.NewNumber(i) : val is double d ? DynValue.NewNumber(d) : DynValue.Nil; },
                v => formHost.SetFormProperty(fId, "left", CePropertyProxy.ToInt(v)));

            formProps["Top"] = CePropertyProxy.ReadWrite(
                () => { var val = formHost.GetFormProperty(fId, "top"); return val is int i ? DynValue.NewNumber(i) : val is double d ? DynValue.NewNumber(d) : DynValue.Nil; },
                v => formHost.SetFormProperty(fId, "top", CePropertyProxy.ToInt(v)));

            formProps["Position"] = CePropertyProxy.WriteOnly(v =>
            {
                var pos = v.String;
                if (pos is "poScreenCenter") formHost.SetFormProperty(fId, "position", "center");
                else if (pos is "poDesigned") formHost.SetFormProperty(fId, "position", "designed");
                else formHost.SetFormProperty(fId, "position", pos);
            });

            formProps["FormStyle"] = CePropertyProxy.WriteOnly(v =>
            {
                var style = v.String;
                if (style is "fsStayOnTop") formHost.SetFormTopMost(fId, true);
                else if (style is "fsNormal") formHost.SetFormTopMost(fId, false);
            });

            formProps["Visible"] = CePropertyProxy.ReadWrite(
                () => { var val = formHost.GetFormProperty(fId, "visible"); return val is bool b ? DynValue.NewBoolean(b) : DynValue.NewBoolean(true); },
                v => formHost.SetFormProperty(fId, "visible", CePropertyProxy.ToBool(v)));

            formProps["Color"] = CePropertyProxy.WriteOnly(v => formHost.SetFormProperty(fId, "color", v.String));

            var formEvents = CePropertyProxy.CreateEventSet();
            formEvents.Add("OnClose");

            CePropertyProxy.ApplyProxy(script, formTable, formProps, formEvents, _callbacks, $"{fId}:");

            var visible = visibleArg.IsNil() || visibleArg.Boolean;
            if (visible) formHost.ShowForm(form);

            return DynValue.NewTable(formTable);
        });

        script.Globals["createButton"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"btn_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaButtonElement(elementId, 10, 10, 100, 30) { Caption = "Button", ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            return DynValue.NewTable(CreateElementTable(script, formId, elementId, element, formHost));
        });

        script.Globals["createLabel"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"lbl_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaLabelElement(elementId, 10, 10, 150, 20) { Caption = "Label", ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            return DynValue.NewTable(CreateElementTable(script, formId, elementId, element, formHost));
        });

        script.Globals["createEdit"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"edit_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaEditElement(elementId, 10, 10, 150, 25) { ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost, props =>
            {
                // S3a: Type-specific property — Text (live read from host)
                props["Text"] = CePropertyProxy.ReadWrite(
                    () =>
                    {
                        var text = formHost.GetElementText(formId, elementId);
                        return DynValue.NewString(text ?? element.Text ?? "");
                    },
                    v => { element.Text = v.String; formHost.UpdateElement(formId, element); });
            });
            // Edit-specific: getText / setText (backward compat methods)
            elemTable["getText"] = (Func<DynValue>)(() =>
            {
                var text = formHost.GetElementText(formId, elementId);
                return text is not null ? DynValue.NewString(text) : DynValue.NewString("");
            });
            elemTable["setText"] = (Action<string>)(text =>
            {
                element.Text = text;
                formHost.UpdateElement(formId, element);
            });
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createCheckBox"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"chk_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaCheckBoxElement(elementId, 10, 10, 150, 20) { Caption = "CheckBox", ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost, props =>
            {
                // S3a: Type-specific properties — Checked / IsChecked (live read from host)
                var checkedBinding = CePropertyProxy.ReadWrite(
                    () => DynValue.NewBoolean(formHost.GetElementChecked(formId, elementId) ?? element.IsChecked),
                    v => { element.IsChecked = CePropertyProxy.ToBool(v); formHost.UpdateElement(formId, element); });
                props["Checked"] = checkedBinding;
                props["IsChecked"] = checkedBinding;
            });
            // CheckBox-specific: getChecked / setChecked (backward compat methods)
            elemTable["getChecked"] = (Func<bool>)(() =>
                formHost.GetElementChecked(formId, elementId) ?? element.IsChecked);
            elemTable["setChecked"] = (Action<bool>)(val =>
            {
                element.IsChecked = val;
                formHost.UpdateElement(formId, element);
            });
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createTimer"] = (Func<Table, DynValue, DynValue>)((parentTable, intervalArg) =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"tmr_{Interlocked.Increment(ref _nextElementId)}";
            var interval = intervalArg.IsNil() ? 1000 : (int)intervalArg.Number;
            var element = new LuaTimerElement(elementId, interval) { Enabled = true, ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost, props =>
            {
                // S3a: Type-specific properties — Interval and Enabled override for timer
                props["Interval"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewNumber(element.IntervalMs),
                    v => { element.IntervalMs = CePropertyProxy.ToInt(v); formHost.StopTimer(formId, elementId); formHost.StartTimer(formId, elementId, element.IntervalMs); });
                // Override Enabled for timer (start/stop semantics instead of generic Visible toggle)
                props["Enabled"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewBoolean(element.Enabled),
                    v => { var en = CePropertyProxy.ToBool(v); element.Enabled = en; if (en) formHost.StartTimer(formId, elementId, element.IntervalMs); else formHost.StopTimer(formId, elementId); });
            });
            elemTable["setInterval"] = (Action<int>)(ms =>
            {
                element.IntervalMs = ms;
                formHost.StopTimer(formId, elementId);
                formHost.StartTimer(formId, elementId, ms);
            });
            elemTable["setEnabled"] = (Action<bool>)(enabled =>
            {
                element.Enabled = enabled;
                if (enabled) formHost.StartTimer(formId, elementId, element.IntervalMs);
                else formHost.StopTimer(formId, elementId);
            });
            // Start the timer
            formHost.StartTimer(formId, elementId, interval);
            return DynValue.NewTable(elemTable);
        });

        // ── S2: New element types ──

        script.Globals["createMemo"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"memo_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaMemoElement(elementId, 10, 10, 200, 100) { ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost, props =>
            {
                props["Text"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewString(formHost.GetElementText(formId, elementId) ?? element.Text ?? ""),
                    v => { element.Text = v.String; formHost.UpdateElement(formId, element); });
                props["ReadOnly"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewBoolean(element.ReadOnly),
                    v => { element.ReadOnly = CePropertyProxy.ToBool(v); formHost.UpdateElement(formId, element); });
            });
            elemTable["getText"] = (Func<DynValue>)(() =>
            {
                var text = formHost.GetElementText(formId, elementId);
                return text is not null ? DynValue.NewString(text) : DynValue.NewString("");
            });
            elemTable["setText"] = (Action<string>)(text =>
            {
                element.Text = text;
                formHost.UpdateElement(formId, element);
            });
            elemTable["setReadOnly"] = (Action<bool>)(ro =>
            {
                element.ReadOnly = ro;
                formHost.UpdateElement(formId, element);
            });
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createListBox"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"lst_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaListBoxElement(elementId, 10, 10, 150, 120) { ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost, props =>
            {
                props["Items"] = CePropertyProxy.ReadOnly(() => DynValue.NewNumber(element.Items.Count));
                props["SelectedIndex"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewNumber(formHost.GetSelectedIndex(formId, elementId) ?? element.SelectedIndex),
                    v => { element.SelectedIndex = CePropertyProxy.ToInt(v); formHost.UpdateElement(formId, element); });
                props["ItemCount"] = CePropertyProxy.ReadOnly(() => DynValue.NewNumber(element.Items.Count));
            });
            elemTable["addItem"] = (Action<string>)(item =>
            {
                element.Items.Add(item);
                formHost.UpdateElement(formId, element);
            });
            elemTable["clear"] = (Action)(() =>
            {
                element.Items.Clear();
                formHost.UpdateElement(formId, element);
            });
            elemTable["getSelectedIndex"] = (Func<double>)(() =>
                formHost.GetSelectedIndex(formId, elementId) ?? element.SelectedIndex);
            elemTable["getItemCount"] = (Func<double>)(() => element.Items.Count);
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createComboBox"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"cmb_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaComboBoxElement(elementId, 10, 10, 150, 25) { ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost, props =>
            {
                props["SelectedIndex"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewNumber(formHost.GetSelectedIndex(formId, elementId) ?? element.SelectedIndex),
                    v => { element.SelectedIndex = CePropertyProxy.ToInt(v); formHost.UpdateElement(formId, element); });
                props["Items"] = CePropertyProxy.ReadOnly(() => DynValue.NewNumber(element.Items.Count));
                props["Text"] = CePropertyProxy.ReadOnly(() =>
                {
                    var idx = formHost.GetSelectedIndex(formId, elementId) ?? element.SelectedIndex;
                    return idx >= 0 && idx < element.Items.Count
                        ? DynValue.NewString(element.Items[idx])
                        : DynValue.NewString(element.Caption ?? "");
                });
            });
            elemTable["addItem"] = (Action<string>)(item =>
            {
                element.Items.Add(item);
                formHost.UpdateElement(formId, element);
            });
            elemTable["clear"] = (Action)(() =>
            {
                element.Items.Clear();
                formHost.UpdateElement(formId, element);
            });
            elemTable["getSelectedIndex"] = (Func<double>)(() =>
                formHost.GetSelectedIndex(formId, elementId) ?? element.SelectedIndex);
            elemTable["setSelectedIndex"] = (Action<int>)(idx =>
            {
                element.SelectedIndex = idx;
                formHost.UpdateElement(formId, element);
            });
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createTrackBar"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"trk_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaTrackBarElement(elementId, 10, 10, 200, 30) { ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost, props =>
            {
                props["Position"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewNumber(formHost.GetTrackBarPosition(formId, elementId) ?? element.Position),
                    v => { element.Position = CePropertyProxy.ToInt(v); formHost.UpdateElement(formId, element); });
                props["Min"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewNumber(element.Min),
                    v => { element.Min = CePropertyProxy.ToInt(v); formHost.UpdateElement(formId, element); });
                props["Max"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewNumber(element.Max),
                    v => { element.Max = CePropertyProxy.ToInt(v); formHost.UpdateElement(formId, element); });
            });
            elemTable["setMin"] = (Action<int>)(v =>
            {
                element.Min = v;
                formHost.UpdateElement(formId, element);
            });
            elemTable["setMax"] = (Action<int>)(v =>
            {
                element.Max = v;
                formHost.UpdateElement(formId, element);
            });
            elemTable["setPosition"] = (Action<int>)(v =>
            {
                element.Position = v;
                formHost.UpdateElement(formId, element);
            });
            elemTable["getPosition"] = (Func<double>)(() =>
                formHost.GetTrackBarPosition(formId, elementId) ?? element.Position);
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createProgressBar"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"prg_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaProgressBarElement(elementId, 10, 10, 200, 25) { ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost, props =>
            {
                props["Position"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewNumber(element.Position),
                    v => { element.Position = CePropertyProxy.ToInt(v); formHost.UpdateElement(formId, element); });
                props["Min"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewNumber(element.Min),
                    v => { element.Min = CePropertyProxy.ToInt(v); formHost.UpdateElement(formId, element); });
                props["Max"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewNumber(element.Max),
                    v => { element.Max = CePropertyProxy.ToInt(v); formHost.UpdateElement(formId, element); });
            });
            elemTable["setMin"] = (Action<int>)(v =>
            {
                element.Min = v;
                formHost.UpdateElement(formId, element);
            });
            elemTable["setMax"] = (Action<int>)(v =>
            {
                element.Max = v;
                formHost.UpdateElement(formId, element);
            });
            elemTable["setPosition"] = (Action<int>)(v =>
            {
                element.Position = v;
                formHost.UpdateElement(formId, element);
            });
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createImage"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"img_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaImageElement(elementId, 10, 10, 100, 100) { ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost, props =>
            {
                props["ImagePath"] = CePropertyProxy.WriteOnly(
                    v => { element.ImagePath = v.String; formHost.UpdateElement(formId, element); });
            });
            elemTable["loadFromFile"] = (Action<string>)(path =>
            {
                element.ImagePath = path;
                formHost.UpdateElement(formId, element);
            });
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createPanel"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"pnl_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaPanelElement(elementId, 10, 10, 200, 150) { ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            return DynValue.NewTable(CreateElementTable(script, formId, elementId, element, formHost));
        });

        script.Globals["createGroupBox"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"grp_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaGroupBoxElement(elementId, 10, 10, 200, 150) { Caption = "Group", ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            return DynValue.NewTable(CreateElementTable(script, formId, elementId, element, formHost));
        });

        script.Globals["createRadioGroup"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"rdg_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaRadioGroupElement(elementId, 10, 10, 150, 120) { Caption = "Options", ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost, props =>
            {
                props["SelectedIndex"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewNumber(formHost.GetSelectedIndex(formId, elementId) ?? element.SelectedIndex),
                    v => { element.SelectedIndex = CePropertyProxy.ToInt(v); formHost.UpdateElement(formId, element); });
            });
            elemTable["addItem"] = (Action<string>)(item =>
            {
                element.Items.Add(item);
                formHost.UpdateElement(formId, element);
            });
            elemTable["getSelectedIndex"] = (Func<double>)(() =>
                formHost.GetSelectedIndex(formId, elementId) ?? element.SelectedIndex);
            elemTable["setSelectedIndex"] = (Action<int>)(idx =>
            {
                element.SelectedIndex = idx;
                formHost.UpdateElement(formId, element);
            });
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createTabControl"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"tab_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaTabControlElement(elementId, 10, 10, 300, 200) { ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost, props =>
            {
                props["SelectedIndex"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewNumber(formHost.GetSelectedIndex(formId, elementId) ?? element.SelectedIndex),
                    v => { element.SelectedIndex = CePropertyProxy.ToInt(v); formHost.UpdateElement(formId, element); });
                props["TabCount"] = CePropertyProxy.ReadOnly(() => DynValue.NewNumber(element.TabNames.Count));
            });
            elemTable["addTab"] = (Action<string>)(name =>
            {
                element.TabNames.Add(name);
                formHost.UpdateElement(formId, element);
            });
            elemTable["getSelectedIndex"] = (Func<double>)(() =>
                formHost.GetSelectedIndex(formId, elementId) ?? element.SelectedIndex);
            elemTable["setSelectedIndex"] = (Action<int>)(idx =>
            {
                element.SelectedIndex = idx;
                formHost.UpdateElement(formId, element);
            });
            elemTable["getTabCount"] = (Func<double>)(() => element.TabNames.Count);
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createMainMenu"] = (Func<Table, DynValue>)(parentTable =>
        {
            var formId = parentTable.Get("_type").String == "form" ? parentTable.Get("_id").String : parentTable.Get("_formId").String;
            var elementId = $"menu_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaMenuItemElement(elementId) { Caption = "Menu" };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
            elemTable["addItem"] = (Func<string, DynValue>)(caption =>
            {
                var subId = $"mi_{Interlocked.Increment(ref _nextElementId)}";
                var subItem = new LuaMenuItemElement(subId) { Caption = caption };
                element.SubItems.Add(subItem);
                formHost.UpdateElement(formId, element);

                var subTable = CreateMenuSubItemTable(script, formId, subId, subItem);
                return DynValue.NewTable(subTable);
            });
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createPopupMenu"] = (Func<Table, DynValue>)(parentTable =>
        {
            var formId = parentTable.Get("_type").String == "form" ? parentTable.Get("_id").String : parentTable.Get("_formId").String;
            var elementId = $"popup_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaPopupMenuElement(elementId);
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
            elemTable["addItem"] = (Func<string, DynValue>)(caption =>
            {
                var subId = $"pmi_{Interlocked.Increment(ref _nextElementId)}";
                var subItem = new LuaMenuItemElement(subId) { Caption = caption };
                element.Items.Add(subItem);

                var subTable = CreateMenuSubItemTable(script, formId, subId, subItem);
                return DynValue.NewTable(subTable);
            });
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createSplitter"] = (Func<Table, DynValue>)(parentTable =>
        {
            var (formId, parentElemId) = ResolveParent(parentTable);
            var elementId = $"spl_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaSplitterElement(elementId, 10, 10, 5, 200) { ParentElementId = parentElemId };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost, props =>
            {
                props["IsVertical"] = CePropertyProxy.ReadWrite(
                    () => DynValue.NewBoolean(element.IsVertical),
                    v => { element.IsVertical = CePropertyProxy.ToBool(v); formHost.UpdateElement(formId, element); });
            });
            elemTable["setVertical"] = (Action<bool>)(v =>
            {
                element.IsVertical = v;
                formHost.UpdateElement(formId, element);
            });
            return DynValue.NewTable(elemTable);
        });

        // Wire click events back to Lua callbacks (store delegates for unsubscribe).
        // CRITICAL: script.Call() must be gated by the engine's semaphore because
        // MoonSharp Script is NOT thread-safe. These handlers fire from the WPF
        // dispatcher thread while scripts may be executing on background threads.
        _clickHandler = (fId, eId) =>
        {
            // CePropertyProxy normalizes event names to lowercase
            var key = $"{fId}:{eId}:onclick";
            if (_callbacks.TryGetValue(key, out var callback))
            {
                if (_engine is not null)
                    _engine.ExecuteGuarded(() => script.Call(callback));
                else
                    script.Call(callback); // fallback for tests without engine
            }
        };
        _timerHandler = (fId, tId) =>
        {
            // CePropertyProxy normalizes event names to lowercase
            var key = $"{fId}:{tId}:ontimer";
            if (_callbacks.TryGetValue(key, out var callback))
            {
                if (_engine is not null)
                    _engine.ExecuteGuarded(() => script.Call(callback));
                else
                    script.Call(callback); // fallback for tests without engine
            }
        };
        _changeHandler = (fId, eId, prop) =>
        {
            var key = $"{fId}:{eId}:onchange";
            if (_callbacks.TryGetValue(key, out var callback))
            {
                if (_engine is not null)
                    _engine.ExecuteGuarded(() => script.Call(callback));
                else
                    script.Call(callback); // fallback for tests without engine
            }
        };
        _closeHandler = (fId) =>
        {
            // S8: Clean up any data bindings for the closed form
            DataBindings?.RemoveBindingsForForm(fId);

            var key = $"{fId}:onclose";
            if (_callbacks.TryGetValue(key, out var callback))
            {
                if (_engine is not null)
                    _engine.ExecuteGuarded(() => script.Call(callback));
                else
                    script.Call(callback);
            }
        };
        _subscribedHost = formHost;
        formHost.ElementClicked += _clickHandler;
        formHost.TimerFired += _timerHandler;
        formHost.ElementChanged += _changeHandler;
        formHost.FormClosed += _closeHandler;
    }

    private void Unsubscribe()
    {
        if (_subscribedHost is not null)
        {
            if (_clickHandler is not null) _subscribedHost.ElementClicked -= _clickHandler;
            if (_timerHandler is not null) _subscribedHost.TimerFired -= _timerHandler;
            if (_changeHandler is not null) _subscribedHost.ElementChanged -= _changeHandler;
            if (_closeHandler is not null) _subscribedHost.FormClosed -= _closeHandler;
            _subscribedHost = null;
            _clickHandler = null;
            _timerHandler = null;
            _changeHandler = null;
            _closeHandler = null;
        }
    }

    public void Dispose() => Unsubscribe();

    /// <summary>
    /// Creates a properly proxied table for a menu sub-item (main menu or popup menu item).
    /// Supports Caption property and OnClick event via CePropertyProxy.
    /// </summary>
    private Table CreateMenuSubItemTable(Script script, string formId, string subId, LuaMenuItemElement subItem)
    {
        var subTable = new Table(script);
        subTable["_id"] = subId;
        subTable["_formId"] = formId;
        subTable["_type"] = "menuitem";
        // Backward-compat method
        subTable["setCaption"] = (Action<string>)(c => subItem.Caption = c);

        var props = CePropertyProxy.CreatePropertyMap();
        props["Caption"] = CePropertyProxy.ReadWrite(
            () => DynValue.NewString(subItem.Caption ?? ""),
            v => subItem.Caption = v.String);

        var events = CePropertyProxy.CreateEventSet();
        events.Add("OnClick");

        CePropertyProxy.ApplyProxy(script, subTable, props, events, _callbacks, $"{formId}:{subId}:");
        return subTable;
    }

    private void AddElementToForm(string formId, LuaFormElement element)
    {
        if (_forms.TryGetValue(formId, out var form))
            form.Elements.Add(element);
    }

    /// <summary>
    /// Creates the base element property map shared by all element types.
    /// Callers can add type-specific properties to the returned map before calling ApplyProxy.
    /// </summary>
    private static Dictionary<string, CePropertyProxy.PropertyBinding> CreateBaseElementProperties(
        string formId, LuaFormElement element, ILuaFormHost formHost)
    {
        void Update() => formHost.UpdateElement(formId, element);

        var props = CePropertyProxy.CreatePropertyMap();
        props["Caption"] = CePropertyProxy.ModelProp(
            () => DynValue.NewString(element.Caption ?? ""),
            v => element.Caption = v.String, Update);
        props["Left"] = CePropertyProxy.ModelProp(
            () => DynValue.NewNumber(element.X),
            v => element.X = CePropertyProxy.ToInt(v), Update);
        props["Top"] = CePropertyProxy.ModelProp(
            () => DynValue.NewNumber(element.Y),
            v => element.Y = CePropertyProxy.ToInt(v), Update);
        props["Width"] = CePropertyProxy.ModelProp(
            () => DynValue.NewNumber(element.Width),
            v => element.Width = CePropertyProxy.ToInt(v), Update);
        props["Height"] = CePropertyProxy.ModelProp(
            () => DynValue.NewNumber(element.Height),
            v => element.Height = CePropertyProxy.ToInt(v), Update);
        props["Visible"] = CePropertyProxy.ModelProp(
            () => DynValue.NewBoolean(element.Visible),
            v => element.Visible = CePropertyProxy.ToBool(v), Update);
        props["Enabled"] = CePropertyProxy.ModelProp(
            () => DynValue.NewBoolean(element.Enabled),
            v => element.Enabled = CePropertyProxy.ToBool(v), Update);
        props["Color"] = CePropertyProxy.WriteOnly(v => { element.BackColor = v.String; Update(); });
        props["BackColor"] = props["Color"];
        props["FontColor"] = CePropertyProxy.WriteOnly(v => { element.FontColor = v.String; Update(); });
        props["FontName"] = CePropertyProxy.WriteOnly(v => { element.FontName = v.String; Update(); });
        props["FontSize"] = CePropertyProxy.WriteOnly(v => { element.FontSize = CePropertyProxy.ToInt(v); Update(); });

        return props;
    }

    /// <summary>
    /// Creates the shared event set for all element types.
    /// </summary>
    private static HashSet<string> CreateBaseElementEvents()
    {
        var events = CePropertyProxy.CreateEventSet();
        events.Add("OnClick");
        events.Add("OnChange");
        events.Add("OnTimer");
        events.Add("OnClose");
        events.Add("OnKeyPress");
        return events;
    }

    private Table CreateElementTable(
        Script script, string formId, string elementId, LuaFormElement element, ILuaFormHost formHost)
    {
        return CreateElementTable(script, formId, elementId, element, formHost, null);
    }

    private Table CreateElementTable(
        Script script, string formId, string elementId, LuaFormElement element, ILuaFormHost formHost,
        Action<Dictionary<string, CePropertyProxy.PropertyBinding>>? addExtraProperties)
    {
        var table = new Table(script);
        table["_id"] = elementId;
        table["_formId"] = formId;
        table["_type"] = element.Type;

        // ── Backward-compatible methods (remain as raw table entries) ──
        table["setCaption"] = (Action<string>)(caption =>
        {
            element.Caption = caption;
            formHost.UpdateElement(formId, element);
        });

        table["setPosition"] = (Action<int, int>)((x, y) =>
        {
            element.X = x;
            element.Y = y;
            formHost.UpdateElement(formId, element);
        });

        table["setSize"] = (Action<int, int>)((w, h) =>
        {
            element.Width = w;
            element.Height = h;
            formHost.UpdateElement(formId, element);
        });

        // S2: Common styling methods
        table["setVisible"] = (Action<bool>)(v =>
        {
            element.Visible = v;
            formHost.UpdateElement(formId, element);
        });

        table["setEnabled"] = (Action<bool>)(v =>
        {
            element.Enabled = v;
            formHost.UpdateElement(formId, element);
        });

        table["setFont"] = (Action<string, DynValue, DynValue>)((name, sizeArg, colorArg) =>
        {
            element.FontName = name;
            if (!sizeArg.IsNil()) element.FontSize = (int)sizeArg.Number;
            if (!colorArg.IsNil()) element.FontColor = colorArg.String;
            formHost.UpdateElement(formId, element);
        });

        table["setColor"] = (Action<string>)(color =>
        {
            element.BackColor = color;
            formHost.UpdateElement(formId, element);
        });

        table["setFontColor"] = (Action<string>)(color =>
        {
            element.FontColor = color;
            formHost.UpdateElement(formId, element);
        });

        // ── S3a: Apply CePropertyProxy with base + type-specific element properties ──
        var props = CreateBaseElementProperties(formId, element, formHost);
        addExtraProperties?.Invoke(props);

        var events = CreateBaseElementEvents();
        CePropertyProxy.ApplyProxy(script, table, props, events, _callbacks, $"{formId}:{elementId}:");

        // S8: Add reactive data binding methods (bind/unbind) if binding engine is available
        DataBindings?.AddBindMethods(table, formId, elementId);

        return table;
    }
}
