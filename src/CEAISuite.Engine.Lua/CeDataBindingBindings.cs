using System.Collections.Concurrent;
using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Reactive data binding engine for Lua form elements.
/// Binds element properties (Caption, Text, Visible, Enabled) to address table record
/// properties (Value, Description, Address, Active). Auto-updates on each refresh cycle.
/// </summary>
internal sealed class CeDataBindingBindings : IDisposable
{
    private sealed record BindingEntry(
        string FormId, string ElementId, string ElementProperty,
        string RecordId, string RecordProperty,
        string? LastKnownValue);

    /// <summary>Maximum number of active bindings to prevent runaway scripts.</summary>
    internal const int MaxBindings = 200;

    // Registry: keyed by "{formId}:{elementId}:{elementProperty}"
    // Guarded by _lock — accessed from UI thread (refresh timer) and Lua thread (bind/unbind).
    private readonly Dictionary<string, BindingEntry> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private ILuaDataBindingHost? _host;
    private ILuaAddressListProvider? _provider;
    private ILuaFormHost? _formHost;
    private ConcurrentDictionary<string, LuaFormDescriptor>? _forms;

    /// <summary>Number of active bindings. Exposed for testing.</summary>
    internal int ActiveBindingCount { get { lock (_lock) { return _bindings.Count; } } }

    public void Register(
        ILuaDataBindingHost host,
        ILuaAddressListProvider provider,
        ILuaFormHost formHost,
        ConcurrentDictionary<string, LuaFormDescriptor> forms)
    {
        // Unsubscribe from previous host to prevent stacking
        if (_host is not null)
            _host.RefreshCycleCompleted -= OnRefreshCycle;

        _host = host;
        _provider = provider;
        _formHost = formHost;
        _forms = forms;

        host.RefreshCycleCompleted += OnRefreshCycle;
    }

    /// <summary>
    /// Add bind/unbind methods to an element's Lua table.
    /// Called from <see cref="CeFormBindings.CreateElementTable"/> after the table is built.
    /// </summary>
    public void AddBindMethods(Table elemTable, string formId, string elementId)
    {
        elemTable["bind"] = (Action<string, Table, string>)((elemProp, recordTable, recordProp) =>
        {
            lock (_lock)
            {
                if (_bindings.Count >= MaxBindings)
                    throw new ScriptRuntimeException($"Maximum binding limit ({MaxBindings}) reached");

                var recordIdDyn = recordTable.Get("_id");
                if (recordIdDyn.IsNil())
                    throw new ScriptRuntimeException("bind() requires a record table with an _id field (from addresslist.getRecordById etc.)");

                var recordId = recordIdDyn.String;
                var key = $"{formId}:{elementId}:{elemProp.ToLowerInvariant()}";
                _bindings[key] = new BindingEntry(formId, elementId, elemProp, recordId, recordProp, null);
            }
        });

        elemTable["unbind"] = (Action<string>)((elemProp) =>
        {
            lock (_lock)
            {
                var key = $"{formId}:{elementId}:{elemProp.ToLowerInvariant()}";
                _bindings.Remove(key);
            }
        });
    }

    private void OnRefreshCycle()
    {
        if (_provider is null || _formHost is null || _forms is null) return;

        KeyValuePair<string, BindingEntry>[] snapshot;
        lock (_lock)
        {
            snapshot = _bindings.ToArray();
        }

        foreach (var (key, binding) in snapshot)
        {
            // Read current record value
            var currentValue = binding.RecordProperty.ToLowerInvariant() switch
            {
                "value" => _provider.GetValue(binding.RecordId),
                "description" => _provider.GetDescription(binding.RecordId),
                "address" => _provider.GetAddress(binding.RecordId),
                "active" => _provider.GetActive(binding.RecordId).ToString(),
                _ => null
            };

            if (currentValue == binding.LastKnownValue) continue;

            // Value changed — update the binding's last known value
            lock (_lock)
            {
                _bindings[key] = binding with { LastKnownValue = currentValue };
            }

            // Find the element in the form and update the bound property
            if (!_forms.TryGetValue(binding.FormId, out var form)) continue;
            var element = form.Elements.FirstOrDefault(e => e.Id == binding.ElementId);
            if (element is null) continue;

            // Map element property name to element field update
            switch (binding.ElementProperty.ToLowerInvariant())
            {
                case "caption":
                    element.Caption = currentValue ?? "";
                    break;
                case "text" when element is LuaEditElement edit:
                    edit.Text = currentValue ?? "";
                    break;
                case "text" when element is LuaMemoElement memo:
                    memo.Text = currentValue ?? "";
                    break;
                case "visible":
                    element.Visible = string.Equals(currentValue, "true", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(currentValue, "True", StringComparison.Ordinal);
                    break;
                case "enabled":
                    element.Enabled = string.Equals(currentValue, "true", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(currentValue, "True", StringComparison.Ordinal);
                    break;
                default:
                    // Default fallback: update Caption
                    element.Caption = currentValue ?? "";
                    break;
            }

            _formHost.UpdateElement(binding.FormId, element);
        }
    }

    /// <summary>Remove all bindings associated with a form (called on form close/destroy).</summary>
    public void RemoveBindingsForForm(string formId)
    {
        lock (_lock)
        {
            var prefix = formId + ":";
            foreach (var key in _bindings.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _bindings.Remove(key);
        }
    }

    public void Dispose()
    {
        if (_host is not null)
            _host.RefreshCycleCompleted -= OnRefreshCycle;
        lock (_lock)
        {
            _bindings.Clear();
        }
        _host = null;
        _provider = null;
        _formHost = null;
        _forms = null;
    }
}
