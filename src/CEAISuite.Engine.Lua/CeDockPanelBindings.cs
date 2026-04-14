using System.Collections.Concurrent;
using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Registers the <c>createDockPanel(title, position)</c> Lua global.
/// Dock panels reuse the form infrastructure (<see cref="LuaFormDescriptor"/>) for element
/// storage, but the host renders them as tool windows rather than floating forms.
/// </summary>
internal sealed class CeDockPanelBindings
{
    private int _nextPanelId;
    private const int MaxDockPanels = 20;
    private readonly ConcurrentDictionary<string, LuaDockPanelDescriptor> _dockPanels = new();

    /// <summary>
    /// Register the createDockPanel global into the given script.
    /// </summary>
    /// <param name="script">MoonSharp script instance.</param>
    /// <param name="formHost">WPF host that renders panels.</param>
    /// <param name="formBindings">Shared form bindings (provides _forms, _callbacks, element ID generator).</param>
    /// <param name="engine">Lua engine for guarded execution of callbacks.</param>
    public void Register(Script script, ILuaFormHost formHost, CeFormBindings formBindings, MoonSharpLuaEngine? engine)
    {
        var forms = formBindings.Forms;
        var callbacks = formBindings.Callbacks;

        script.Globals["createDockPanel"] = (Func<DynValue, DynValue, DynValue>)((titleArg, positionArg) =>
        {
            if (_dockPanels.Count >= MaxDockPanels)
                throw new ScriptRuntimeException($"Maximum dock panel limit ({MaxDockPanels}) reached");

            var title = titleArg.IsNilOrNan() ? "Lua Panel" : titleArg.String;
            var position = positionArg.IsNilOrNan() ? "bottom" : positionArg.String;

            // Validate position
            if (position is not ("bottom" or "left" or "right" or "document"))
                position = "bottom";

            var panelId = $"luapanel_{Interlocked.Increment(ref _nextPanelId)}";

            // Create the dock panel descriptor
            var dockDesc = new LuaDockPanelDescriptor(panelId, title, position, new List<LuaFormElement>());
            _dockPanels[panelId] = dockDesc;

            // Also register as a LuaFormDescriptor so createButton/createLabel/etc. can add elements to it
            var formDesc = new LuaFormDescriptor(panelId, title, 400, 300, dockDesc.Elements);
            forms[panelId] = formDesc;

            // Build the panel proxy table
            var panelTable = new Table(script);
            panelTable["_id"] = panelId;
            panelTable["_type"] = "dockpanel";
            panelTable["_formId"] = panelId;

            // Methods
            panelTable["close"] = (Action)(() =>
            {
                if (_dockPanels.TryRemove(panelId, out _))
                {
                    forms.TryRemove(panelId, out _);
                    formHost.CloseDockPanel(panelId);
                }
            });

            panelTable["show"] = (Action)(() =>
            {
                // Re-create/show the dock panel with current elements
                if (_dockPanels.TryGetValue(panelId, out var desc))
                    formHost.CreateDockPanel(desc);
            });

            // Properties via CePropertyProxy
            var props = CePropertyProxy.CreatePropertyMap();

            props["Title"] = CePropertyProxy.ReadWrite(
                () => _dockPanels.TryGetValue(panelId, out var d)
                    ? DynValue.NewString(d.Title)
                    : DynValue.Nil,
                v =>
                {
                    if (_dockPanels.TryGetValue(panelId, out var d))
                    {
                        var updated = d with { Title = v.String };
                        _dockPanels[panelId] = updated;
                        // Update the window title via form property
                        formHost.SetFormProperty(panelId, "caption", v.String);
                    }
                });

            props["Visible"] = CePropertyProxy.ReadWrite(
                () =>
                {
                    var val = formHost.GetFormProperty(panelId, "visible");
                    return val is bool b ? DynValue.NewBoolean(b) : DynValue.NewBoolean(false);
                },
                v => formHost.SetFormProperty(panelId, "visible", CePropertyProxy.ToBool(v)));

            // Events
            var events = CePropertyProxy.CreateEventSet();
            events.Add("OnClose");

            CePropertyProxy.ApplyProxy(script, panelTable, props, events, callbacks, $"{panelId}:");

            // Show the dock panel immediately
            formHost.CreateDockPanel(dockDesc);

            return DynValue.NewTable(panelTable);
        });
    }

    /// <summary>Close all dock panels (called during engine reset).</summary>
    public void CloseAll(ILuaFormHost formHost, ConcurrentDictionary<string, LuaFormDescriptor> forms)
    {
        foreach (var (id, _) in _dockPanels)
        {
            forms.TryRemove(id, out _);
            formHost.CloseDockPanel(id);
        }
        _dockPanels.Clear();
    }
}
