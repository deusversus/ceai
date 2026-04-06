using System.Globalization;
using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Registers CE-compatible Lua API functions (readInteger, writeFloat, getAddress, etc.)
/// into a MoonSharp Script instance, delegating to the engine abstraction layer.
/// </summary>
internal static class CeApiBindings
{
    /// <summary>Register all CE API functions into the Lua script.</summary>
    public static void Register(
        Script script,
        MoonSharpLuaEngine engine,
        IEngineFacade engineFacade,
        IAutoAssemblerEngine? autoAssembler,
        ILuaFormHost? formHost = null)
    {
        RegisterMemoryRead(script, engine, engineFacade, autoAssembler);
        RegisterMemoryWrite(script, engine, engineFacade, autoAssembler);
        RegisterProcessFunctions(script, engine, engineFacade);
        RegisterAddressFunctions(script, engine, engineFacade, autoAssembler);
        RegisterAutoAssemblerFunctions(script, engine, engineFacade, autoAssembler);
        RegisterUtilityFunctions(script, engine, formHost);
    }

    // ── Memory Read ──

    private static void RegisterMemoryRead(
        Script script, MoonSharpLuaEngine engine, IEngineFacade facade, IAutoAssemblerEngine? aa)
    {
        script.Globals["readByte"] = (Func<string, DynValue>)(addr =>
            ReadTyped(addr, MemoryDataType.Byte, engine, facade, aa));

        script.Globals["readInteger"] = (Func<string, DynValue>)(addr =>
            ReadTyped(addr, MemoryDataType.Int32, engine, facade, aa));

        script.Globals["readSmallInteger"] = (Func<string, DynValue>)(addr =>
            ReadTyped(addr, MemoryDataType.Int16, engine, facade, aa));

        script.Globals["readQword"] = (Func<string, DynValue>)(addr =>
            ReadTyped(addr, MemoryDataType.Int64, engine, facade, aa));

        script.Globals["readFloat"] = (Func<string, DynValue>)(addr =>
            ReadTyped(addr, MemoryDataType.Float, engine, facade, aa));

        script.Globals["readDouble"] = (Func<string, DynValue>)(addr =>
            ReadTyped(addr, MemoryDataType.Double, engine, facade, aa));

        script.Globals["readBytes"] = (Func<string, int, DynValue>)((addr, count) =>
        {
            var pid = RequireProcess(engine);
            var resolved = ResolveAddress(addr, pid, facade, aa);
            var result = facade.ReadMemoryAsync(pid, resolved, count).GetAwaiter().GetResult();
            var table = new Table(script);
            for (int i = 0; i < result.Bytes.Count; i++)
                table[i + 1] = (double)result.Bytes[i]; // Lua tables are 1-indexed
            return DynValue.NewTable(table);
        });

        script.Globals["readString"] = (Func<DynValue, DynValue, DynValue, DynValue>)((addrArg, maxLenArg, wideArg) =>
        {
            var pid = RequireProcess(engine);
            var addr = addrArg.CastToString();
            if (addr is null) throw new ScriptRuntimeException("readString: address is nil");
            var resolved = ResolveAddress(addr, pid, facade, aa);

            var maxLen = maxLenArg.IsNil() ? 256 : (int)maxLenArg.Number;
            var result = facade.ReadMemoryAsync(pid, resolved, maxLen).GetAwaiter().GetResult();
            var bytes = result.Bytes is byte[] arr ? arr : result.Bytes.ToArray();

            var isWide = !wideArg.IsNil() && wideArg.Boolean;
            string text;
            if (isWide)
            {
                text = System.Text.Encoding.Unicode.GetString(bytes);
            }
            else
            {
                text = System.Text.Encoding.ASCII.GetString(bytes);
            }

            // Trim at null terminator
            var nullIdx = text.IndexOf('\0');
            if (nullIdx >= 0) text = text[..nullIdx];

            return DynValue.NewString(text);
        });
    }

    // ── Memory Write ──

    private static void RegisterMemoryWrite(
        Script script, MoonSharpLuaEngine engine, IEngineFacade facade, IAutoAssemblerEngine? aa)
    {
        script.Globals["writeByte"] = (Action<string, DynValue>)((addr, val) =>
            WriteTyped(addr, val, MemoryDataType.Byte, engine, facade, aa));

        script.Globals["writeInteger"] = (Action<string, DynValue>)((addr, val) =>
            WriteTyped(addr, val, MemoryDataType.Int32, engine, facade, aa));

        script.Globals["writeSmallInteger"] = (Action<string, DynValue>)((addr, val) =>
            WriteTyped(addr, val, MemoryDataType.Int16, engine, facade, aa));

        script.Globals["writeQword"] = (Action<string, DynValue>)((addr, val) =>
            WriteTyped(addr, val, MemoryDataType.Int64, engine, facade, aa));

        script.Globals["writeFloat"] = (Action<string, DynValue>)((addr, val) =>
            WriteTyped(addr, val, MemoryDataType.Float, engine, facade, aa));

        script.Globals["writeDouble"] = (Action<string, DynValue>)((addr, val) =>
            WriteTyped(addr, val, MemoryDataType.Double, engine, facade, aa));

        script.Globals["writeBytes"] = (Action<string, Table>)((addr, bytesTable) =>
        {
            var pid = RequireProcess(engine);
            var resolved = ResolveAddress(addr, pid, facade, aa);

            var bytes = new byte[bytesTable.Length];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)bytesTable.Get(i + 1).Number;

            facade.WriteBytesAsync(pid, resolved, bytes).GetAwaiter().GetResult();
        });
    }

    // ── Process Functions ──

    private static void RegisterProcessFunctions(
        Script script, MoonSharpLuaEngine engine, IEngineFacade facade)
    {
        script.Globals["openProcess"] = (Func<DynValue, DynValue>)(arg =>
        {
            if (arg.Type == DataType.Number)
            {
                var pid = (int)arg.Number;
                facade.AttachAsync(pid).GetAwaiter().GetResult();
                return DynValue.NewNumber(pid);
            }
            else if (arg.Type == DataType.String)
            {
                var name = arg.String;
                var processes = facade.ListProcessesAsync().GetAwaiter().GetResult();
                var match = processes.FirstOrDefault(p =>
                    p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                    throw new ScriptRuntimeException($"Process '{name}' not found");
                facade.AttachAsync(match.Id).GetAwaiter().GetResult();
                return DynValue.NewNumber(match.Id);
            }
            throw new ScriptRuntimeException("openProcess: expected process name or PID");
        });

        script.Globals["getOpenedProcessID"] = (Func<DynValue>)(() =>
        {
            var pid = engine.CurrentProcessId ?? facade.AttachedProcessId;
            return pid.HasValue ? DynValue.NewNumber(pid.Value) : DynValue.Nil;
        });

        // Alias
        script.Globals["getProcessId"] = script.Globals["getOpenedProcessID"];

        script.Globals["getProcessList"] = (Func<DynValue>)(() =>
        {
            var script2 = script;
            var processes = facade.ListProcessesAsync().GetAwaiter().GetResult();
            var table = new Table(script2);
            int idx = 1;
            foreach (var p in processes)
            {
                var entry = new Table(script2);
                entry["id"] = (double)p.Id;
                entry["name"] = p.Name;
                table[idx++] = DynValue.NewTable(entry);
            }
            return DynValue.NewTable(table);
        });
    }

    // ── Address Functions ──

    private static void RegisterAddressFunctions(
        Script script, MoonSharpLuaEngine engine, IEngineFacade facade, IAutoAssemblerEngine? aa)
    {
        script.Globals["getAddress"] = (Func<string, DynValue>)(expression =>
        {
            var pid = RequireProcess(engine);
            var resolved = LuaAddressResolver.ResolveAsync(expression, pid, facade, aa).GetAwaiter().GetResult();
            return resolved.HasValue
                ? DynValue.NewNumber((double)(ulong)resolved.Value)
                : DynValue.Nil;
        });

        script.Globals["getModuleBaseAddress"] = (Func<string, DynValue>)(moduleName =>
        {
            var pid = RequireProcess(engine);
            var baseAddr = LuaAddressResolver.FindModuleBaseAsync(moduleName, pid, facade, CancellationToken.None)
                .GetAwaiter().GetResult();
            return baseAddr.HasValue
                ? DynValue.NewNumber((double)(ulong)baseAddr.Value)
                : DynValue.Nil;
        });

        // Symbol registration — syncs both Lua globals and AA engine symbol table
        script.Globals["registerSymbol"] = (Action<string, DynValue>)((name, addrVal) =>
        {
            nuint addr;
            if (addrVal.Type == DataType.Number)
                addr = (nuint)(ulong)addrVal.Number;
            else if (addrVal.Type == DataType.String && LuaAddressResolver.TryParseHex(addrVal.String, out var parsed))
                addr = (nuint)parsed;
            else
                throw new ScriptRuntimeException($"registerSymbol: '{name}' requires an address number or hex string, got {addrVal.Type}");

            script.Globals[name] = DynValue.NewNumber((double)(ulong)addr);
            aa?.RegisterSymbol(name, addr);
        });

        script.Globals["unregisterSymbol"] = (Action<string>)(name =>
        {
            script.Globals[name] = DynValue.Nil;
            aa?.UnregisterSymbol(name);
        });
    }

    // ── Auto Assembler Functions ──

    private static void RegisterAutoAssemblerFunctions(
        Script script, MoonSharpLuaEngine engine, IEngineFacade facade, IAutoAssemblerEngine? aa)
    {
        if (aa is null) return;

        script.Globals["autoAssemble"] = (Func<string, DynValue>)(aaScript =>
        {
            var pid = RequireProcess(engine);
            var result = aa.EnableAsync(pid, aaScript).GetAwaiter().GetResult();
            return result.Success ? DynValue.True : DynValue.False;
        });

        script.Globals["autoAssembleCheck"] = (Func<string, DynValue>)(aaScript =>
        {
            var parseResult = aa.Parse(aaScript);
            return parseResult.IsValid ? DynValue.True : DynValue.False;
        });
    }

    // ── Utility Functions ──

    private static void RegisterUtilityFunctions(Script script, MoonSharpLuaEngine engine, ILuaFormHost? formHost)
    {
        script.Globals["sleep"] = (Action<int>)(ms =>
        {
            // Cap at 10 seconds to prevent hangs
            var capped = Math.Min(ms, 10_000);
            Thread.Sleep(capped);
        });

        script.Globals["getTickCount"] = (Func<double>)(() => (double)Environment.TickCount64);

        script.Globals["showMessage"] = (Action<string>)(msg =>
        {
            if (formHost is not null)
                formHost.ShowMessageDialog(msg, "Lua Script");
            else
                script.Call(script.Globals.Get("print"), DynValue.NewString(msg));
        });

        script.Globals["inputQuery"] = (Func<string, string, DynValue, DynValue>)((title, prompt, defaultArg) =>
        {
            if (formHost is null)
                throw new ScriptRuntimeException("inputQuery requires a form host (GUI mode).");
            var defaultVal = defaultArg.IsNil() ? "" : defaultArg.String;
            var result = formHost.ShowInputDialog(title, prompt, defaultVal);
            return result is not null ? DynValue.NewString(result) : DynValue.Nil;
        });

        script.Globals["stringToHex"] = (Func<string, string>)(str =>
        {
            return string.Join(" ", System.Text.Encoding.ASCII.GetBytes(str).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        });

        script.Globals["hexToString"] = (Func<string, string>)(hex =>
        {
            var bytes = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToArray();
            return System.Text.Encoding.ASCII.GetString(bytes);
        });
    }

    // ── Helpers ──

    private static int RequireProcess(MoonSharpLuaEngine engine)
    {
        return engine.CurrentProcessId
            ?? throw new ScriptRuntimeException("No process attached. Call openProcess() first.");
    }

    private static nuint ResolveAddress(
        string addrExpr, int pid, IEngineFacade facade, IAutoAssemblerEngine? aa)
    {
        var resolved = LuaAddressResolver.ResolveAsync(addrExpr, pid, facade, aa).GetAwaiter().GetResult();
        return resolved ?? throw new ScriptRuntimeException($"Cannot resolve address: '{addrExpr}'");
    }

    private static DynValue ReadTyped(
        string addr, MemoryDataType dataType,
        MoonSharpLuaEngine engine, IEngineFacade facade, IAutoAssemblerEngine? aa)
    {
        var pid = RequireProcess(engine);
        var resolved = ResolveAddress(addr, pid, facade, aa);
        var result = facade.ReadValueAsync(pid, resolved, dataType).GetAwaiter().GetResult();

        return dataType switch
        {
            MemoryDataType.Float => DynValue.NewNumber(double.Parse(result.DisplayValue, CultureInfo.InvariantCulture)),
            MemoryDataType.Double => DynValue.NewNumber(double.Parse(result.DisplayValue, CultureInfo.InvariantCulture)),
            _ => DynValue.NewNumber(double.Parse(result.DisplayValue, CultureInfo.InvariantCulture))
        };
    }

    private static void WriteTyped(
        string addr, DynValue val, MemoryDataType dataType,
        MoonSharpLuaEngine engine, IEngineFacade facade, IAutoAssemblerEngine? aa)
    {
        var pid = RequireProcess(engine);
        var resolved = ResolveAddress(addr, pid, facade, aa);

        string valueStr = dataType switch
        {
            MemoryDataType.Float => val.Number.ToString(CultureInfo.InvariantCulture),
            MemoryDataType.Double => val.Number.ToString(CultureInfo.InvariantCulture),
            _ => ((long)val.Number).ToString(CultureInfo.InvariantCulture)
        };

        facade.WriteValueAsync(pid, resolved, dataType, valueStr).GetAwaiter().GetResult();
    }
}
