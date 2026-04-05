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
    IReadOnlyList<CheatTableEntry> Children,
    bool ShowAsSigned = true,
    bool ShowAsHex = false,
    IReadOnlyDictionary<int, string>? DropDownList = null);

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

    private static List<AddressTableNode> ConvertToNodes(IReadOnlyList<CheatTableEntry> entries, AddressTableNode? parent = null)
    {
        var nodes = new List<AddressTableNode>();
        foreach (var entry in entries)
        {
            if (entry.IsGroupHeader)
            {
                var group = new AddressTableNode($"ct-{entry.Id}", entry.Description, true)
                {
                    Address = entry.Address,
                    IsPointer = entry.IsPointer,
                    PointerOffsets = entry.PointerOffsets.Select(o => ParseCeOffset(o)).ToList(),
                    IsOffset = entry.Address.TrimStart().StartsWith('+') || entry.Address.TrimStart().StartsWith('-'),
                    Parent = parent,
                    ShowAsSigned = entry.ShowAsSigned,
                    ShowAsHex = entry.ShowAsHex,
                    DropDownList = entry.DropDownList is not null ? new Dictionary<int, string>(entry.DropDownList) : null
                };
                foreach (var child in ConvertToNodes(entry.Children, group))
                    group.Children.Add(child);
                nodes.Add(group);
                continue;
            }

            // Script-only entries (address = "0" but has a script)
            if (entry.AssemblerScript is not null && (entry.Address == "0" || entry.Address == "+0"))
            {
                var scriptNode = new AddressTableNode($"ct-{entry.Id}", entry.Description, false)
                {
                    Address = "(script)",
                    AssemblerScript = entry.AssemblerScript,
                    Notes = entry.AssemblerScript.Contains("LuaCall") ? "LuaCall script (display only)" : "Auto Assembler script",
                    Parent = parent
                };
                // Also import any children the script entry may have
                foreach (var child in ConvertToNodes(entry.Children, scriptNode))
                    scriptNode.Children.Add(child);
                nodes.Add(scriptNode);
                continue;
            }

            // CE inheritance: children inherit ShowAsHex / DropDownList from their parent group
            // when the child entry itself doesn't explicitly set them.
            var effectiveShowAsHex = entry.ShowAsHex || (parent?.ShowAsHex ?? false);
            var effectiveDropDown = entry.DropDownList is not null
                ? new Dictionary<int, string>(entry.DropDownList)
                : parent?.DropDownList is not null
                    ? new Dictionary<int, string>(parent.DropDownList)
                    : null;

            var node = new AddressTableNode($"ct-{entry.Id}", entry.Description, false)
            {
                Address = entry.Address,
                DataType = entry.DataType,
                CurrentValue = entry.LastValue ?? "0",
                IsPointer = entry.IsPointer,
                PointerOffsets = entry.PointerOffsets.Select(o => ParseCeOffset(o)).ToList(),
                IsOffset = entry.Address.TrimStart().StartsWith('+') || entry.Address.TrimStart().StartsWith('-'),
                Notes = entry.IsPointer ? $"Pointer: [{string.Join(" → ", entry.PointerOffsets)}]" : null,
                AssemblerScript = entry.AssemblerScript,
                Parent = parent,
                ShowAsSigned = entry.ShowAsSigned,
                ShowAsHex = effectiveShowAsHex,
                DropDownList = effectiveDropDown
            };

            // Import any children
            foreach (var child in ConvertToNodes(entry.Children, node))
                node.Children.Add(child);

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

            // Script-only entries still get imported (with placeholder address)
            if (entry.AssemblerScript is not null && (entry.Address == "0" || entry.Address == "+0"))
            {
                var scriptLabel = groupPrefix is not null
                    ? $"{groupPrefix}/{entry.Description}"
                    : entry.Description;
                result.Add(new AddressTableEntry(
                    $"ct-{entry.Id}",
                    scriptLabel,
                    "(script)",
                    MemoryDataType.Int32,
                    "",
                    null,
                    "Auto Assembler script",
                    false,
                    null));
                FlattenEntries(entry.Children, result, groupPrefix);
                continue;
            }

            var label = groupPrefix is not null
                ? $"{groupPrefix}/{entry.Description}"
                : entry.Description;

            var address = entry.Address;

            // For display in flat mode, show module-relative address
            if (!address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && !address.Contains('+')
                && !address.Contains('.'))
            {
                address = $"0x{address}";
            }

            if (entry.IsPointer)
            {
                address = $"P->{address}+[{string.Join(",", entry.PointerOffsets)}]";
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

        // CE ShowAsSigned: 0 = unsigned display (default in CE is unsigned)
        var showAsSigned = el.Element("ShowAsSigned")?.Value != "0";

        // CE ShowAsHex: 1 = display values in hexadecimal
        var showAsHex = el.Element("ShowAsHex")?.Value == "1";

        // CE DropDownList: newline-separated "value:description" pairs for value-to-name mapping
        Dictionary<int, string>? dropDownList = null;
        var dropDownText = el.Element("DropDownList")?.Value;
        if (!string.IsNullOrEmpty(dropDownText))
        {
            dropDownList = new Dictionary<int, string>();
            foreach (var line in dropDownText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx <= 0) continue;
                var valueStr = line[..colonIdx].Trim();
                var name = line[(colonIdx + 1)..].Trim();
                // CE dropdown values: when ShowAsHex=1, bare values (no 0x prefix) are hex.
                // Always handle explicit 0x prefix first, then context-dependent parsing.
                if (valueStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(valueStr[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexVal))
                    dropDownList.TryAdd(hexVal, name);
                else if (showAsHex && int.TryParse(valueStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var showHexVal))
                    dropDownList.TryAdd(showHexVal, name);
                else if (int.TryParse(valueStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
                    dropDownList.TryAdd(intVal, name);
                else if (int.TryParse(valueStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rawHexVal))
                    dropDownList.TryAdd(rawHexVal, name);
            }
            if (dropDownList.Count == 0) dropDownList = null;
        }

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
            isPointer, offsets, isGroupHeader, assemblerScript, children,
            showAsSigned, showAsHex, dropDownList);
    }

    private static MemoryDataType MapVariableType(string ceType) =>
        ceType.ToLowerInvariant() switch
        {
            "byte" => MemoryDataType.Byte,
            "2 bytes" => MemoryDataType.Int16,
            "4 bytes" => MemoryDataType.Int32,
            "8 bytes" => MemoryDataType.Int64,
            "float" => MemoryDataType.Float,
            "double" => MemoryDataType.Double,
            "string" => MemoryDataType.String,
            "array of byte" => MemoryDataType.ByteArray,
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

    /// <summary>Parse a CE hex offset string (e.g. "B8", "0x10") into a long.</summary>
    private static long ParseCeOffset(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var val) ? val : 0;
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
