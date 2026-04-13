using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// CE-compatible memory management Lua functions: allocateMemory, deAllocateMemory,
/// setMemoryProtection, virtualQueryEx, getRegionInfo, copyMemory.
/// </summary>
internal static class LuaMemoryManagementBindings
{
    public static void Register(
        Script script,
        MoonSharpLuaEngine engine,
        IMemoryProtectionEngine memProtEngine,
        IEngineFacade engineFacade,
        IAutoAssemblerEngine? autoAssembler)
    {
        // allocateMemory(size) → number (address)
        script.Globals["allocateMemory"] = (Func<double, DynValue>)(size =>
        {
            var pid = RequireProcess(engine);
            var alloc = memProtEngine.AllocateAsync(pid, (long)size).GetAwaiter().GetResult();
            return DynValue.NewNumber((double)(ulong)alloc.BaseAddress);
        });

        // deAllocateMemory(address) → boolean
        script.Globals["deAllocateMemory"] = (Func<DynValue, DynValue>)(addrArg =>
        {
            var pid = RequireProcess(engine);
            var address = ResolveAddressArg(addrArg, pid, engineFacade, autoAssembler);
            var success = memProtEngine.FreeAsync(pid, address).GetAwaiter().GetResult();
            return success ? DynValue.True : DynValue.False;
        });

        // setMemoryProtection(address, size, protection) → boolean
        // protection: numeric value matching Win32 PAGE_* constants
        script.Globals["setMemoryProtection"] = (Func<DynValue, double, double, DynValue>)((addrArg, size, prot) =>
        {
            var pid = RequireProcess(engine);
            var address = ResolveAddressArg(addrArg, pid, engineFacade, autoAssembler);
            var protection = (MemoryProtection)(int)prot;
            try
            {
                memProtEngine.ChangeProtectionAsync(pid, address, (long)size, protection)
                    .GetAwaiter().GetResult();
                return DynValue.True;
            }
            catch
            {
                return DynValue.False;
            }
        });

        // virtualQueryEx(address) → table {baseAddress, regionSize, protection, isReadable, isWritable, isExecutable}
        script.Globals["virtualQueryEx"] = (Func<DynValue, DynValue>)(addrArg =>
        {
            var pid = RequireProcess(engine);
            var address = ResolveAddressArg(addrArg, pid, engineFacade, autoAssembler);
            var region = memProtEngine.QueryProtectionAsync(pid, address).GetAwaiter().GetResult();
            return RegionToTable(script, region);
        });

        // getRegionInfo(address) → same as virtualQueryEx (CE alias)
        script.Globals["getRegionInfo"] = script.Globals["virtualQueryEx"];

        // copyMemory(dest, source, size) — copy within target process
        script.Globals["copyMemory"] = (Action<DynValue, DynValue, double>)((destArg, srcArg, size) =>
        {
            var pid = RequireProcess(engine);
            var dest = ResolveAddressArg(destArg, pid, engineFacade, autoAssembler);
            var src = ResolveAddressArg(srcArg, pid, engineFacade, autoAssembler);
            var byteCount = (int)size;

            // Read from source, write to dest
            var data = engineFacade.ReadMemoryAsync(pid, src, byteCount).GetAwaiter().GetResult();
            var bytes = data.Bytes is byte[] arr ? arr : data.Bytes.ToArray();
            engineFacade.WriteBytesAsync(pid, dest, bytes).GetAwaiter().GetResult();
        });
    }

    private static DynValue RegionToTable(Script script, MemoryRegionDescriptor region)
    {
        var table = new Table(script);
        table["baseAddress"] = (double)(ulong)region.BaseAddress;
        table["regionSize"] = (double)region.RegionSize;
        table["isReadable"] = region.IsReadable;
        table["isWritable"] = region.IsWritable;
        table["isExecutable"] = region.IsExecutable;

        // Reconstruct approximate PAGE_* protection constant for CE compatibility
        var prot = 0x01; // PAGE_NOACCESS
        if (region.IsExecutable && region.IsWritable) prot = 0x40; // PAGE_EXECUTE_READWRITE
        else if (region.IsExecutable && region.IsReadable) prot = 0x20; // PAGE_EXECUTE_READ
        else if (region.IsExecutable) prot = 0x10; // PAGE_EXECUTE
        else if (region.IsWritable) prot = 0x04; // PAGE_READWRITE
        else if (region.IsReadable) prot = 0x02; // PAGE_READONLY
        table["protection"] = (double)prot;

        return DynValue.NewTable(table);
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
}
