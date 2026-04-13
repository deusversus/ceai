using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

/// <summary>
/// Bridges <see cref="ILuaStructureProvider"/> to <see cref="StructureDissectorService"/>,
/// maintaining in-memory structure definitions and providing C struct export.
/// </summary>
public sealed class LuaStructureAdapter : ILuaStructureProvider
{
    private readonly IEngineFacade _engineFacade;
    private readonly ConcurrentDictionary<string, MutableStructure> _structures = new();
    private int _nextId;

    public LuaStructureAdapter(IEngineFacade engineFacade)
    {
        _engineFacade = engineFacade;
    }

    public string CreateStructure(string name)
    {
        var id = $"struct_{Interlocked.Increment(ref _nextId)}";
        _structures[id] = new MutableStructure(name);
        return id;
    }

    public void AddElement(string structureId, int offset, string fieldType, string name)
    {
        if (_structures.TryGetValue(structureId, out var structure))
            structure.Fields.Add(new LuaStructureField(offset, name, fieldType, null));
    }

    public LuaStructureDefinition? GetStructure(string structureId)
    {
        if (!_structures.TryGetValue(structureId, out var structure)) return null;
        return new LuaStructureDefinition(structureId, structure.Name, structure.Fields.ToList());
    }

    public IReadOnlyList<LuaStructureDefinition> ListStructures()
    {
        return _structures.Select(kv =>
            new LuaStructureDefinition(kv.Key, kv.Value.Name, kv.Value.Fields.ToList()))
            .ToList();
    }

    public string ExportAsCStruct(string structureId)
    {
        if (!_structures.TryGetValue(structureId, out var structure))
            return "// Structure not found";

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"typedef struct {{");

        foreach (var field in structure.Fields.OrderBy(f => f.Offset))
        {
            var cType = MapToCType(field.FieldType);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"    {cType,-16} {field.Name}; // offset 0x{field.Offset:X}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"}} {structure.Name};");
        return sb.ToString();
    }

    public void RemoveStructure(string structureId)
        => _structures.TryRemove(structureId, out _);

    public IReadOnlyList<LuaStructureField> DissectMemory(int processId, nuint address, int size)
    {
        var dissector = new StructureDissectorService(_engineFacade);
        var (fields, _) = dissector.DissectAsync(processId, address, size).GetAwaiter().GetResult();

        return fields.Select(f => new LuaStructureField(
            f.Offset,
            $"field_{f.Offset:X4}",
            f.ProbableType,
            f.DisplayValue))
            .ToList();
    }

    private static string MapToCType(string fieldType) => fieldType.ToLowerInvariant() switch
    {
        "byte" => "uint8_t",
        "int16" or "short" => "int16_t",
        "int32" or "int" or "integer" => "int32_t",
        "int64" or "long" or "qword" => "int64_t",
        "float" => "float",
        "double" => "double",
        "pointer" or "ptr" => "void*",
        "string" => "char*",
        "padding/zero" or "padding" => "uint8_t",
        _ => $"/* {fieldType} */ uint8_t"
    };

    private sealed class MutableStructure(string name)
    {
        public string Name { get; } = name;
        public List<LuaStructureField> Fields { get; } = [];
    }
}
