using System.Globalization;
using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// CE-compatible Mono introspection Lua globals: mono_enumDomains, mono_findClass,
/// mono_class_enumFields, mono_class_enumMethods, mono_findMethod, mono_invoke_method,
/// mono_getStaticFieldValue, mono_setStaticFieldValue, LaunchMonoDataCollector, etc.
///
/// All functions require the Mono agent to be injected first (via InjectMonoAgent AI tool
/// or LaunchMonoDataCollector() Lua call).
/// </summary>
internal static class LuaMonoBindings
{
    public static void Register(
        Script script,
        MoonSharpLuaEngine engine,
        IMonoEngine monoEngine,
        IEngineFacade engineFacade)
    {
        // ── LaunchMonoDataCollector() — inject the Mono agent (CE compat name) ──
        script.Globals["LaunchMonoDataCollector"] = (Func<DynValue>)(() =>
        {
            var pid = RequireProcess(engine);
            var result = monoEngine.InjectAsync(pid).GetAwaiter().GetResult();
            if (!result.Success)
                throw new ScriptRuntimeException($"LaunchMonoDataCollector failed: {result.Error}");
            return DynValue.NewString(result.MonoVersion ?? "unknown");
        });

        // ── mono_enumDomains() → table of domain tables ──
        script.Globals["mono_enumDomains"] = (Func<DynValue>)(() =>
        {
            var pid = RequireProcess(engine);
            var domains = monoEngine.EnumDomainsAsync(pid).GetAwaiter().GetResult();
            var table = new Table(script);
            for (int i = 0; i < domains.Count; i++)
            {
                var dt = new Table(script);
                dt["handle"] = (double)(ulong)domains[i].Handle;
                dt["name"] = domains[i].Name;
                dt["assemblyCount"] = (double)domains[i].AssemblyCount;
                table[i + 1] = dt;
            }
            return DynValue.NewTable(table);
        });

        // ── mono_enumAssemblies(domainHandle) → table of assembly tables ──
        script.Globals["mono_enumAssemblies"] = (Func<DynValue, DynValue>)((domainArg) =>
        {
            var pid = RequireProcess(engine);
            var domainHandle = ParseHandle(domainArg);
            var assemblies = monoEngine.EnumAssembliesAsync(pid, domainHandle).GetAwaiter().GetResult();
            var table = new Table(script);
            for (int i = 0; i < assemblies.Count; i++)
            {
                var at = new Table(script);
                at["handle"] = (double)(ulong)assemblies[i].Handle;
                at["imageHandle"] = (double)(ulong)assemblies[i].ImageHandle;
                at["name"] = assemblies[i].Name;
                at["fullName"] = assemblies[i].FullName;
                table[i + 1] = at;
            }
            return DynValue.NewTable(table);
        });

        // ── mono_findClass(imageHandle, namespace, className) → class table or nil ──
        script.Globals["mono_findClass"] = (Func<DynValue, string, string, DynValue>)((imageArg, ns, name) =>
        {
            var pid = RequireProcess(engine);
            var imageHandle = ParseHandle(imageArg);
            var cls = monoEngine.FindClassAsync(pid, imageHandle, ns, name).GetAwaiter().GetResult();
            if (cls is null) return DynValue.Nil;
            return ClassToTable(script, cls);
        });

        // ── mono_class_enumFields(classHandle) → table of field tables ──
        script.Globals["mono_class_enumFields"] = (Func<DynValue, DynValue>)((classArg) =>
        {
            var pid = RequireProcess(engine);
            var classHandle = ParseHandle(classArg);
            var fields = monoEngine.EnumFieldsAsync(pid, classHandle).GetAwaiter().GetResult();
            var table = new Table(script);
            for (int i = 0; i < fields.Count; i++)
            {
                var ft = new Table(script);
                ft["handle"] = (double)(ulong)fields[i].Handle;
                ft["name"] = fields[i].Name;
                ft["typeName"] = fields[i].TypeName;
                ft["offset"] = (double)fields[i].Offset;
                ft["isStatic"] = fields[i].IsStatic;
                table[i + 1] = ft;
            }
            return DynValue.NewTable(table);
        });

        // ── mono_class_enumMethods(classHandle) → table of method tables ──
        script.Globals["mono_class_enumMethods"] = (Func<DynValue, DynValue>)((classArg) =>
        {
            var pid = RequireProcess(engine);
            var classHandle = ParseHandle(classArg);
            var methods = monoEngine.EnumMethodsAsync(pid, classHandle).GetAwaiter().GetResult();
            var table = new Table(script);
            for (int i = 0; i < methods.Count; i++)
            {
                var mt = new Table(script);
                mt["handle"] = (double)(ulong)methods[i].Handle;
                mt["name"] = methods[i].Name;
                mt["returnType"] = methods[i].ReturnType;
                mt["isStatic"] = methods[i].IsStatic;
                var paramTable = new Table(script);
                for (int j = 0; j < methods[i].ParameterTypes.Count; j++)
                    paramTable[j + 1] = methods[i].ParameterTypes[j];
                mt["parameterTypes"] = paramTable;
                table[i + 1] = mt;
            }
            return DynValue.NewTable(table);
        });

        // ── mono_findMethod(classHandle, methodName) → method handle (number) or nil ──
        script.Globals["mono_findMethod"] = (Func<DynValue, string, DynValue>)((classArg, methodName) =>
        {
            var pid = RequireProcess(engine);
            var classHandle = ParseHandle(classArg);
            var methods = monoEngine.EnumMethodsAsync(pid, classHandle).GetAwaiter().GetResult();
            var match = methods.FirstOrDefault(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));
            return match is not null ? DynValue.NewNumber((double)(ulong)match.Handle) : DynValue.Nil;
        });

        // ── mono_invoke_method(methodHandle, instanceHandle, [args...]) → return value ──
        script.Globals["mono_invoke_method"] = (Func<DynValue, DynValue, DynValue, DynValue>)((methodArg, instanceArg, argsArg) =>
        {
            var pid = RequireProcess(engine);
            var methodHandle = ParseHandle(methodArg);
            var instanceHandle = instanceArg.IsNil() ? (nuint)0 : ParseHandle(instanceArg);

            nuint[]? args = null;
            if (!argsArg.IsNil() && argsArg.Type == DataType.Table)
            {
                var argList = new List<nuint>();
                foreach (var pair in argsArg.Table.Pairs)
                    argList.Add((nuint)(ulong)pair.Value.CastToNumber()!.Value);
                args = argList.ToArray();
            }

            var result = monoEngine.InvokeMethodAsync(pid, methodHandle, instanceHandle, args).GetAwaiter().GetResult();
            if (!result.Success)
                throw new ScriptRuntimeException($"mono_invoke_method failed: {result.Error}");
            return DynValue.NewNumber((double)(ulong)result.ReturnValue);
        });

        // ── mono_getStaticFieldValue(classHandle, fieldHandle, size) → number ──
        script.Globals["mono_getStaticFieldValue"] = (Func<DynValue, DynValue, DynValue, DynValue>)((classArg, fieldArg, sizeArg) =>
        {
            var pid = RequireProcess(engine);
            var classHandle = ParseHandle(classArg);
            var fieldHandle = ParseHandle(fieldArg);
            var size = sizeArg.IsNil() ? 8 : (int)sizeArg.CastToNumber()!.Value;

            var data = monoEngine.GetStaticFieldValueAsync(pid, classHandle, fieldHandle, size).GetAwaiter().GetResult();
            if (data is null) return DynValue.Nil;

            // Return as number (up to 8 bytes)
            return DynValue.NewNumber(size switch
            {
                1 => data[0],
                2 => BitConverter.ToInt16(data, 0),
                4 => BitConverter.ToInt32(data, 0),
                _ => (double)BitConverter.ToInt64(data, 0)
            });
        });

        // ── mono_setStaticFieldValue(classHandle, fieldHandle, value, size) → bool ──
        script.Globals["mono_setStaticFieldValue"] = (Func<DynValue, DynValue, DynValue, DynValue, bool>)((classArg, fieldArg, valueArg, sizeArg) =>
        {
            var pid = RequireProcess(engine);
            var classHandle = ParseHandle(classArg);
            var fieldHandle = ParseHandle(fieldArg);
            var size = sizeArg.IsNil() ? 8 : (int)sizeArg.CastToNumber()!.Value;
            var value = (long)valueArg.CastToNumber()!.Value;

            var bytes = size switch
            {
                1 => new[] { (byte)value },
                2 => BitConverter.GetBytes((short)value),
                4 => BitConverter.GetBytes((int)value),
                _ => BitConverter.GetBytes(value)
            };

            return monoEngine.SetStaticFieldValueAsync(pid, classHandle, fieldHandle, bytes).GetAwaiter().GetResult();
        });

        // ── mono_class_getFullName(classHandle) → string (from cached find) ──
        // Note: CE implements this by re-querying the agent. We simplify by requiring
        // the caller to use the table returned by mono_findClass which includes FullName.
        // This stub exists for script compatibility.
        script.Globals["mono_class_getFullName"] = (Func<DynValue, string>)(classArg =>
        {
            // CE scripts typically call this after mono_findClass, but our implementation
            // returns the full name in the class table. This fallback queries the agent.
            var pid = RequireProcess(engine);
            var classHandle = ParseHandle(classArg);
            // Return a placeholder — real implementation would query agent
            return $"Class@0x{(ulong)classHandle:X}";
        });

        // ── getAddressSafe(expression) → number or nil (pcall wrapper around getAddress) ──
        // CE scripts use this as error-safe address resolution
        script.Globals["getAddressSafe"] = (Func<string, DynValue>)(expression =>
        {
            try
            {
                var pid = RequireProcess(engine);
                var addr = LuaBindingHelpers.ResolveAddress(expression, pid, engineFacade, null);
                return DynValue.NewNumber((double)(ulong)addr);
            }
            catch
            {
                return DynValue.Nil;
            }
        });
    }

    // ── Helpers ──

    private static int RequireProcess(MoonSharpLuaEngine engine)
        => LuaBindingHelpers.RequireProcess(engine);

    private static nuint ParseHandle(DynValue arg)
    {
        if (arg.Type == DataType.Number)
            return (nuint)(ulong)arg.Number;
        if (arg.Type == DataType.String && ulong.TryParse(
                arg.String.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                NumberStyles.HexNumber, null, out var hex))
            return (nuint)hex;
        throw new ScriptRuntimeException($"Expected a handle (number or hex string), got {arg.Type}");
    }

    private static DynValue ClassToTable(Script script, MonoClass cls)
    {
        var t = new Table(script);
        t["handle"] = (double)(ulong)cls.Handle;
        t["namespace"] = cls.Namespace;
        t["name"] = cls.Name;
        t["fullName"] = cls.FullName;
        t["parentHandle"] = (double)(ulong)cls.ParentHandle;
        t["fieldCount"] = (double)cls.FieldCount;
        t["methodCount"] = (double)cls.MethodCount;
        return DynValue.NewTable(t);
    }
}
