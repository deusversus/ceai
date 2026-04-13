using System.Runtime.InteropServices;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Additional CE-compatible utility Lua functions that don't fit into other binding categories:
/// getCEVersion, messageDialog, beep, getScreenWidth/Height, md5, getTickCount64.
/// </summary>
internal static class LuaUtilityBindings
{
    public static void Register(Script script)
    {
        // getCEVersion() → string — returns CE AI Suite version
        script.Globals["getCEVersion"] = (Func<string>)(() =>
        {
            var asm = typeof(LuaUtilityBindings).Assembly;
            var ver = asm.GetName().Version;
            return ver is not null ? $"CE AI Suite {ver.Major}.{ver.Minor}.{ver.Build}" : "CE AI Suite";
        });

        // getCheatEngineFileVersion() → string — alias
        script.Globals["getCheatEngineFileVersion"] = script.Globals["getCEVersion"];

        // getOperatingSystem() → string
        script.Globals["getOperatingSystem"] = (Func<string>)(() =>
            RuntimeInformation.OSDescription);

        // getScreenWidth() → number (uses P/Invoke to avoid WPF dependency)
        script.Globals["getScreenWidth"] = (Func<double>)(() =>
            GetSystemMetrics(0)); // SM_CXSCREEN

        // getScreenHeight() → number
        script.Globals["getScreenHeight"] = (Func<double>)(() =>
            GetSystemMetrics(1)); // SM_CYSCREEN

        // beep() — play system beep
        script.Globals["beep"] = (Action)(() => Console.Beep());

        // messageDialog(text, type, buttons) → number
        // Simplified version that maps to showMessage behavior
        script.Globals["messageDialog"] = (Func<string, DynValue, DynValue, double>)((text, typeArg, buttonsArg) =>
        {
            // In headless mode, just return 0 (OK)
            // The full implementation would use ILuaFormHost.ShowMessageDialog with button options
            return 0;
        });

        // md5(text) → string — MD5 hash of a string (CE API compatibility, not for security)
#pragma warning disable CA5351 // CE Lua API compatibility — MD5 is not used for security purposes
        script.Globals["md5"] = (Func<string, string>)(text =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var hash = System.Security.Cryptography.MD5.HashData(bytes);
            return Convert.ToHexStringLower(hash);
        });
#pragma warning restore CA5351

        // getTickCount64() → number — alias for getTickCount with explicit 64-bit name
        script.Globals["getTickCount64"] = (Func<double>)(() => (double)Environment.TickCount64);

        // os_clock() → number — returns process time in seconds (safe alternative to blocked os.clock)
        script.Globals["os_clock"] = (Func<double>)(() =>
            System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds);

        // encodeFunction(func) → string — stub (CE serializes Lua functions; we return a placeholder)
        script.Globals["encodeFunction"] = (Func<DynValue, string>)(func =>
        {
            if (func.Type != DataType.Function)
                throw new ScriptRuntimeException("encodeFunction: expected a function");
            return "[encoded-function-stub]";
        });

        // createRef(value) → number and getRef(id) → value and destroyRef(id)
        var refs = new Dictionary<int, DynValue>();
        var nextRefId = 0;

        script.Globals["createRef"] = (Func<DynValue, double>)(value =>
        {
            var id = Interlocked.Increment(ref nextRefId);
            refs[id] = value;
            return id;
        });

        script.Globals["getRef"] = (Func<double, DynValue>)(id =>
            refs.TryGetValue((int)id, out var val) ? val : DynValue.Nil);

        script.Globals["destroyRef"] = (Action<double>)(id =>
            refs.Remove((int)id));
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
