using System.Globalization;
using System.Xml.Linq;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

/// <summary>
/// Options flags from CE's <Options> element on cheat entries.
/// Controls group behavior like hiding children, cascading activation, etc.
/// </summary>
[Flags]
public enum CheatEntryOptions
{
    None = 0,
    HideChildren = 1 << 0,
    ActivateChildrenAsWell = 1 << 1,
    DeactivateChildrenAsWell = 1 << 2,
    RecursiveSetValue = 1 << 3,
    AllowManualCollapseAndExpand = 1 << 4
}

/// <summary>
/// Represents a hotkey binding on a cheat entry.
/// </summary>
public sealed record CheatEntryHotkey(string Action, IReadOnlyList<int> Keys, string? Value, int Id);

/// <summary>
/// Represents an offset with optional CE attributes (Interval, UpdateOnFullRefresh).
/// </summary>
public sealed record CeOffset(string Value, int? Interval = null, bool UpdateOnFullRefresh = false);

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
    IReadOnlyDictionary<int, string>? DropDownList = null,
    string? DropDownListLink = null,
    string? RawDropDownText = null,
    IReadOnlyList<XElement>? PreservedElements = null,
    string? Color = null,
    CheatEntryOptions Options = CheatEntryOptions.None,
    IReadOnlyList<CheatEntryHotkey>? Hotkeys = null,
    string? LastRealAddress = null,
    bool LastActivated = false,
    IReadOnlyList<CeOffset>? RichOffsets = null,
    int? StringLength = null,
    bool? IsUnicode = null,
    bool? ZeroTerminate = null,
    int? ByteLength = null,
    int? BitStart = null,
    int? BitLength = null,
    bool ShowAsBinary = false,
    string? CustomTypeName = null,
    bool DropDownDescriptionOnly = false,
    bool DropDownReadOnly = false,
    bool DropDownDisplayValueAsItem = false,
    bool ScriptAsync = false,
    string? Comment = null);

/// <summary>
/// Represents a parsed Cheat Engine .CT file.
/// </summary>
public sealed record CheatTableFile(
    string FileName,
    int TableVersion,
    IReadOnlyList<CheatTableEntry> Entries,
    int TotalEntryCount,
    string? LuaScript,
    IReadOnlyList<XElement>? PreservedElements = null);

/// <summary>
/// Parses Cheat Engine .CT (Cheat Table) XML files into our domain model.
/// Supports addresses, pointer chains, groups, scripts, and nested entries.
/// </summary>
public sealed class CheatTableParser
{
    /// <summary>Known top-level element names under the CheatTable root.</summary>
    private static readonly HashSet<string> KnownRootElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "CheatEntries", "LuaScript"
    };

    /// <summary>Known element names within a CheatEntry.</summary>
    private static readonly HashSet<string> KnownEntryElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "ID", "Description", "GroupHeader", "VariableType", "Address",
        "Offsets", "LastState", "AssemblerScript", "ShowAsSigned", "ShowAsHex",
        "DropDownList", "DropDownListLink", "CheatEntries",
        "Color", "Options", "Hotkeys",
        "Length", "Unicode", "ZeroTerminate", "ByteLength",
        "BitStart", "BitLength", "ShowAsBinary",
        "CustomType", "Comment"
    };

    /// <summary>
    /// Parse a .CT file from a file path.
    /// </summary>
    public static CheatTableFile ParseFile(string filePath)
    {
        var xml = File.ReadAllText(filePath);
        var fileName = Path.GetFileName(filePath);
        return Parse(xml, fileName);
    }

    /// <summary>
    /// Parse a .CT file from raw XML content.
    /// </summary>
    public static CheatTableFile Parse(string xml, string fileName = "unknown.ct")
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

        // Step 0: Collect unknown root-level elements as preserved XElement clones
        var preservedRootElements = root.Elements()
            .Where(e => !KnownRootElements.Contains(e.Name.LocalName))
            .Select(e => new XElement(e))
            .ToList();

        var totalCount = CountEntries(entries);

        return new CheatTableFile(fileName, tableVersion, entries, totalCount, luaScript,
            preservedRootElements.Count > 0 ? preservedRootElements : null);
    }

    /// <summary>
    /// Convert parsed CT entries into AddressTableEntry objects (flat, for backward compat).
    /// </summary>
    public static IReadOnlyList<AddressTableEntry> ToAddressTableEntries(CheatTableFile ctFile)
    {
        var result = new List<AddressTableEntry>();
        FlattenEntries(ctFile.Entries, result, null);
        return result;
    }

    /// <summary>
    /// Convert parsed CT entries into a tree of AddressTableNode, preserving group hierarchy.
    /// </summary>
    public static IReadOnlyList<AddressTableNode> ToAddressTableNodes(CheatTableFile ctFile)
    {
        // Build a global lookup of Description -> raw DropDownList text for resolving <DropDownListLink>.
        var dropDownRawLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        BuildDropDownRawLookup(ctFile.Entries, dropDownRawLookup);
        return ConvertToNodes(ctFile.Entries, null, dropDownRawLookup);
    }

    /// <summary>Recursively collect raw DropDownList text from all entries, keyed by cleaned description.</summary>
    private static void BuildDropDownRawLookup(IReadOnlyList<CheatTableEntry> entries, Dictionary<string, string> lookup)
    {
        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.RawDropDownText))
                lookup.TryAdd(entry.Description, entry.RawDropDownText);
            BuildDropDownRawLookup(entry.Children, lookup);
        }
    }

    /// <summary>Parse CE dropdown text ("value:name\n...") into int->string map, interpreting values as hex when showAsHex is true.</summary>
    internal static Dictionary<int, string>? ParseDropDownText(string? text, bool showAsHex)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var dict = new Dictionary<int, string>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;
            var valueStr = line[..colonIdx].Trim();
            var name = line[(colonIdx + 1)..].Trim();
            if (valueStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(valueStr[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexVal))
                dict.TryAdd(hexVal, name);
            else if (showAsHex && int.TryParse(valueStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var showHexVal))
                dict.TryAdd(showHexVal, name);
            else if (int.TryParse(valueStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
                dict.TryAdd(intVal, name);
            else if (int.TryParse(valueStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rawHexVal))
                dict.TryAdd(rawHexVal, name);
        }
        return dict.Count > 0 ? dict : null;
    }

    /// <summary>Convert BGR hex (e.g. "FF0000") to RGB hex string (e.g. "#0000FF").</summary>
    internal static string? ConvertBgrToRgb(string? bgrHex)
    {
        if (string.IsNullOrWhiteSpace(bgrHex)) return null;
        var s = bgrHex.Trim();
        // Pad to 6 chars
        s = s.PadLeft(6, '0');
        if (s.Length < 6) return null;
        // BGR -> RGB: swap first and last byte pairs
        return $"#{s[4..6]}{s[2..4]}{s[0..2]}";
    }

    /// <summary>Convert RGB hex string (e.g. "#RRGGBB") to BGR hex (e.g. "BBGGRR").</summary>
    internal static string? ConvertRgbToBgr(string? rgbHex)
    {
        if (string.IsNullOrWhiteSpace(rgbHex)) return null;
        var s = rgbHex.Trim().TrimStart('#');
        if (s.Length < 6) return null;
        return $"{s[4..6]}{s[2..4]}{s[0..2]}";
    }

    private static List<AddressTableNode> ConvertToNodes(
        IReadOnlyList<CheatTableEntry> entries,
        AddressTableNode? parent,
        Dictionary<string, string> dropDownRawLookup)
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
                    DropDownList = entry.DropDownList is not null ? new Dictionary<int, string>(entry.DropDownList) : null,
                    UserColor = entry.Color is not null ? ConvertBgrToRgb(entry.Color) : null,
                    Options = entry.Options,
                    Hotkeys = entry.Hotkeys?.ToList() ?? new List<CheatEntryHotkey>(),
                    PreservedElements = entry.PreservedElements?.ToList(),
                    OriginalOffsets = entry.RichOffsets?.ToList(),
                    Comment = entry.Comment
                };
                if (!string.IsNullOrEmpty(entry.Comment))
                    group.Notes = entry.Comment;
                foreach (var child in ConvertToNodes(entry.Children, group, dropDownRawLookup))
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
                    Notes = entry.Comment ?? (entry.AssemblerScript.Contains("LuaCall") ? "LuaCall script (display only)" : "Auto Assembler script"),
                    Parent = parent,
                    UserColor = entry.Color is not null ? ConvertBgrToRgb(entry.Color) : null,
                    Options = entry.Options,
                    Hotkeys = entry.Hotkeys?.ToList() ?? new List<CheatEntryHotkey>(),
                    PreservedElements = entry.PreservedElements?.ToList(),
                    ScriptAsync = entry.ScriptAsync,
                    LastRealAddress = entry.LastRealAddress,
                    Comment = entry.Comment
                };
                // LastActivated -> IsScriptEnabled
                if (entry.LastActivated)
                    scriptNode.IsScriptEnabled = true;
                foreach (var child in ConvertToNodes(entry.Children, scriptNode, dropDownRawLookup))
                    scriptNode.Children.Add(child);
                nodes.Add(scriptNode);
                continue;
            }

            // CE inheritance: children inherit ShowAsHex / DropDownList from their parent group
            var effectiveShowAsHex = entry.ShowAsHex || (parent?.ShowAsHex ?? false);

            // Resolve DropDownList: own > DropDownListLink (re-parsed with consumer's ShowAsHex) > parent inheritance
            Dictionary<int, string>? effectiveDropDown = null;
            if (entry.DropDownList is { Count: > 0 })
                effectiveDropDown = new Dictionary<int, string>(entry.DropDownList);
            else if (entry.DropDownListLink is not null
                     && dropDownRawLookup.TryGetValue(entry.DropDownListLink, out var rawText))
                effectiveDropDown = ParseDropDownText(rawText, effectiveShowAsHex);
            else if (parent?.DropDownList is not null)
                effectiveDropDown = new Dictionary<int, string>(parent.DropDownList);

            var node = new AddressTableNode($"ct-{entry.Id}", entry.Description, false)
            {
                Address = entry.Address,
                DataType = entry.DataType,
                CurrentValue = entry.LastValue ?? "0",
                IsPointer = entry.IsPointer,
                PointerOffsets = entry.PointerOffsets.Select(o => ParseCeOffset(o)).ToList(),
                IsOffset = entry.Address.TrimStart().StartsWith('+') || entry.Address.TrimStart().StartsWith('-'),
                Notes = entry.Comment ?? (entry.IsPointer ? $"Pointer: [{string.Join(" -> ", entry.PointerOffsets)}]" : null),
                AssemblerScript = entry.AssemblerScript,
                Parent = parent,
                ShowAsSigned = entry.ShowAsSigned,
                ShowAsHex = effectiveShowAsHex,
                DropDownList = effectiveDropDown,
                UserColor = entry.Color is not null ? ConvertBgrToRgb(entry.Color) : null,
                Options = entry.Options,
                Hotkeys = entry.Hotkeys?.ToList() ?? new List<CheatEntryHotkey>(),
                PreservedElements = entry.PreservedElements?.ToList(),
                OriginalOffsets = entry.RichOffsets?.ToList(),
                LastRealAddress = entry.LastRealAddress,
                StringLength = entry.StringLength,
                IsUnicode = entry.IsUnicode,
                ZeroTerminate = entry.ZeroTerminate,
                ByteLength = entry.ByteLength,
                BitStart = entry.BitStart,
                BitLength = entry.BitLength,
                ShowAsBinary = entry.ShowAsBinary,
                CustomTypeName = entry.CustomTypeName,
                DropDownDescriptionOnly = entry.DropDownDescriptionOnly,
                DropDownReadOnly = entry.DropDownReadOnly,
                DropDownDisplayValueAsItem = entry.DropDownDisplayValueAsItem,
                ScriptAsync = entry.ScriptAsync,
                Comment = entry.Comment
            };

            // Import any children
            foreach (var child in ConvertToNodes(entry.Children, node, dropDownRawLookup))
                node.Children.Add(child);

            nodes.Add(node);
        }
        return nodes;
    }

    private static void FlattenEntries(
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
        var richOffsets = new List<CeOffset>();
        var offsetsEl = el.Element("Offsets");
        if (offsetsEl is not null)
        {
            foreach (var offset in offsetsEl.Elements("Offset"))
            {
                offsets.Add(offset.Value);
                int? interval = null;
                bool updateOnFullRefresh = false;
                if (int.TryParse(offset.Attribute("Interval")?.Value, out var iv))
                    interval = iv;
                if (offset.Attribute("UpdateOnFullRefresh")?.Value == "1")
                    updateOnFullRefresh = true;
                richOffsets.Add(new CeOffset(offset.Value, interval, updateOnFullRefresh));
            }
        }

        // Parse last known value + RealAddress + Activated
        string? lastValue = null;
        string? lastRealAddress = null;
        bool lastActivated = false;
        var lastStateEl = el.Element("LastState");
        if (lastStateEl is not null)
        {
            lastValue = lastStateEl.Attribute("Value")?.Value;
            lastRealAddress = lastStateEl.Attribute("RealAddress")?.Value;
            lastActivated = lastStateEl.Attribute("Activated")?.Value == "1";
        }

        var assemblerScriptEl = el.Element("AssemblerScript");
        var assemblerScript = assemblerScriptEl?.Value;
        bool scriptAsync = assemblerScriptEl?.Attribute("Async")?.Value == "1";

        // CE ShowAsSigned: 0 = unsigned display (default in CE is unsigned)
        var showAsSigned = el.Element("ShowAsSigned")?.Value != "0";

        // CE ShowAsHex: 1 = display values in hexadecimal
        var showAsHex = el.Element("ShowAsHex")?.Value == "1";

        // CE DropDownList: newline-separated "value:description" pairs for value-to-name mapping
        var dropDownEl = el.Element("DropDownList");
        var dropDownText = dropDownEl?.Value;
        var dropDownList = ParseDropDownText(dropDownText, showAsHex);
        bool dropDownDescriptionOnly = dropDownEl?.Attribute("DescriptionOnly")?.Value == "1";
        bool dropDownReadOnly = dropDownEl?.Attribute("ReadOnly")?.Value == "1";
        bool dropDownDisplayValueAsItem = dropDownEl?.Attribute("DisplayValueAsItem")?.Value == "1";

        // CE DropDownListLink: reference to another entry's DropDownList by description
        var dropDownListLink = el.Element("DropDownListLink")?.Value;
        if (dropDownListLink is not null)
            dropDownListLink = CleanDescription(dropDownListLink);

        // Color (BGR hex)
        var color = el.Element("Color")?.Value;

        // Options flags
        var options = CheatEntryOptions.None;
        var optionsEl = el.Element("Options");
        if (optionsEl is not null)
        {
            if (optionsEl.Attribute("moHideChildren")?.Value == "1")
                options |= CheatEntryOptions.HideChildren;
            if (optionsEl.Attribute("moActivateChildrenAsWell")?.Value == "1")
                options |= CheatEntryOptions.ActivateChildrenAsWell;
            if (optionsEl.Attribute("moDeactivateChildrenAsWell")?.Value == "1")
                options |= CheatEntryOptions.DeactivateChildrenAsWell;
            if (optionsEl.Attribute("moRecursiveSetValue")?.Value == "1")
                options |= CheatEntryOptions.RecursiveSetValue;
            if (optionsEl.Attribute("moAllowManualCollapseAndExpand")?.Value == "1")
                options |= CheatEntryOptions.AllowManualCollapseAndExpand;
        }

        // Hotkeys
        var hotkeys = new List<CheatEntryHotkey>();
        var hotkeysEl = el.Element("Hotkeys");
        if (hotkeysEl is not null)
        {
            foreach (var hkEl in hotkeysEl.Elements("Hotkey"))
            {
                var action = hkEl.Element("Action")?.Value ?? "";
                var hkId = int.TryParse(hkEl.Element("ID")?.Value, out var hid) ? hid : 0;
                var value = hkEl.Element("Value")?.Value;
                var keys = new List<int>();
                var keysEl = hkEl.Element("Keys");
                if (keysEl is not null)
                {
                    foreach (var keyEl in keysEl.Elements("Key"))
                    {
                        if (int.TryParse(keyEl.Value, out var k))
                            keys.Add(k);
                    }
                }
                hotkeys.Add(new CheatEntryHotkey(action, keys, value, hkId));
            }
        }

        // Phase 2: String config
        int? stringLength = null;
        if (int.TryParse(el.Element("Length")?.Value, out var sl))
            stringLength = sl;
        bool? isUnicode = el.Element("Unicode")?.Value is string uv ? uv == "1" : null;
        bool? zeroTerminate = el.Element("ZeroTerminate")?.Value is string zt ? zt == "1" : null;

        // ByteLength
        int? byteLength = null;
        if (int.TryParse(el.Element("ByteLength")?.Value, out var bl))
            byteLength = bl;

        // Bit fields
        int? bitStart = null;
        if (int.TryParse(el.Element("BitStart")?.Value, out var bs))
            bitStart = bs;
        int? bitLength = null;
        if (int.TryParse(el.Element("BitLength")?.Value, out var blen))
            bitLength = blen;
        bool showAsBinary = el.Element("ShowAsBinary")?.Value == "1";

        // CustomType
        var customTypeName = el.Element("CustomType")?.Value;

        // Comment
        var comment = el.Element("Comment")?.Value;

        // Parse nested child entries
        var children = new List<CheatTableEntry>();
        var childEntriesEl = el.Element("CheatEntries");
        if (childEntriesEl is not null)
        {
            children.AddRange(ParseEntries(childEntriesEl));
        }

        var dataType = MapVariableType(variableType);

        // Step 0: Collect unknown entry-level elements
        var preservedElements = el.Elements()
            .Where(e => !KnownEntryElements.Contains(e.Name.LocalName))
            .Select(e => new XElement(e))
            .ToList();

        return new CheatTableEntry(
            id, description, address, dataType, lastValue,
            isPointer, offsets, isGroupHeader, assemblerScript, children,
            showAsSigned, showAsHex, dropDownList, dropDownListLink, dropDownText,
            PreservedElements: preservedElements.Count > 0 ? preservedElements : null,
            Color: color,
            Options: options,
            Hotkeys: hotkeys.Count > 0 ? hotkeys : null,
            LastRealAddress: lastRealAddress,
            LastActivated: lastActivated,
            RichOffsets: richOffsets.Count > 0 ? richOffsets : null,
            StringLength: stringLength,
            IsUnicode: isUnicode,
            ZeroTerminate: zeroTerminate,
            ByteLength: byteLength,
            BitStart: bitStart,
            BitLength: bitLength,
            ShowAsBinary: showAsBinary,
            CustomTypeName: customTypeName,
            DropDownDescriptionOnly: dropDownDescriptionOnly,
            DropDownReadOnly: dropDownReadOnly,
            DropDownDisplayValueAsItem: dropDownDisplayValueAsItem,
            ScriptAsync: scriptAsync,
            Comment: comment);
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
        // CE wraps descriptions in quotes: "Health" -> Health
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
