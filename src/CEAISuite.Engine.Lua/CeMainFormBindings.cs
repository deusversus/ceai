using System.Collections.Concurrent;
using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Registers the <c>getMainForm()</c> global function in MoonSharp, providing Lua scripts
/// with access to the host application's panel navigation, theme, status bar, and menu extension.
/// Follows the CE property proxy pattern used by <see cref="CeFormBindings"/>.
/// </summary>
internal static class CeMainFormBindings
{
    /// <summary>
    /// Register the <c>getMainForm()</c> global in the given script.
    /// </summary>
    /// <param name="script">The MoonSharp script instance.</param>
    /// <param name="proxy">The host application proxy.</param>
    /// <param name="callbacks">Shared callback dictionary for property proxy events.</param>
    /// <param name="engine">Optional engine reference for guarded callback execution.</param>
    public static void Register(
        Script script,
        IMainFormProxy proxy,
        ConcurrentDictionary<string, DynValue> callbacks,
        MoonSharpLuaEngine? engine = null)
    {
        // Subscribe to theme changes and fire stored Lua callbacks
        proxy.ThemeChanged += themeName =>
        {
            var cbKey = "mainform:onthemechanged";
            if (callbacks.TryGetValue(cbKey, out var cb) && cb.Type == DataType.Function)
            {
                if (engine is not null)
                {
                    engine.ExecuteGuarded(() =>
                    {
                        try
                        {
                            script.Call(cb, DynValue.NewString(themeName));
                        }
                        catch (OutOfMemoryException) { throw; }
                        catch (StackOverflowException) { throw; }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[CeMainFormBindings] OnThemeChanged callback failed: {ex.Message}");
                        }
                    });
                }
            }
        };

        script.Globals["getMainForm"] = (Func<DynValue>)(() =>
        {
            var table = new Table(script);

            // ── Panel methods ──
            table["showPanel"] = (Action<string>)(id => proxy.SetPanelVisible(id, true));
            table["hidePanel"] = (Action<string>)(id => proxy.SetPanelVisible(id, false));
            table["isPanelVisible"] = (Func<string, bool>)(id => proxy.IsPanelVisible(id));

            table["getPanelIds"] = (Func<DynValue>)(() =>
            {
                var ids = proxy.GetPanelIds();
                var t = new Table(script);
                for (int i = 0; i < ids.Count; i++)
                    t[i + 1] = ids[i];
                return DynValue.NewTable(t);
            });

            // ── Navigation methods ──
            table["navigateDisassembler"] = (Action<string>)(addr => proxy.NavigateDisassembler(addr));
            table["navigateMemoryBrowser"] = (Action<string>)(addr => proxy.NavigateMemoryBrowser(addr));

            // ── Status bar methods ──
            table["setStatus"] = (Action<string>)(text => proxy.SetStatus(text));
            table["getStatus"] = (Func<string>)(() => proxy.GetStatus());

            // ── Theme methods ──
            table["isDarkTheme"] = (Func<bool>)(() => proxy.IsDarkTheme());
            table["getThemeName"] = (Func<string>)(() => proxy.GetThemeName());

            // ── Menu extension ──
            table["getMenu"] = (Func<DynValue>)(() =>
            {
                var menuTable = new Table(script);

                menuTable["addItem"] = (Func<string, DynValue, DynValue>)((caption, callback) =>
                {
                    var menuId = proxy.AddMenuItem(caption, () =>
                    {
                        if (engine is not null && callback.Type == DataType.Function)
                        {
                            engine.ExecuteGuarded(() =>
                            {
                                try
                                {
                                    script.Call(callback);
                                }
                                catch (OutOfMemoryException) { throw; }
                                catch (StackOverflowException) { throw; }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[CeMainFormBindings] Menu callback failed: {ex.Message}");
                                }
                            });
                        }
                    });

                    var itemTable = new Table(script);
                    itemTable["_id"] = menuId;
                    itemTable["remove"] = (Action)(() => proxy.RemoveMenuItem(menuId));
                    return DynValue.NewTable(itemTable);
                });

                return DynValue.NewTable(menuTable);
            });

            // ── CE property proxy for Delphi-style property access ──
            var props = CePropertyProxy.CreatePropertyMap();
            props["Caption"] = CePropertyProxy.ReadOnly(() => DynValue.NewString("CE AI Suite"));

            var events = CePropertyProxy.CreateEventSet();
            events.Add("OnThemeChanged");

            CePropertyProxy.ApplyProxy(script, table, props, events, callbacks, "mainform:");

            return DynValue.NewTable(table);
        });
    }
}
