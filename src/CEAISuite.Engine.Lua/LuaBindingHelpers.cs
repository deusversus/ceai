using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Shared helper methods used across multiple Lua binding classes.
/// Eliminates duplication of RequireProcess, ResolveAddress, FormatAddress.
/// </summary>
internal static class LuaBindingHelpers
{
    /// <summary>Returns the attached process ID or throws a descriptive error.</summary>
    public static int RequireProcess(MoonSharpLuaEngine engine)
    {
        return engine.CurrentProcessId
            ?? throw new ScriptRuntimeException("No process attached. Call openProcess() first.");
    }

    /// <summary>Resolves a string address expression or throws.</summary>
    public static nuint ResolveAddress(
        string addrExpr, int pid, IEngineFacade facade, IAutoAssemblerEngine? aa)
    {
        var resolved = LuaAddressResolver.ResolveAsync(addrExpr, pid, facade, aa).GetAwaiter().GetResult();
        return resolved ?? throw new ScriptRuntimeException($"Cannot resolve address: '{addrExpr}'");
    }

    /// <summary>Resolves a DynValue (number or string) to an address.</summary>
    public static nuint ResolveAddressArg(
        DynValue addrArg, int pid, IEngineFacade facade, IAutoAssemblerEngine? aa)
    {
        if (addrArg.Type == DataType.Number)
            return (nuint)(ulong)addrArg.Number;

        var expr = addrArg.CastToString()
            ?? throw new ScriptRuntimeException("Expected address number or string");
        return ResolveAddress(expr, pid, facade, aa);
    }

    /// <summary>Formats an address as a hex string with 0x prefix.</summary>
    public static string FormatAddress(nuint address)
        => $"0x{(ulong)address:X}";
}
