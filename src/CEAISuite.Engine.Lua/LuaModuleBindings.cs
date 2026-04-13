using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// CE-compatible module and symbol Lua functions: enumModules, getModuleSize,
/// getNameFromAddress, readPointer, writePointer, writeString.
/// </summary>
internal static class LuaModuleBindings
{
    public static void Register(
        Script script,
        MoonSharpLuaEngine engine,
        IEngineFacade engineFacade,
        IScanEngine? scanEngine,
        IAutoAssemblerEngine? autoAssembler,
        ISymbolEngine? symbolEngine = null)
    {
        // reinitializeSymbolhandler() — reload symbols for all modules
        script.Globals["reinitializeSymbolhandler"] = (Action)(() =>
        {
            if (symbolEngine is null)
                throw new ScriptRuntimeException("reinitializeSymbolhandler requires symbol engine");

            var pid = RequireProcess(engine);
            var attachment = engineFacade.AttachAsync(pid).GetAwaiter().GetResult();
            symbolEngine.Cleanup(pid);
            foreach (var mod in attachment.Modules)
            {
                symbolEngine.LoadSymbolsForModuleAsync(pid, mod.Name, mod.BaseAddress, mod.SizeBytes)
                    .GetAwaiter().GetResult();
            }
        });

        // enumModules() → table of {name, base, size}
        script.Globals["enumModules"] = (Func<DynValue>)(() =>
        {
            var pid = RequireProcess(engine);
            var inspection = engineFacade.AttachAsync(pid).GetAwaiter().GetResult();
            var modules = inspection.Modules;

            var table = new Table(script);
            int idx = 1;
            foreach (var mod in modules)
            {
                var entry = new Table(script);
                entry["name"] = mod.Name;
                entry["base"] = (double)(ulong)mod.BaseAddress;
                entry["size"] = (double)mod.SizeBytes;
                table[idx++] = DynValue.NewTable(entry);
            }
            return DynValue.NewTable(table);
        });

        // getModuleSize(moduleName) → number or nil
        script.Globals["getModuleSize"] = (Func<string, DynValue>)(moduleName =>
        {
            var pid = RequireProcess(engine);
            var inspection = engineFacade.AttachAsync(pid).GetAwaiter().GetResult();
            var mod = inspection.Modules.FirstOrDefault(m =>
                m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));

            return mod is not null
                ? DynValue.NewNumber((double)mod.SizeBytes)
                : DynValue.Nil;
        });

        // getNameFromAddress(address) → string "module+0xOffset" or "0xAddress"
        script.Globals["getNameFromAddress"] = (Func<DynValue, string>)(addrArg =>
        {
            var pid = RequireProcess(engine);
            nuint address;

            if (addrArg.Type == DataType.Number)
                address = (nuint)(ulong)addrArg.Number;
            else
            {
                var expr = addrArg.CastToString()
                    ?? throw new ScriptRuntimeException("getNameFromAddress: expected address");
                var resolved = LuaAddressResolver.ResolveAsync(expr, pid, engineFacade, autoAssembler)
                    .GetAwaiter().GetResult();
                address = resolved ?? throw new ScriptRuntimeException($"Cannot resolve address: '{expr}'");
            }

            // Find containing module
            var inspection = engineFacade.AttachAsync(pid).GetAwaiter().GetResult();
            foreach (var mod in inspection.Modules)
            {
                var modBase = (ulong)mod.BaseAddress;
                var modEnd = modBase + (ulong)mod.SizeBytes;
                if ((ulong)address >= modBase && (ulong)address < modEnd)
                {
                    var offset = (ulong)address - modBase;
                    return $"{mod.Name}+0x{offset:X}";
                }
            }

            return $"0x{(ulong)address:X}";
        });

        // readPointer(address) → number (reads 4 bytes on 32-bit, 8 bytes on 64-bit)
        // In our x64 context, always reads 8 bytes
        script.Globals["readPointer"] = (Func<string, DynValue>)(addr =>
        {
            var pid = RequireProcess(engine);
            var resolved = ResolveAddress(addr, pid, engineFacade, autoAssembler);
            var result = engineFacade.ReadValueAsync(pid, resolved, MemoryDataType.Pointer).GetAwaiter().GetResult();
            var displayVal = result.DisplayValue;
            // Strip "0x" prefix if present for hex parsing
            if (displayVal.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                displayVal = displayVal[2..];
            if (ulong.TryParse(displayVal, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var val))
                return DynValue.NewNumber((double)val);
            if (ulong.TryParse(result.DisplayValue, out val))
                return DynValue.NewNumber((double)val);
            return DynValue.Nil;
        });

        // writePointer(address, value) — writes 8-byte pointer
        script.Globals["writePointer"] = (Action<string, DynValue>)((addr, val) =>
        {
            var pid = RequireProcess(engine);
            var resolved = ResolveAddress(addr, pid, engineFacade, autoAssembler);
            var valueStr = ((ulong)val.Number).ToString(System.Globalization.CultureInfo.InvariantCulture);
            engineFacade.WriteValueAsync(pid, resolved, MemoryDataType.Int64, valueStr).GetAwaiter().GetResult();
        });

        // writeString(address, text, [wide]) — writes null-terminated string
        script.Globals["writeString"] = (Action<string, string, DynValue>)((addr, text, wideArg) =>
        {
            var pid = RequireProcess(engine);
            var resolved = ResolveAddress(addr, pid, engineFacade, autoAssembler);
            var isWide = !wideArg.IsNil() && wideArg.Boolean;

            byte[] bytes;
            if (isWide)
            {
                var encoded = System.Text.Encoding.Unicode.GetBytes(text);
                bytes = new byte[encoded.Length + 2]; // null terminator (2 bytes for UTF-16)
                encoded.CopyTo(bytes, 0);
            }
            else
            {
                var encoded = System.Text.Encoding.ASCII.GetBytes(text);
                bytes = new byte[encoded.Length + 1]; // null terminator
                encoded.CopyTo(bytes, 0);
            }

            engineFacade.WriteBytesAsync(pid, resolved, bytes).GetAwaiter().GetResult();
        });

        // executeCode(address) — create a remote thread at the given address
        // WARNING: This is an inherently dangerous CE feature — it executes arbitrary code
        // in the target process at the specified address. There is no sandboxing or validation
        // of what the code at that address does. This matches CE's behavior by design.
        script.Globals["executeCode"] = (Action<DynValue>)(addrArg =>
        {
            var pid = RequireProcess(engine);
            var address = LuaBindingHelpers.ResolveAddressArg(addrArg, pid, engineFacade, autoAssembler);

            // Delegate to AA engine's createthread infrastructure via a minimal AA script
            if (autoAssembler is not null)
            {
                var script2 = $"[ENABLE]\ncreatethread(0x{(ulong)address:X})\n[DISABLE]\n";
                autoAssembler.EnableAsync(pid, script2).GetAwaiter().GetResult();
            }
            else
            {
                throw new ScriptRuntimeException("executeCode requires Auto Assembler engine");
            }
        });

        // injectDLL(path) — load a DLL into the target process
        script.Globals["injectDLL"] = (Action<string>)(dllPath =>
        {
            // Validate DLL path before interpolating into AA script.
            // Order matters: reject obviously bad inputs first, then check filesystem.
            if (string.IsNullOrWhiteSpace(dllPath))
                throw new ScriptRuntimeException("injectDLL: path cannot be empty");
            if (dllPath.StartsWith("\\\\", StringComparison.Ordinal))
                throw new ScriptRuntimeException("injectDLL: UNC/network paths are not allowed");
            if (!dllPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new ScriptRuntimeException("injectDLL: file must be a .dll");
            if (!File.Exists(dllPath))
                throw new ScriptRuntimeException($"injectDLL: file not found: {dllPath}");
            var fileInfo = new FileInfo(dllPath);
            if (fileInfo.LinkTarget is not null)
                throw new ScriptRuntimeException("injectDLL: symbolic links are not allowed");

            var pid = RequireProcess(engine);

            if (autoAssembler is not null)
            {
                var script2 = $"[ENABLE]\nloadlibrary({dllPath})\n[DISABLE]\n";
                autoAssembler.EnableAsync(pid, script2).GetAwaiter().GetResult();
            }
            else
            {
                throw new ScriptRuntimeException("injectDLL requires Auto Assembler engine");
            }
        });

        // enumMemoryRegions() → table of regions
        if (scanEngine is not null)
        {
            script.Globals["enumMemoryRegions"] = (Func<DynValue>)(() =>
            {
                var pid = RequireProcess(engine);
                var regions = scanEngine.EnumerateRegionsAsync(pid).GetAwaiter().GetResult();

                var table = new Table(script);
                int idx = 1;
                foreach (var region in regions)
                {
                    var entry = new Table(script);
                    entry["baseAddress"] = (double)(ulong)region.BaseAddress;
                    entry["regionSize"] = (double)region.RegionSize;
                    entry["isReadable"] = region.IsReadable;
                    entry["isWritable"] = region.IsWritable;
                    entry["isExecutable"] = region.IsExecutable;
                    table[idx++] = DynValue.NewTable(entry);
                }
                return DynValue.NewTable(table);
            });
        }
    }

    private static int RequireProcess(MoonSharpLuaEngine engine)
        => LuaBindingHelpers.RequireProcess(engine);

    private static nuint ResolveAddress(
        string addrExpr, int pid, IEngineFacade facade, IAutoAssemblerEngine? aa)
        => LuaBindingHelpers.ResolveAddress(addrExpr, pid, facade, aa);
}
