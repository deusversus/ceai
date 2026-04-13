using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// CE-compatible debugger control Lua functions: debug_setBreakpoint, debug_removeBreakpoint,
/// debug_continueFromBreakpoint, debug_getBreakpointList, debug_isDebugging.
/// </summary>
internal static class LuaDebuggerBindings
{
    public static void Register(
        Script script,
        MoonSharpLuaEngine engine,
        IBreakpointEngine breakpointEngine,
        IEngineFacade engineFacade,
        IAutoAssemblerEngine? autoAssembler)
    {
        // debug_setBreakpoint(address, [size], [type], [callback])
        // type: 0=Execute(HW), 1=Write(HW), 2=ReadWrite(HW), 3=Software
        // Returns breakpoint ID string
        script.Globals["debug_setBreakpoint"] = (Func<DynValue, DynValue, DynValue, DynValue, DynValue>)(
            (addrArg, sizeArg, typeArg, callbackArg) =>
        {
            var pid = RequireProcess(engine);
            var address = ResolveAddressArg(addrArg, pid, engineFacade, autoAssembler);
            var bpType = BreakpointType.HardwareExecute;

            if (!typeArg.IsNil())
            {
                bpType = (int)typeArg.Number switch
                {
                    0 => BreakpointType.HardwareExecute,
                    1 => BreakpointType.HardwareWrite,
                    2 => BreakpointType.HardwareReadWrite,
                    3 => BreakpointType.Software,
                    _ => BreakpointType.HardwareExecute
                };
            }

            var bp = breakpointEngine.SetBreakpointAsync(pid, address, bpType).GetAwaiter().GetResult();

            // If a Lua callback function was provided, register it
            if (!callbackArg.IsNil() && callbackArg.Type == DataType.Function)
            {
                // Store the callback function as a global with a generated name
                var callbackName = $"__bp_callback_{bp.Id}";
                script.Globals[callbackName] = callbackArg;
                engine.RegisterBreakpointCallback(callbackName);
            }

            return DynValue.NewString(bp.Id);
        });

        // debug_removeBreakpoint(addressOrId) → boolean
        script.Globals["debug_removeBreakpoint"] = (Func<DynValue, DynValue>)(arg =>
        {
            var pid = RequireProcess(engine);

            if (arg.Type == DataType.String)
            {
                // Try as breakpoint ID first
                var success = breakpointEngine.RemoveBreakpointAsync(pid, arg.String).GetAwaiter().GetResult();
                if (success) return DynValue.True;
            }

            // Try as address — find the breakpoint at that address
            var address = ResolveAddressArg(arg, pid, engineFacade, autoAssembler);
            var bps = breakpointEngine.ListBreakpointsAsync(pid).GetAwaiter().GetResult();
            var match = bps.FirstOrDefault(b => b.Address == address);
            if (match is not null)
            {
                var removed = breakpointEngine.RemoveBreakpointAsync(pid, match.Id).GetAwaiter().GetResult();
                return removed ? DynValue.True : DynValue.False;
            }

            return DynValue.False;
        });

        // debug_getBreakpointList() → table of breakpoint descriptors
        script.Globals["debug_getBreakpointList"] = (Func<DynValue>)(() =>
        {
            var pid = RequireProcess(engine);
            var bps = breakpointEngine.ListBreakpointsAsync(pid).GetAwaiter().GetResult();

            var table = new Table(script);
            for (int i = 0; i < bps.Count; i++)
            {
                var bp = bps[i];
                var entry = new Table(script);
                entry["id"] = bp.Id;
                entry["address"] = FormatAddress(bp.Address);
                entry["type"] = bp.Type.ToString();
                entry["enabled"] = bp.IsEnabled;
                entry["hitCount"] = (double)bp.HitCount;
                entry["mode"] = bp.Mode.ToString();
                table[i + 1] = DynValue.NewTable(entry);
            }

            return DynValue.NewTable(table);
        });

        // debug_isDebugging() → boolean
        script.Globals["debug_isDebugging"] = (Func<DynValue>)(() =>
        {
            var pid = engine.CurrentProcessId;
            if (!pid.HasValue) return DynValue.False;

            var bps = breakpointEngine.ListBreakpointsAsync(pid.Value).GetAwaiter().GetResult();
            return bps.Any(b => b.IsEnabled) ? DynValue.True : DynValue.False;
        });

        // debug_getBreakpointHitLog(breakpointId, [maxEntries]) → table of hit events
        script.Globals["debug_getBreakpointHitLog"] = (Func<string, DynValue, DynValue>)((bpId, maxArg) =>
        {
            var maxEntries = maxArg.IsNil() ? 50 : (int)maxArg.Number;
            var hits = breakpointEngine.GetHitLogAsync(bpId, maxEntries).GetAwaiter().GetResult();

            var table = new Table(script);
            for (int i = 0; i < hits.Count; i++)
            {
                var hit = hits[i];
                var entry = new Table(script);
                entry["address"] = FormatAddress(hit.Address);
                entry["threadId"] = (double)hit.ThreadId;

                var regs = new Table(script);
                foreach (var (name, value) in hit.RegisterSnapshot)
                    regs[name] = value;
                entry["registers"] = DynValue.NewTable(regs);

                table[i + 1] = DynValue.NewTable(entry);
            }

            return DynValue.NewTable(table);
        });
    }

    private static int RequireProcess(MoonSharpLuaEngine engine)
    {
        return engine.CurrentProcessId
            ?? throw new ScriptRuntimeException("No process attached. Call openProcess() first.");
    }

    private static nuint ResolveAddressArg(
        DynValue addrArg, int pid, IEngineFacade facade, IAutoAssemblerEngine? aa)
    {
        if (addrArg.Type == DataType.Number)
            return (nuint)(ulong)addrArg.Number;

        var expr = addrArg.CastToString()
            ?? throw new ScriptRuntimeException("Expected address number or string");
        var resolved = LuaAddressResolver.ResolveAsync(expr, pid, facade, aa).GetAwaiter().GetResult();
        return resolved ?? throw new ScriptRuntimeException($"Cannot resolve address: '{expr}'");
    }

    private static string FormatAddress(nuint address)
        => $"0x{(ulong)address:X}";
}
