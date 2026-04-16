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
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
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

        // fillMemory(address, size, byteValue) — fill region with a single byte value
        script.Globals["fillMemory"] = (Action<DynValue, double, double>)((addrArg, size, byteVal) =>
        {
            var pid = RequireProcess(engine);
            var addr = ResolveAddressArg(addrArg, pid, engineFacade, autoAssembler);
            var byteCount = (int)size;
            if (byteCount < 1 || byteCount > 0x100000)
                throw new ScriptRuntimeException("fillMemory: size must be 1..1048576");
            var fillByte = (byte)byteVal;
            var buffer = new byte[byteCount];
            System.Array.Fill(buffer, fillByte);
            engineFacade.WriteBytesAsync(pid, addr, buffer).GetAwaiter().GetResult();
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
        => LuaBindingHelpers.RequireProcess(engine);

    private static nuint ResolveAddressArg(
        DynValue addrArg, int pid, IEngineFacade facade, IAutoAssemblerEngine? aa)
        => LuaBindingHelpers.ResolveAddressArg(addrArg, pid, facade, aa);
}
