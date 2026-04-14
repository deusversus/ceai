using System.Collections.Concurrent;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Universal CE-compatible property proxy factory. Applies __index and __newindex metatables
/// to MoonSharp tables so that CE Delphi-style property access works:
///   element.Caption = "text"   →  __newindex fires setter
///   local x = element.Caption  →  __index fires getter
///   element.OnClick = func     →  __newindex stores callback
///   element.myData = 42        →  __newindex falls through to raw table
///
/// Design invariant: known property names NEVER land on the raw table. This ensures
/// __newindex fires on every write (not just the first). Methods placed on the raw table
/// before ApplyProxy are still accessible — __index checks raw table first.
/// </summary>
internal static class CePropertyProxy
{
    /// <summary>
    /// A property binding with optional live getter and setter.
    /// </summary>
    /// <param name="Getter">Called by __index when the property is read. Returns the live value.</param>
    /// <param name="Setter">Called by __newindex when the property is written. Performs side effects (host update, model write).</param>
    public sealed record PropertyBinding(
        Func<Table, DynValue>? Getter,
        Action<Table, DynValue>? Setter);

    /// <summary>
    /// Apply CE-compatible __index and __newindex metatables to a table.
    /// Methods already on the raw table (like setCaption, show, getValue) remain accessible
    /// because __index checks the raw table first — MoonSharp/Lua standard behavior.
    /// </summary>
    /// <param name="script">The MoonSharp script instance (needed for metatable creation).</param>
    /// <param name="target">The table to enhance with property proxy metatables.</param>
    /// <param name="properties">Property name → getter/setter map. Case-insensitive lookups.</param>
    /// <param name="eventNames">Property names that are callback events (OnClick, OnChange, etc.). Case-insensitive.</param>
    /// <param name="callbackRegistry">Shared callback storage. Keys are "{prefix}{eventName}".</param>
    /// <param name="callbackPrefix">Prefix for callback keys, e.g. "form_1:" or "form_1:btn_1:".</param>
    public static void ApplyProxy(
        Script script,
        Table target,
        Dictionary<string, PropertyBinding> properties,
        HashSet<string> eventNames,
        ConcurrentDictionary<string, DynValue> callbackRegistry,
        string callbackPrefix)
    {
        var meta = target.MetaTable ?? new Table(script);

        // __index: fires when a key is NOT found on the raw table.
        // 1. Check property map → call getter
        // 2. Return nil
        meta["__index"] = (Func<Table, DynValue, DynValue>)((self, key) =>
        {
            if (key.Type != DataType.String)
                return DynValue.Nil;

            var keyStr = key.String;

            // Check property map (case-insensitive)
            if (properties.TryGetValue(keyStr, out var binding) && binding.Getter is not null)
            {
                try
                {
                    return binding.Getter(self);
                }
                catch
                {
                    return DynValue.Nil;
                }
            }

            // Check if it's an event name — return the stored callback (or nil)
            if (eventNames.Contains(keyStr))
            {
                var cbKey = $"{callbackPrefix}{NormalizeEventName(keyStr)}";
                return callbackRegistry.TryGetValue(cbKey, out var cb) ? cb : DynValue.Nil;
            }

            return DynValue.Nil;
        });

        // __newindex: fires when a key is NOT found on the raw table.
        // For known properties/events, we NEVER rawset — this ensures __newindex fires every time.
        // For unknown keys, we rawset so they persist on the table (CE custom user data pattern).
        meta["__newindex"] = (Action<Table, DynValue, DynValue>)((self, key, value) =>
        {
            if (key.Type != DataType.String)
            {
                // Non-string keys: fall through to raw table
                self.Set(key, value);
                return;
            }

            var keyStr = key.String;

            // 1. Check event names (case-insensitive) → store in callback registry, do NOT rawset
            if (eventNames.Contains(keyStr))
            {
                var cbKey = $"{callbackPrefix}{NormalizeEventName(keyStr)}";
                if (value.IsNil())
                    callbackRegistry.TryRemove(cbKey, out _);
                else
                    callbackRegistry[cbKey] = value;
                return;
            }

            // 2. Check property map with setter → call setter, do NOT rawset
            if (properties.TryGetValue(keyStr, out var binding) && binding.Setter is not null)
            {
                try
                {
                    binding.Setter(self, value);
                }
                catch
                {
                    // Setter failed — don't crash the script, just ignore
                }
                return;
            }

            // 3. Not a known property or event → rawset on the table (custom user data)
            self.Set(key.String, value);
        });

        target.MetaTable = meta;
    }

    // ── Helper builders for common property patterns ──

    /// <summary>Read-only property: getter returns live value, setter falls through to raw table.</summary>
    public static PropertyBinding ReadOnly(Func<DynValue> getter) =>
        new(_ => getter(), null);

    /// <summary>Read-write property: getter returns live value, setter performs side effects.</summary>
    public static PropertyBinding ReadWrite(Func<DynValue> getter, Action<DynValue> setter) =>
        new(_ => getter(), (_, v) => setter(v));

    /// <summary>Write-only property: no getter (returns nil via __index), setter performs side effects.</summary>
    public static PropertyBinding WriteOnly(Action<DynValue> setter) =>
        new(null, (_, v) => setter(v));

    /// <summary>
    /// Read-write property backed by a model field + host update callback.
    /// </summary>
    public static PropertyBinding ModelProp(
        Func<DynValue> getter,
        Action<DynValue> modelSetter,
        Action? hostUpdate = null) =>
        new(
            _ => getter(),
            (_, v) =>
            {
                modelSetter(v);
                hostUpdate?.Invoke();
            });

    /// <summary>
    /// Normalize event names to a consistent format for callback registry keys.
    /// CE scripts use various casings: OnClick, onClick, ONCLICK — all should map to the same key.
    /// </summary>
    private static string NormalizeEventName(string eventName) =>
        eventName.ToLowerInvariant();

    /// <summary>
    /// Create a case-insensitive property dictionary.
    /// </summary>
    public static Dictionary<string, PropertyBinding> CreatePropertyMap() =>
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Create a case-insensitive event name set.
    /// </summary>
    public static HashSet<string> CreateEventSet() =>
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Convert a DynValue to a double, handling both Number and String types.
    /// CE scripts often pass numbers as strings.
    /// </summary>
    public static double ToDouble(DynValue value)
    {
        if (value.Type == DataType.Number)
            return value.Number;
        if (value.Type == DataType.String &&
            double.TryParse(value.String, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        return 0;
    }

    /// <summary>
    /// Convert a DynValue to an int.
    /// </summary>
    public static int ToInt(DynValue value) => (int)ToDouble(value);

    /// <summary>
    /// Convert a DynValue to a bool. Handles booleans, numbers (0=false), and strings ("true"/"false").
    /// </summary>
    public static bool ToBool(DynValue value)
    {
        if (value.Type == DataType.Boolean) return value.Boolean;
        if (value.Type == DataType.Number) return value.Number != 0;
        if (value.Type == DataType.String) return !string.IsNullOrEmpty(value.String) &&
            !value.String.Equals("false", StringComparison.OrdinalIgnoreCase) &&
            value.String != "0";
        return !value.IsNil();
    }
}
