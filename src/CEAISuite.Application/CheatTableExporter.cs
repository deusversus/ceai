using System.Globalization;
using System.Xml.Linq;

namespace CEAISuite.Application;

/// <summary>
/// Exports AddressTableNode trees back to Cheat Engine .CT (Cheat Table) XML format.
/// Produces XML that is round-trip compatible with <see cref="CheatTableParser"/>.
/// </summary>
public sealed class CheatTableExporter
{
    private int _nextId;

    /// <summary>
    /// Serialize a tree of address table nodes to a CE-format CT XML string.
    /// </summary>
    public string ExportToXml(IEnumerable<AddressTableNode> roots)
    {
        _nextId = 0;
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("CheatTable",
                new XAttribute("CheatEngineTableVersion", "46"),
                new XElement("CheatEntries",
                    roots.Select(BuildEntry))));

        return doc.Declaration + Environment.NewLine + doc.Root!.ToString();
    }

    /// <summary>
    /// Save a tree of address table nodes to a .CT file on disk.
    /// </summary>
    public void SaveToFile(IEnumerable<AddressTableNode> roots, string filePath)
    {
        var xml = ExportToXml(roots);
        File.WriteAllText(filePath, xml);
    }

    private XElement BuildEntry(AddressTableNode node)
    {
        var id = _nextId++;
        var entry = new XElement("CheatEntry");

        entry.Add(new XElement("ID", id));
        entry.Add(new XElement("Description", $"\"{node.Label}\""));

        if (node.IsGroup)
        {
            entry.Add(new XElement("GroupHeader", "1"));

            if (node.Children.Count > 0)
            {
                entry.Add(new XElement("CheatEntries",
                    node.Children.Select(BuildEntry)));
            }

            return entry;
        }

        // Script-only entries
        if (node.IsScriptEntry)
        {
            entry.Add(new XElement("VariableType", "Auto Assembler Script"));
            entry.Add(new XElement("AssemblerScript", new XCData(node.AssemblerScript!)));
            entry.Add(new XElement("Address", "0"));

            if (node.Children.Count > 0)
            {
                entry.Add(new XElement("CheatEntries",
                    node.Children.Select(BuildEntry)));
            }

            return entry;
        }

        // Regular value entries
        var ceType = MapDataTypeToCe(node.DataType);
        entry.Add(new XElement("VariableType", ceType));

        // Address: use the raw address (module-relative or absolute hex)
        var address = node.Address;
        if (string.IsNullOrEmpty(address)) address = "0";
        entry.Add(new XElement("Address", address));

        if (!node.ShowAsSigned)
        {
            entry.Add(new XElement("ShowAsSigned", "0"));
        }

        if (node.ShowAsHex)
        {
            entry.Add(new XElement("ShowAsHex", "1"));
        }

        if (node.DropDownList is { Count: > 0 })
        {
            // CE format: "value:name\r\n" pairs
            var lines = string.Join("\r\n", node.DropDownList.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
            entry.Add(new XElement("DropDownList", lines + "\r\n"));
        }

        // Pointer offsets — CE stores them deepest-first (same order we keep in PointerOffsets)
        if (node.IsPointer && node.PointerOffsets.Count > 0)
        {
            var offsetsEl = new XElement("Offsets");
            foreach (var offset in node.PointerOffsets)
            {
                offsetsEl.Add(new XElement("Offset", offset.ToString("X", CultureInfo.InvariantCulture)));
            }
            entry.Add(offsetsEl);
        }

        // Last known value
        if (!string.IsNullOrEmpty(node.CurrentValue) && node.CurrentValue != "0" && node.CurrentValue != "??")
        {
            entry.Add(new XElement("LastState",
                new XAttribute("Value", node.CurrentValue)));
        }

        // Assembler script on a value entry (some entries have both address + script)
        if (node.AssemblerScript is not null)
        {
            entry.Add(new XElement("AssemblerScript", new XCData(node.AssemblerScript)));
        }

        // Nested children
        if (node.Children.Count > 0)
        {
            entry.Add(new XElement("CheatEntries",
                node.Children.Select(BuildEntry)));
        }

        return entry;
    }

    private static string MapDataTypeToCe(Engine.Abstractions.MemoryDataType dt) =>
        dt switch
        {
            Engine.Abstractions.MemoryDataType.Byte => "Byte",
            Engine.Abstractions.MemoryDataType.Int16 => "2 Bytes",
            Engine.Abstractions.MemoryDataType.Int32 => "4 Bytes",
            Engine.Abstractions.MemoryDataType.Int64 => "8 Bytes",
            Engine.Abstractions.MemoryDataType.Float => "Float",
            Engine.Abstractions.MemoryDataType.Double => "Double",
            Engine.Abstractions.MemoryDataType.String => "String",
            Engine.Abstractions.MemoryDataType.ByteArray => "Array of Byte",
            _ => "4 Bytes"
        };
}
