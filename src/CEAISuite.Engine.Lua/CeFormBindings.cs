using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Registers CE form designer functions (createForm, createButton, etc.) into MoonSharp.
/// Forms are represented as Lua tables with method bindings; the actual WPF rendering
/// is delegated to <see cref="ILuaFormHost"/>.
/// </summary>
internal static class CeFormBindings
{
    private static int _nextFormId;
    private static int _nextElementId;
    private static readonly Dictionary<string, LuaFormDescriptor> _forms = new();
    private static readonly Dictionary<string, DynValue> _clickCallbacks = new();

    public static void Register(Script script, ILuaFormHost formHost)
    {
        _forms.Clear();
        _clickCallbacks.Clear();

        // createForm(visible) → returns form table
        script.Globals["createForm"] = (Func<DynValue, DynValue>)(visibleArg =>
        {
            var formId = $"form_{Interlocked.Increment(ref _nextFormId)}";
            var form = new LuaFormDescriptor(formId, "Lua Form", 400, 300, new List<LuaFormElement>());
            _forms[formId] = form;

            var formTable = new Table(script);
            formTable["_id"] = formId;
            formTable["_type"] = "form";

            formTable["show"] = (Action)(() => formHost.ShowForm(form));
            formTable["hide"] = (Action)(() => formHost.CloseForm(formId));
            formTable["close"] = (Action)(() =>
            {
                formHost.CloseForm(formId);
                _forms.Remove(formId);
            });
            formTable["setCaption"] = (Action<string>)(caption =>
            {
                var updated = form with { Title = caption };
                _forms[formId] = updated;
            });
            formTable["setSize"] = (Action<int, int>)((w, h) =>
            {
                var updated = form with { Width = w, Height = h };
                _forms[formId] = updated;
            });

            var visible = visibleArg.IsNil() || visibleArg.Boolean;
            if (visible) formHost.ShowForm(form);

            return DynValue.NewTable(formTable);
        });

        // createButton(form) → returns button table
        script.Globals["createButton"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"btn_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaButtonElement(elementId, 10, 10, 100, 30) { Caption = "Button" };

            if (_forms.TryGetValue(formId, out var form))
                form.Elements.Add(element);

            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
            return DynValue.NewTable(elemTable);
        });

        // createLabel(form) → returns label table
        script.Globals["createLabel"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"lbl_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaLabelElement(elementId, 10, 10, 150, 20) { Caption = "Label" };

            if (_forms.TryGetValue(formId, out var form))
                form.Elements.Add(element);

            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
            return DynValue.NewTable(elemTable);
        });

        // createEdit(form) → returns edit table
        script.Globals["createEdit"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"edit_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaEditElement(elementId, 10, 10, 150, 25);

            if (_forms.TryGetValue(formId, out var form))
                form.Elements.Add(element);

            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
            return DynValue.NewTable(elemTable);
        });

        // createCheckBox(form) → returns checkbox table
        script.Globals["createCheckBox"] = (Func<Table, DynValue>)(formTable =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"chk_{Interlocked.Increment(ref _nextElementId)}";
            var element = new LuaCheckBoxElement(elementId, 10, 10, 150, 20) { Caption = "CheckBox" };

            if (_forms.TryGetValue(formId, out var form))
                form.Elements.Add(element);

            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
            return DynValue.NewTable(elemTable);
        });

        // createTimer(form, intervalMs) → returns timer table
        script.Globals["createTimer"] = (Func<Table, DynValue, DynValue>)((formTable, intervalArg) =>
        {
            var formId = formTable.Get("_id").String;
            var elementId = $"tmr_{Interlocked.Increment(ref _nextElementId)}";
            var interval = intervalArg.IsNil() ? 1000 : (int)intervalArg.Number;
            var element = new LuaTimerElement(elementId, interval) { Enabled = true };

            if (_forms.TryGetValue(formId, out var form))
                form.Elements.Add(element);

            var elemTable = CreateElementTable(script, formId, elementId, element, formHost);
            elemTable["setInterval"] = (Action<int>)(ms =>
            {
                var updated = element with { IntervalMs = ms };
                formHost.UpdateElement(formId, updated);
            });
            return DynValue.NewTable(elemTable);
        });

        // Wire click events back to Lua callbacks
        formHost.ElementClicked += (formId, elementId) =>
        {
            var key = $"{formId}:{elementId}";
            if (_clickCallbacks.TryGetValue(key, out var callback))
                script.Call(callback);
        };

        formHost.TimerFired += (formId, timerId) =>
        {
            var key = $"{formId}:{timerId}";
            if (_clickCallbacks.TryGetValue(key, out var callback))
                script.Call(callback);
        };
    }

    private static Table CreateElementTable(
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
            // Records are immutable — update via the form's element list
            // For simplicity, update the Caption which triggers a re-render
            formHost.UpdateElement(formId, element);
        });

        table["setSize"] = (Action<int, int>)((w, h) =>
        {
            formHost.UpdateElement(formId, element);
        });

        // onClick callback registration
        table["__newindex"] = (Action<Table, DynValue, DynValue>)((self, key, value) =>
        {
            if (key.String == "onClick" || key.String == "onTimer")
            {
                var callbackKey = $"{formId}:{elementId}";
                _clickCallbacks[callbackKey] = value;
            }
            else
            {
                self.Set(key, value);
            }
        });

        return table;
    }
}
