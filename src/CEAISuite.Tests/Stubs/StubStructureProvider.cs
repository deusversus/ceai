using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubStructureProvider : ILuaStructureProvider
{
    private readonly ConcurrentDictionary<string, (string Name, List<LuaStructureField> Fields)> _structures = new();
    private int _nextId;

    public string CreateStructure(string name)
    {
        var id = $"struct_{Interlocked.Increment(ref _nextId)}";
        _structures[id] = (name, []);
        return id;
    }

    public void AddElement(string structureId, int offset, string fieldType, string name)
    {
        if (_structures.TryGetValue(structureId, out var s))
            s.Fields.Add(new LuaStructureField(offset, name, fieldType, null));
    }

    public LuaStructureDefinition? GetStructure(string structureId)
    {
        if (!_structures.TryGetValue(structureId, out var s)) return null;
        return new LuaStructureDefinition(structureId, s.Name, s.Fields.ToList());
    }

    public IReadOnlyList<LuaStructureDefinition> ListStructures() =>
        _structures.Select(kv =>
            new LuaStructureDefinition(kv.Key, kv.Value.Name, kv.Value.Fields.ToList()))
            .ToList();

    public string ExportAsCStruct(string structureId)
    {
        if (!_structures.TryGetValue(structureId, out var s))
            return "// not found";

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"typedef struct {{");
        foreach (var f in s.Fields.OrderBy(f => f.Offset))
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {f.FieldType,-12} {f.Name}; // 0x{f.Offset:X}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"}} {s.Name};");
        return sb.ToString();
    }

    public void RemoveStructure(string structureId) =>
        _structures.TryRemove(structureId, out _);

    public IReadOnlyList<LuaStructureField> DissectMemory(int processId, nuint address, int size) =>
    [
        new(0x00, "field_0000", "Int32", "42"),
        new(0x04, "field_0004", "Float", "3.14"),
        new(0x08, "field_0008", "Pointer", "0x7FF00000")
    ];
}
