using System.Globalization;
using System.Xml.Linq;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

/// <summary>
/// Represents a single entry parsed from a Cheat Engine .CT (Cheat Table) file.
/// </summary>
public sealed record CheatTableEntry(
    int Id,
    string Description,
    string Address,
    MemoryDataType DataType,
    string? LastValue,
    bool IsPointer,
    IReadOnlyList<string> PointerOffsets,
    bool IsGroupHeader,
    string? AssemblerScript,
    IReadOnlyList<CheatTableEntry> Children);

/// <summary>
/// Represents a parsed Cheat Engine .CT file.
/// </summary>
public sealed record CheatTableFile(
    string FileName,
    int TableVersion,
    IReadOnlyList<CheatTableEntry> Entries,
    int TotalEntryCount,
    string? LuaScript);

/// <summary>
/// Parses Cheat Engine .CT (Cheat Table) XML files into our domain model.
/// Supports addresses, pointer chains, groups, scripts, and nested entries.
/// </summary>
public sealed class CheatTableParser
{
    /// <summary>
    /// Parse a .CT file from a file path.
    /// </summary>
    public CheatTableFile ParseFile(string filePath)
    {
        var xml = File.ReadAllText(filePath);
        var fileName = Path.GetFileName(filePath);
        return Parse(xml, fileName);
    }

    /// <summary>
    /// Parse a .CT file from raw XML content.
    /// </summary>
    public CheatTableFile Parse(string xml, string fileName = "unknown.ct")
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root
            ?? throw new FormatException("Invalid CT file: no root element.");

        var tableVersion = int.TryParse(root.Attribute("CheatEngineTableVersion")?.Value, out var v) ? v : 0;

        var entriesElement = root.Element("CheatEntries");
        var entries = entriesElement is not null
            ? ParseEntries(entriesElement).ToList()
            : new List<CheatTableEntry>();

        var luaScript = root.Element("LuaScript")?.Value;

        var totalCount = CountEntries(entries);

        return new CheatTableFile(fileName, tableVersion, entries, totalCount, luaScript);
    }

    /// <summary>
    /// Convert parsed CT entries into AddressTableEntry objects (flat, for backward compat).
    /// </summary>
    public IReadOnlyList<AddressTableEntry> ToAddressTableEntries(CheatTableFile ctFile)
    {
        var result = new List<AddressTableEntry>();
        FlattenEntries(ctFile.Entries, result, null);
        return result;
    }

    /// <summary>
    /// Convert parsed CT entries into a tree of AddressTableNode, preserving group hierarchy.
    /// </summary>
    public IReadOnlyList<AddressTableNode> ToAddressTableNodes(CheatTableFile ctFile)
    {
        return ConvertToNodes(ctFile.Entries);
    }

    private static List<AddressTableNode> ConvertToNodes(IReadOnlyList<CheatTableEntry> entries)
    {
        var nodes = new List<AddressTableNode>();
        foreach (var entry in entries)
        {
            if (entry.IsGroupHeader)
            {
                var group = new AddressTableNode($"ct-{entry.Id}", entry.Description, true);
                foreach (var child in ConvertToNodes(entry.Children))
                    group.Children.Add(child);
                nodes.Add(group);
                continue;
            }

            // Skip script-only entries with no address
            if (entry.AssemblerScript is not null && entry.Address == "0")
                continue;

            var address = entry.IsPointer
                ? $"{entry.Address}+{string.Join("+", entry.PointerOffsets)}"
                : entry.Address;

            if (!address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && !address.Contains('+')
                && !address.Contains('.'))
            {
                address = $"0x{address}";
            }

            var node = new AddressTableNode($"ct-{entry.Id}", entry.Description, false)
            {
                Address = address,
                DataType = entry.DataType,
                CurrentValue = entry.LastValue ?? "0",
                Notes = entry.IsPointer ? $"Pointer: [{string.Join(" -> ", entry.PointerOffsets)}]" : null
            };
            nodes.Add(node);
        }
        return nodes;
    }

    private void FlattenEntries(
        IReadOnlyList<CheatTableEntry> entries,
        List<AddressTableEntry> result,
        string? groupPrefix)
    {
        foreach (var entry in entries)
        {
            if (entry.IsGroupHeader)
            {
                var prefix = groupPrefix is not null
                    ? $"{groupPrefix}/{entry.Description}"
                    : entry.Description;
                FlattenEntries(entry.Children, result, prefix);
                continue;
            }

            // Skip script-only entries
            if (entry.AssemblerScript is not null && entry.Address == "0")
                continue;

            var label = groupPrefix is not null
                ? $"{groupPrefix}/{entry.Description}"
                : entry.Description;

            var address = entry.IsPointer
                ? $"{entry.Address}+{string.Join("+", entry.PointerOffsets)}"
                : entry.Address;

            // Normalize address format
            if (!address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && !address.Contains('+')
                && !address.Contains('.'))
            {
                address = $"0x{address}";
            }

            result.Add(new AddressTableEntry(
                $"ct-{entry.Id}",
                label,
                address,
                entry.DataType,
                entry.LastValue ?? "0",
                null,
                entry.IsPointer ? $"Pointer: [{string.Join(" -> ", entry.PointerOffsets)}]" : null,
                false,
                null));
        }
    }

    private static IEnumerable<CheatTableEntry> ParseEntries(XElement entriesElement)
    {
        foreach (var entryEl in entriesElement.Elements("CheatEntry"))
        {
            yield return ParseEntry(entryEl);
        }
    }

    private static CheatTableEntry ParseEntry(XElement el)
    {
        var id = int.TryParse(el.Element("ID")?.Value, out var parsedId) ? parsedId : 0;
        var description = CleanDescription(el.Element("Description")?.Value ?? "Unknown");
        var isGroupHeader = el.Element("GroupHeader")?.Value == "1";
        var variableType = el.Element("VariableType")?.Value ?? "4 Bytes";
        var address = el.Element("Address")?.Value ?? "0";
        var isPointer = el.Element("Offsets") is not null;

        // Parse pointer offsets (CE stores them in reverse order)
        var offsets = new List<string>();
        var offsetsEl = el.Element("Offsets");
        if (offsetsEl is not null)
        {
            foreach (var offset in offsetsEl.Elements("Offset"))
            {
                offsets.Add(offset.Value);
            }
        }

        // Parse last known value
        string? lastValue = null;
        var lastStateEl = el.Element("LastState");
        if (lastStateEl is not null)
        {
            lastValue = lastStateEl.Attribute("Value")?.Value;
        }

        var assemblerScript = el.Element("AssemblerScript")?.Value;

        // Parse nested child entries
        var children = new List<CheatTableEntry>();
        var childEntriesEl = el.Element("CheatEntries");
        if (childEntriesEl is not null)
        {
            children.AddRange(ParseEntries(childEntriesEl));
        }

        var dataType = MapVariableType(variableType);

        return new CheatTableEntry(
            id, description, address, dataType, lastValue,
            isPointer, offsets, isGroupHeader, assemblerScript, children);
    }

    private static MemoryDataType MapVariableType(string ceType) =>
        ceType.ToLowerInvariant() switch
        {
            "byte" => MemoryDataType.Int32,         // Upcast byte to Int32
            "2 bytes" => MemoryDataType.Int32,       // Upcast Int16 to Int32
            "4 bytes" => MemoryDataType.Int32,
            "8 bytes" => MemoryDataType.Int64,
            "float" => MemoryDataType.Float,
            "double" => MemoryDataType.Double,
            "string" => MemoryDataType.Int32,        // Best-effort fallback
            "array of byte" => MemoryDataType.Int32, // Best-effort fallback
            "binary" => MemoryDataType.Int32,
            "auto assembler script" => MemoryDataType.Int32,
            _ => MemoryDataType.Int32
        };

    private static string CleanDescription(string raw)
    {
        // CE wraps descriptions in quotes: "Health" → Health
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            return raw[1..^1];
        return raw;
    }

    private static int CountEntries(IReadOnlyList<CheatTableEntry> entries)
    {
        var count = 0;
        foreach (var entry in entries)
        {
            count++;
            count += CountEntries(entry.Children);
        }
        return count;
    }
}
