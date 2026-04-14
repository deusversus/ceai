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

    private MoonSharpLuaEngine? _engine;

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
            formTable["close"] = (Action)(() =>
            {
                formHost.CloseForm(formId);
                _forms.TryRemove(formId, out _);
            });
            formTable["destroy"] = (Action)(() =>
            {
                formHost.CloseForm(formId);
                _forms.TryRemove(formId, out _);
            });
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

            var visible = visibleArg.IsNil() || visibleArg.Boolean;
            if (visible) formHost.ShowForm(form);

            return DynValue.NewTable(formTable);
        });

        script.Globals["createButton"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"btn_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaButtonElement(elementId, 10, 10, 100, 30) { Caption = "Button" };
            AddElementToForm(formId, element);
            return DynValue.NewTable(CreateElementTable(script, formId, elementId, element, formHost));
        });

        script.Globals["createLabel"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"lbl_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaLabelElement(elementId, 10, 10, 150, 20) { Caption = "Label" };
            AddElementToForm(formId, element);
            return DynValue.NewTable(CreateElementTable(script, formId, elementId, element, formHost));
        });

        script.Globals["createEdit"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"edit_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaEditElement(elementId, 10, 10, 150, 25);
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
            // Edit-specific: getText / setText
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

        script.Globals["createCheckBox"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"chk_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaCheckBoxElement(elementId, 10, 10, 150, 20) { Caption = "CheckBox" };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
            // CheckBox-specific: getChecked / setChecked
            elemTable["getChecked"] = (Func<bool>)(() =>
                formHost.GetElementChecked(formId, elementId) ?? element.IsChecked);
            elemTable["setChecked"] = (Action<bool>)(val =>
            {
                element.IsChecked = val;
                formHost.UpdateElement(formId, element);
            });
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createTimer"] = (Func<Table, DynValue, DynValue>)((formTable, intervalArg) =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"tmr_{Interlocked.Increment(ref _nextElementId)}";
            var interval = intervalArg.IsNil() ? 1000 : (int)intervalArg.Number;
            var element = new LuaTimerElement(elementId, interval) { Enabled = true };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
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

        script.Globals["createMemo"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"memo_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaMemoElement(elementId, 10, 10, 200, 100);
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
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

        script.Globals["createListBox"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"lst_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaListBoxElement(elementId, 10, 10, 150, 120);
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
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

        script.Globals["createComboBox"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"cmb_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaComboBoxElement(elementId, 10, 10, 150, 25);
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
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

        script.Globals["createTrackBar"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"trk_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaTrackBarElement(elementId, 10, 10, 200, 30);
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
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

        script.Globals["createProgressBar"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"prg_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaProgressBarElement(elementId, 10, 10, 200, 25);
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
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

        script.Globals["createImage"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"img_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaImageElement(elementId, 10, 10, 100, 100);
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
            elemTable["loadFromFile"] = (Action<string>)(path =>
            {
                element.ImagePath = path;
                formHost.UpdateElement(formId, element);
            });
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createPanel"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"pnl_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaPanelElement(elementId, 10, 10, 200, 150);
            AddElementToForm(formId, element);
            return DynValue.NewTable(CreateElementTable(script, formId, elementId, element, formHost));
        });

        script.Globals["createGroupBox"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"grp_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaGroupBoxElement(elementId, 10, 10, 200, 150) { Caption = "Group" };
            AddElementToForm(formId, element);
            return DynValue.NewTable(CreateElementTable(script, formId, elementId, element, formHost));
        });

        script.Globals["createRadioGroup"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"rdg_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaRadioGroupElement(elementId, 10, 10, 150, 120) { Caption = "Options" };
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
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

        script.Globals["createTabControl"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"tab_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaTabControlElement(elementId, 10, 10, 300, 200);
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
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

        script.Globals["createMainMenu"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
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

                var subTable = new Table(script);
                subTable["_id"] = subId;
                subTable["_formId"] = formId;
                subTable["setCaption"] = (Action<string>)(c => subItem.Caption = c);
                // Callback via metatable
                var cbs = _callbacks;
                subTable.MetaTable = new Table(script);
                subTable.MetaTable["__newindex"] = (Action<Table, DynValue, DynValue>)((_, key, value) =>
                {
                    if (key.String is "onClick")
                        cbs[$"{formId}:{subId}:onClick"] = value;
                    else
                        subTable.Set(key.String, value);
                });
                return DynValue.NewTable(subTable);
            });
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createPopupMenu"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"popup_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaPopupMenuElement(elementId);
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
            elemTable["addItem"] = (Func<string, DynValue>)(caption =>
            {
                var subId = $"pmi_{Interlocked.Increment(ref _nextElementId)}";
                var subItem = new LuaMenuItemElement(subId) { Caption = caption };
                element.Items.Add(subItem);

                var subTable = new Table(script);
                subTable["_id"] = subId;
                subTable["_formId"] = formId;
                subTable["setCaption"] = (Action<string>)(c => subItem.Caption = c);
                var cbs = _callbacks;
                subTable.MetaTable = new Table(script);
                subTable.MetaTable["__newindex"] = (Action<Table, DynValue, DynValue>)((_, key, value) =>
                {
                    if (key.String is "onClick")
                        cbs[$"{formId}:{subId}:onClick"] = value;
                    else
                        subTable.Set(key.String, value);
                });
                return DynValue.NewTable(subTable);
            });
            return DynValue.NewTable(elemTable);
        });

        script.Globals["createSplitter"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"spl_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaSplitterElement(elementId, 10, 10, 5, 200);
            AddElementToForm(formId, element);
            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
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
            var key = $"{fId}:{eId}:onClick";
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
            var key = $"{fId}:{tId}:onTimer";
            if (_callbacks.TryGetValue(key, out var callback))
            {
                if (_engine is not null)
                    _engine.ExecuteGuarded(() => script.Call(callback));
                else
                    script.Call(callback); // fallback for tests without engine
            }
        };
        _subscribedHost = formHost;
        formHost.ElementClicked += _clickHandler;
        formHost.TimerFired += _timerHandler;
    }

    private void Unsubscribe()
    {
        if (_subscribedHost is not null)
        {
            if (_clickHandler is not null) _subscribedHost.ElementClicked -= _clickHandler;
            if (_timerHandler is not null) _subscribedHost.TimerFired -= _timerHandler;
            _subscribedHost = null;
            _clickHandler = null;
            _timerHandler = null;
        }
    }

    public void Dispose() => Unsubscribe();

    private void AddElementToForm(string formId, LuaFormElement element)
    {
        if (_forms.TryGetValue(formId, out var form))
            form.Elements.Add(element);
    }

    private Table CreateElementTable(
        Script script, string formId, string elementId, LuaFormElement element, ILuaFormHost formHost)
    {
        var table = new Table(script);
        table["_id"] = elementId;
        table["_formId"] = formId;
        table["_type"] = element.Type;

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

        // Callback registration via property assignment: element.onClick = function() ... end
        var callbacks = _callbacks;
        table.MetaTable = new Table(script);
        table.MetaTable["__newindex"] = (Action<Table, DynValue, DynValue>)((_, key, value) =>
        {
            var keyStr = key.String;
            if (keyStr is "onClick" or "onTimer")
            {
                callbacks[$"{formId}:{elementId}:{keyStr}"] = value;
            }
            else
            {
                table.Set(key.String, value);
            }
        });

        return table;
    }
}
