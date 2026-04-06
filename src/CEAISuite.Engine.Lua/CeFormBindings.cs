using System.Collections.Concurrent;
using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Registers CE form designer functions (createForm, createButton, etc.) into MoonSharp.
/// Forms are represented as Lua tables with method bindings; the actual WPF rendering
/// is delegated to <see cref="ILuaFormHost"/>.
/// </summary>
internal sealed class CeFormBindings
{
    private int _nextFormId;
    private int _nextElementId;
    private readonly ConcurrentDictionary<string, LuaFormDescriptor> _forms = new();
    private readonly ConcurrentDictionary<string, DynValue> _callbacks = new();

    public void Register(Script script, ILuaFormHost formHost)
    {
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

        // Wire click events back to Lua callbacks
        formHost.ElementClicked += (fId, eId) =>
        {
            var key = $"{fId}:{eId}:onClick";
            if (_callbacks.TryGetValue(key, out var callback))
                script.Call(callback);
        };

        formHost.TimerFired += (fId, tId) =>
        {
            var key = $"{fId}:{tId}:onTimer";
            if (_callbacks.TryGetValue(key, out var callback))
                script.Call(callback);
        };
    }

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
