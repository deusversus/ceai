using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// CE-compatible structure definition Lua functions: createStructure, structure_addElement,
/// structure_autoGuess, structure_toCStruct, listStructures.
/// Delegates to <see cref="ILuaStructureProvider"/> for Application-layer isolation.
/// </summary>
internal static class LuaStructureBindings
{
    public static void Register(
        Script script,
        MoonSharpLuaEngine engine,
        ILuaStructureProvider provider)
    {
        // createStructure(name) → structure proxy table
        script.Globals["createStructure"] = (Func<string, DynValue>)(name =>
        {
            var structId = provider.CreateStructure(name);
            return StructureToTable(script, structId, name, provider, engine);
        });

        // listStructures() → table of structure proxies
        script.Globals["listStructures"] = (Func<DynValue>)(() =>
        {
            var structs = provider.ListStructures();
            var table = new Table(script);
            for (int i = 0; i < structs.Count; i++)
            {
                var s = structs[i];
                table[i + 1] = StructureToTable(script, s.Id, s.Name, provider, engine);
            }
            return DynValue.NewTable(table);
        });

        // Standalone functions for CE compatibility
        script.Globals["structure_getName"] = (Func<Table, string>)(st =>
        {
            var def = provider.GetStructure(st.Get("_id").String);
            return def?.Name ?? "";
        });

        script.Globals["structure_addElement"] = (Action<Table, int, string, string>)((st, offset, fieldType, name) =>
            provider.AddElement(st.Get("_id").String, offset, fieldType, name));

        script.Globals["structure_getElement"] = (Func<Table, double, DynValue>)((st, index) =>
        {
            var def = provider.GetStructure(st.Get("_id").String);
            if (def is null) return DynValue.Nil;
            var idx = (int)index;
            if (idx < 0 || idx >= def.Fields.Count) return DynValue.Nil;

            var field = def.Fields[idx];
            var ft = new Table(script);
            ft["Offset"] = (double)field.Offset;
            ft["Name"] = field.Name;
            ft["Type"] = field.FieldType;
            ft["Value"] = field.Value ?? "";
            return DynValue.NewTable(ft);
        });

        script.Globals["structure_getElementCount"] = (Func<Table, double>)(st =>
        {
            var def = provider.GetStructure(st.Get("_id").String);
            return def?.Fields.Count ?? 0;
        });

        script.Globals["structure_toCStruct"] = (Func<Table, string>)(st =>
            provider.ExportAsCStruct(st.Get("_id").String));
    }

    private static DynValue StructureToTable(
        Script script, string structId, string name,
        ILuaStructureProvider provider, MoonSharpLuaEngine engine)
    {
        var table = new Table(script);
        table["_id"] = structId;
        table["Name"] = name;

        table["getName"] = (Func<string>)(() =>
        {
            var def = provider.GetStructure(structId);
            return def?.Name ?? name;
        });

        table["addElement"] = (Action<int, string, string>)((offset, fieldType, fieldName) =>
            provider.AddElement(structId, offset, fieldType, fieldName));

        table["getElement"] = (Func<double, DynValue>)(index =>
        {
            var def = provider.GetStructure(structId);
            if (def is null) return DynValue.Nil;
            var idx = (int)index;
            if (idx < 0 || idx >= def.Fields.Count) return DynValue.Nil;

            var field = def.Fields[idx];
            var ft = new Table(script);
            ft["Offset"] = (double)field.Offset;
            ft["Name"] = field.Name;
            ft["Type"] = field.FieldType;
            ft["Value"] = field.Value ?? "";
            return DynValue.NewTable(ft);
        });

        table["getElementCount"] = (Func<double>)(() =>
        {
            var def = provider.GetStructure(structId);
            return def?.Fields.Count ?? 0;
        });

        table["autoGuess"] = (Action<DynValue, DynValue>)((addrArg, sizeArg) =>
        {
            var pid = LuaBindingHelpers.RequireProcess(engine);
            nuint address;
            if (addrArg.Type == DataType.Number)
                address = (nuint)(ulong)addrArg.Number;
            else
                throw new ScriptRuntimeException("autoGuess: address must be a number");

            var size = sizeArg.IsNil() ? 256 : (int)sizeArg.Number;
            var fields = provider.DissectMemory(pid, address, size);
            foreach (var field in fields)
                provider.AddElement(structId, field.Offset, field.FieldType, field.Name);
        });

        table["toCStruct"] = (Func<string>)(() =>
            provider.ExportAsCStruct(structId));

        table["destroy"] = (Action)(() =>
            provider.RemoveStructure(structId));

        return DynValue.NewTable(table);
    }
}
