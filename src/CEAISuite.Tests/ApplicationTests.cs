using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

public class AddressTableServiceTests
{
    private readonly AddressTableService _sut;

    public AddressTableServiceTests()
    {
        _sut = new AddressTableService(new StubEngineFacade());
    }

    [Fact]
    public void AddEntry_CreatesEntryWithCorrectValues()
    {
        var entry = _sut.AddEntry("0x400000", MemoryDataType.Int32, "100", "Health");

        Assert.Equal("Health", entry.Label);
        Assert.Equal("0x400000", entry.Address);
        Assert.Equal(MemoryDataType.Int32, entry.DataType);
        Assert.Equal("100", entry.CurrentValue);
        Assert.False(entry.IsLocked);
        Assert.Single(_sut.Entries);
    }

    [Fact]
    public void AddEntry_WithoutLabel_GeneratesDefaultLabel()
    {
        var entry = _sut.AddEntry("0x500000", MemoryDataType.Float, "3.14");

        Assert.StartsWith("Address_", entry.Label);
    }

    [Fact]
    public void RemoveEntry_RemovesCorrectEntry()
    {
        var e1 = _sut.AddEntry("0x100", MemoryDataType.Int32, "1", "A");
        var e2 = _sut.AddEntry("0x200", MemoryDataType.Int32, "2", "B");

        _sut.RemoveEntry(e1.Id);

        Assert.Single(_sut.Entries);
        Assert.Equal("B", _sut.Entries[0].Label);
    }

    [Fact]
    public void ToggleLock_LocksAndUnlocks()
    {
        var entry = _sut.AddEntry("0x100", MemoryDataType.Int32, "42", "Score");

        _sut.ToggleLock(entry.Id);
        Assert.True(_sut.Entries[0].IsLocked);
        Assert.Equal("42", _sut.Entries[0].LockedValue);

        _sut.ToggleLock(entry.Id);
        Assert.False(_sut.Entries[0].IsLocked);
        Assert.Null(_sut.Entries[0].LockedValue);
    }

    [Fact]
    public void UpdateLabel_ChangesLabel()
    {
        var entry = _sut.AddEntry("0x100", MemoryDataType.Int32, "0", "Old");

        _sut.UpdateLabel(entry.Id, "New");

        Assert.Equal("New", _sut.Entries[0].Label);
    }

    [Fact]
    public async Task RefreshAll_UpdatesValues()
    {
        var engine = new StubEngineFacade();
        var sut = new AddressTableService(engine);

        engine.WriteMemoryDirect((nuint)0x100, BitConverter.GetBytes(999));
        sut.AddEntry("0x100", MemoryDataType.Int32, "0", "TestAddr");

        await sut.RefreshAllAsync(1000);

        Assert.Equal("999", sut.Entries[0].CurrentValue);
        Assert.Equal("0", sut.Entries[0].PreviousValue);
    }

    [Fact]
    public void CreateGroup_AddsGroupNode()
    {
        var group = _sut.CreateGroup("Player Stats");
        Assert.True(group.IsGroup);
        Assert.Equal("Player Stats", group.Label);
        Assert.Single(_sut.Roots);
        Assert.Empty(_sut.Entries); // groups don't appear in flat Entries
    }

    [Fact]
    public void AddEntryToGroup_NestsCorrectly()
    {
        var group = _sut.CreateGroup("Player");
        _sut.AddEntryToGroup(group.Id, "0x100", MemoryDataType.Int32, "100", "Health");
        _sut.AddEntryToGroup(group.Id, "0x200", MemoryDataType.Int32, "50", "Mana");

        Assert.Single(_sut.Roots); // one root (the group)
        Assert.Equal(2, _sut.Roots[0].Children.Count);
        Assert.Equal(2, _sut.Entries.Count); // flat view sees 2 leaf entries
    }

    [Fact]
    public void MoveToGroup_RelocatesEntry()
    {
        var entry = _sut.AddEntry("0x100", MemoryDataType.Int32, "42", "Score");
        var group = _sut.CreateGroup("Stats");

        _sut.MoveToGroup(entry.Id, group.Id);

        Assert.Single(_sut.Roots); // only the group at root
        Assert.Single(group.Children);
        Assert.Equal("Score", group.Children[0].Label);
    }

    [Fact]
    public void CreateSubGroup_NestsInsideParent()
    {
        var parent = _sut.CreateGroup("Game");
        var child = _sut.CreateSubGroup(parent.Id, "Player");

        Assert.Single(_sut.Roots);
        Assert.Single(parent.Children);
        Assert.True(child.IsGroup);
        Assert.Equal("Player", child.Label);
    }

    [Fact]
    public async Task RefreshAll_ResolvesModuleRelativeAddress()
    {
        var engine = new StubEngineFacade();
        var sut = new AddressTableService(engine);
        // Module "main.exe" at base 0x400000
        sut.SetProcessContext(
            new[] { new ModuleDescriptor("main.exe", (nuint)0x400000, 4096) },
            is32Bit: false);

        // Write value 42 at main.exe+0x100 = 0x400100
        engine.WriteMemoryDirect((nuint)0x400100, BitConverter.GetBytes(42));

        var node = new AddressTableNode("test1", "Test", false)
        {
            Address = "main.exe+100",
            DataType = MemoryDataType.Int32,
            CurrentValue = "0"
        };
        sut.Roots.Add(node);

        await sut.RefreshAllAsync(1000);

        Assert.Equal("42", node.CurrentValue);
        Assert.Equal((nuint)0x400100, node.ResolvedAddress);
    }

    [Fact]
    public async Task RefreshAll_ResolvesPointerChain()
    {
        var engine = new StubEngineFacade();
        var sut = new AddressTableService(engine);
        sut.SetProcessContext(
            new[] { new ModuleDescriptor("game.dll", (nuint)0x10000000, 0x1000000) },
            is32Bit: false);

        // Pointer chain: game.dll+0x100 → read ptr → 0x20000000, add offset 0x30 → 0x20000030
        // Write a pointer at game.dll+0x100 = 0x10000100 pointing to 0x20000000
        engine.WriteMemoryDirect((nuint)0x10000100, BitConverter.GetBytes((ulong)0x20000000));
        // Write the actual value (99) at 0x20000030
        engine.WriteMemoryDirect((nuint)0x20000030, BitConverter.GetBytes(99));

        // CE stores offsets deepest-first, so offset [30] means: read ptr at base, add 0x30
        var node = new AddressTableNode("ptr1", "PtrValue", false)
        {
            Address = "game.dll+100",
            DataType = MemoryDataType.Int32,
            CurrentValue = "0",
            IsPointer = true,
            PointerOffsets = new List<long> { 0x30 }  // single-level pointer
        };
        sut.Roots.Add(node);

        await sut.RefreshAllAsync(1000);

        Assert.Equal("99", node.CurrentValue);
        Assert.Equal((nuint)0x20000030, node.ResolvedAddress);
    }

    [Fact]
    public async Task RefreshAll_ResolvesMultiLevelPointer()
    {
        var engine = new StubEngineFacade();
        var sut = new AddressTableService(engine);
        sut.SetProcessContext(
            new[] { new ModuleDescriptor("game.dll", (nuint)0x10000000, 0x1000000) },
            is32Bit: false);

        // Two-level pointer chain:
        // Base: game.dll+0x200 = 0x10000200
        // Level 1: read ptr at 0x10000200 → 0x30000000, add 0xB8 → 0x300000B8
        // Level 2: read ptr at 0x300000B8 → 0x40000000, add 0x08 → 0x40000008
        // Final value at 0x40000008 = 777

        engine.WriteMemoryDirect((nuint)0x10000200, BitConverter.GetBytes((ulong)0x30000000));
        engine.WriteMemoryDirect((nuint)0x300000B8, BitConverter.GetBytes((ulong)0x40000000));
        engine.WriteMemoryDirect((nuint)0x40000008, BitConverter.GetBytes(777));

        // CE stores offsets deepest-first: [8, B8]
        // Resolution reverses: first use B8, then use 8
        var node = new AddressTableNode("ptr2", "DeepPtr", false)
        {
            Address = "game.dll+200",
            DataType = MemoryDataType.Int32,
            CurrentValue = "0",
            IsPointer = true,
            PointerOffsets = new List<long> { 0x08, 0xB8 }
        };
        sut.Roots.Add(node);

        await sut.RefreshAllAsync(1000);

        Assert.Equal("777", node.CurrentValue);
        Assert.Equal((nuint)0x40000008, node.ResolvedAddress);
    }
}

public class CheatTablePointerTests
{
    [Fact]
    public void ParsedCT_PreservesPointerData()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""44"">
  <CheatEntries>
    <CheatEntry>
      <ID>1</ID>
      <Description>""Health""</Description>
      <VariableType>4 Bytes</VariableType>
      <Address>""GameAssembly.dll""+02E9EBB0</Address>
      <Offsets>
        <Offset>0</Offset>
        <Offset>B8</Offset>
      </Offsets>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var parser = new CheatTableParser();
        var ct = parser.Parse(xml);
        var nodes = parser.ToAddressTableNodes(ct);

        Assert.Single(nodes);
        var node = nodes[0];
        Assert.True(node.IsPointer);
        Assert.Equal(2, node.PointerOffsets.Count);
        Assert.Equal(0x00, node.PointerOffsets[0]); // deepest offset
        Assert.Equal(0xB8, node.PointerOffsets[1]); // shallowest offset
        // Address should be the raw base, not concatenated
        Assert.Contains("GameAssembly.dll", node.Address);
        Assert.DoesNotContain("+0+", node.Address); // no flattened format
    }

    [Fact]
    public void ParsedCT_GroupPreservesPointerData()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""44"">
  <CheatEntries>
    <CheatEntry>
      <ID>1</ID>
      <Description>""Characters""</Description>
      <GroupHeader>1</GroupHeader>
      <Address>""GameAssembly.dll""+02E9EBB0</Address>
      <Offsets>
        <Offset>20</Offset>
        <Offset>10</Offset>
        <Offset>18</Offset>
      </Offsets>
      <CheatEntries>
        <CheatEntry>
          <ID>2</ID>
          <Description>""EXP""</Description>
          <VariableType>4 Bytes</VariableType>
          <Address>+38</Address>
        </CheatEntry>
      </CheatEntries>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var parser = new CheatTableParser();
        var ct = parser.Parse(xml);
        var nodes = parser.ToAddressTableNodes(ct);

        Assert.Single(nodes);
        var group = nodes[0];

        // Group should preserve its pointer chain
        Assert.True(group.IsGroup);
        Assert.True(group.IsPointer);
        Assert.Equal(3, group.PointerOffsets.Count);
        Assert.Contains("GameAssembly.dll", group.Address);

        // Child should be marked as offset and have parent reference
        Assert.Single(group.Children);
        var child = group.Children[0];
        Assert.True(child.IsOffset);
        Assert.Equal("+38", child.Address);
        Assert.Same(group, child.Parent);
    }
}

public class AddressTableExportServiceTests
{
    [Fact]
    public void RoundTrip_ExportImport_PreservesEntries()
    {
        var sut = new AddressTableExportService();
        var entries = new[]
        {
            new AddressTableEntry("id1", "Health", "0x400000", MemoryDataType.Int32, "100", null, "player health", false, null),
            new AddressTableEntry("id2", "Speed", "0x400010", MemoryDataType.Float, "3.5", null, null, true, "3.5")
        };

        var json = sut.ExportToJson(entries);
        var imported = sut.ImportFromJson(json);

        Assert.Equal(2, imported.Count);
        Assert.Equal("Health", imported[0].Label);
        Assert.Equal("0x400000", imported[0].Address);
        Assert.Equal(MemoryDataType.Int32, imported[0].DataType);
        Assert.Equal("Speed", imported[1].Label);
        Assert.True(imported[1].IsLocked);
    }
}

public class ScriptGenerationServiceTests
{
    [Fact]
    public void GenerateTrainer_IncludesLockedEntries()
    {
        var sut = new ScriptGenerationService();
        var entries = new[]
        {
            new AddressTableEntry("id1", "Health", "0x400000", MemoryDataType.Int32, "999", null, null, true, "999")
        };

        var script = sut.GenerateTrainerScript(entries, "TestGame.exe");

        Assert.Contains("TestGame", script);
        Assert.Contains("WriteValue", script);
        Assert.Contains("Health", script);
    }

    [Fact]
    public void GenerateAutoAssemblerScript_IncludesAllocAndDealloc()
    {
        var sut = new ScriptGenerationService();
        var entries = new[]
        {
            new AddressTableEntry("id1", "Health", "0x400000", MemoryDataType.Int32, "999", null, null, true, "999")
        };

        var script = sut.GenerateAutoAssemblerScript(entries, "TestGame.exe");

        Assert.Contains("[ENABLE]", script);
        Assert.Contains("[DISABLE]", script);
        Assert.Contains("alloc(", script);
        Assert.Contains("dealloc(", script);
        Assert.Contains("Health", script);
    }

    [Fact]
    public void GenerateLuaScript_IncludesWriteFunction()
    {
        var sut = new ScriptGenerationService();
        var entries = new[]
        {
            new AddressTableEntry("id1", "Health", "0x400000", MemoryDataType.Int32, "999", null, null, true, "999"),
            new AddressTableEntry("id2", "Speed", "0x400010", MemoryDataType.Float, "3.5", null, null, true, "3.5")
        };

        var script = sut.GenerateLuaScript(entries, "TestGame.exe");

        Assert.Contains("writeInteger", script);
        Assert.Contains("writeFloat", script);
        Assert.Contains("Health", script);
        Assert.Contains("openProcess", script);
    }

    [Fact]
    public void SummarizeInvestigation_ReturnsMarkdownSummary()
    {
        var sut = new ScriptGenerationService();
        var entries = new[]
        {
            new AddressTableEntry("id1", "Health", "0x400000", MemoryDataType.Int32, "999", null, null, true, "999")
        };

        var summary = sut.SummarizeInvestigation("TestGame.exe", 1234, entries, null, null);

        Assert.Contains("# Investigation Summary", summary);
        Assert.Contains("TestGame.exe", summary);
        Assert.Contains("Health", summary);
        Assert.Contains("[LOCKED]", summary);
    }
}

public class SessionServiceTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTrip()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ceai_test_{Guid.NewGuid():N}.db");
        var repo = new CEAISuite.Persistence.Sqlite.SqliteInvestigationSessionRepository(dbPath);
        await repo.InitializeAsync();
        var sut = new SessionService(repo);

        var entries = new[]
        {
            new AddressTableEntry("a1", "TestAddr", "0x1000", MemoryDataType.Int32, "42", null, "note", false, null)
        };

        var sessionId = await sut.SaveSessionAsync("game.exe", 1234, entries, Array.Empty<AiActionLogEntry>());
        Assert.NotNull(sessionId);

        var sessions = await sut.ListSessionsAsync();
        Assert.True(sessions.Count >= 1);

        var loaded = await sut.LoadSessionAsync(sessionId);
        Assert.NotNull(loaded);
        Assert.Single(loaded.Value.Entries);
        Assert.Equal("TestAddr", loaded.Value.Entries[0].Label);
        Assert.Equal("0x1000", loaded.Value.Entries[0].Address);
        Assert.Equal("game.exe", loaded.Value.ProcessName);

        // Cleanup is best-effort; SQLite may hold the file briefly
        try { File.Delete(dbPath); } catch { /* ignore */ }
    }
}

public class CheatTableParserTests
{
    private const string SampleCtXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <CheatTable CheatEngineTableVersion="44">
          <CheatEntries>
            <CheatEntry>
              <ID>0</ID>
              <Description>"Player Stats"</Description>
              <GroupHeader>1</GroupHeader>
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"Health"</Description>
                  <LastState Value="100" RealAddress="00400000"/>
                  <VariableType>4 Bytes</VariableType>
                  <Address>00400000</Address>
                </CheatEntry>
                <CheatEntry>
                  <ID>2</ID>
                  <Description>"Speed"</Description>
                  <LastState Value="1.5" RealAddress="00400010"/>
                  <VariableType>Float</VariableType>
                  <Address>00400010</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatEntry>
            <CheatEntry>
              <ID>3</ID>
              <Description>"Money"</Description>
              <VariableType>4 Bytes</VariableType>
              <Address>game.exe+123456</Address>
              <Offsets>
                <Offset>10</Offset>
                <Offset>1C</Offset>
              </Offsets>
            </CheatEntry>
            <CheatEntry>
              <ID>4</ID>
              <Description>"God Mode"</Description>
              <VariableType>Auto Assembler Script</VariableType>
              <AssemblerScript><![CDATA[
              [ENABLE]
              nop
              [DISABLE]
              ]]></AssemblerScript>
              <Address>0</Address>
            </CheatEntry>
          </CheatEntries>
          <LuaScript>print("hello")</LuaScript>
        </CheatTable>
        """;

    [Fact]
    public void Parse_ReturnsCorrectTableVersion()
    {
        var parser = new CheatTableParser();
        var ct = parser.Parse(SampleCtXml, "test.ct");

        Assert.Equal(44, ct.TableVersion);
        Assert.Equal("test.ct", ct.FileName);
    }

    [Fact]
    public void Parse_ParsesTopLevelAndNestedEntries()
    {
        var parser = new CheatTableParser();
        var ct = parser.Parse(SampleCtXml);

        // 3 top-level entries (group + money + script)
        Assert.Equal(3, ct.Entries.Count);
        // Total = group + 2 children + money + script = 5
        Assert.Equal(5, ct.TotalEntryCount);
    }

    [Fact]
    public void Parse_RecognizesGroupHeaders()
    {
        var parser = new CheatTableParser();
        var ct = parser.Parse(SampleCtXml);

        var group = ct.Entries[0];
        Assert.True(group.IsGroupHeader);
        Assert.Equal("Player Stats", group.Description);
        Assert.Equal(2, group.Children.Count);
    }

    [Fact]
    public void Parse_ParsesPointerOffsets()
    {
        var parser = new CheatTableParser();
        var ct = parser.Parse(SampleCtXml);

        var money = ct.Entries[1];
        Assert.True(money.IsPointer);
        Assert.Equal(2, money.PointerOffsets.Count);
        Assert.Equal("10", money.PointerOffsets[0]);
        Assert.Equal("1C", money.PointerOffsets[1]);
    }

    [Fact]
    public void Parse_ParsesAssemblerScript()
    {
        var parser = new CheatTableParser();
        var ct = parser.Parse(SampleCtXml);

        var script = ct.Entries[2];
        Assert.NotNull(script.AssemblerScript);
        Assert.Contains("[ENABLE]", script.AssemblerScript);
    }

    [Fact]
    public void Parse_CapturesLuaScript()
    {
        var parser = new CheatTableParser();
        var ct = parser.Parse(SampleCtXml);

        Assert.NotNull(ct.LuaScript);
        Assert.Contains("hello", ct.LuaScript);
    }

    [Fact]
    public void ToAddressTableEntries_FlattensGroupsAndIncludesScripts()
    {
        var parser = new CheatTableParser();
        var ct = parser.Parse(SampleCtXml);
        var entries = parser.ToAddressTableEntries(ct);

        // Health, Speed (from group), Money, God Mode script = 4 entries
        Assert.Equal(4, entries.Count);

        // Group children get prefixed labels
        Assert.Equal("Player Stats/Health", entries[0].Label);
        Assert.Equal("Player Stats/Speed", entries[1].Label);
        Assert.Equal(MemoryDataType.Float, entries[1].DataType);

        // Pointer entry has notes
        Assert.Equal("Money", entries[2].Label);
        Assert.NotNull(entries[2].Notes);
        Assert.Contains("Pointer", entries[2].Notes);

        // Script entry is included with placeholder address
        Assert.Equal("God Mode", entries[3].Label);
        Assert.Equal("(script)", entries[3].Address);
    }

    [Fact]
    public void Parse_MapsVariableTypesCorrectly()
    {
        var parser = new CheatTableParser();
        var ct = parser.Parse(SampleCtXml);

        var group = ct.Entries[0];
        Assert.Equal(MemoryDataType.Int32, group.Children[0].DataType); // 4 Bytes
        Assert.Equal(MemoryDataType.Float, group.Children[1].DataType); // Float
    }

    [Fact]
    public void Parse_CleansQuotedDescriptions()
    {
        var parser = new CheatTableParser();
        var ct = parser.Parse(SampleCtXml);

        // Descriptions should not have surrounding quotes
        Assert.Equal("Player Stats", ct.Entries[0].Description);
        Assert.Equal("Health", ct.Entries[0].Children[0].Description);
    }

    [Fact]
    public void ToAddressTableNodes_PreservesScriptEntries()
    {
        var parser = new CheatTableParser();
        var ct = parser.Parse(SampleCtXml);
        var nodes = parser.ToAddressTableNodes(ct);

        // 3 top-level nodes: group (Player Stats), Money, God Mode script
        Assert.Equal(3, nodes.Count);

        // God Mode script node
        var scriptNode = nodes[2];
        Assert.True(scriptNode.IsScriptEntry);
        Assert.Equal("(script)", scriptNode.Address);
        Assert.Contains("[ENABLE]", scriptNode.AssemblerScript);
        Assert.Equal("God Mode", scriptNode.Label);
    }

    [Fact]
    public void ToAddressTableNodes_PreservesGroupHierarchy()
    {
        var parser = new CheatTableParser();
        var ct = parser.Parse(SampleCtXml);
        var nodes = parser.ToAddressTableNodes(ct);

        var group = nodes[0];
        Assert.True(group.IsGroup);
        Assert.Equal("Player Stats", group.Label);
        Assert.Equal(2, group.Children.Count);
        Assert.Equal("Health", group.Children[0].Label);
        Assert.Equal("Speed", group.Children[1].Label);
    }

    [Fact]
    public void ScriptNode_DisplayHelpers_ShowCorrectValues()
    {
        var node = new AddressTableNode("s1", "EXP Multiplier", false)
        {
            AssemblerScript = "[ENABLE]\nalloc(newmem,1024)\n[DISABLE]\ndealloc(newmem)"
        };

        Assert.True(node.IsScriptEntry);
        Assert.Equal("📜", node.DisplayIcon);
        Assert.Equal("AA Script", node.DisplayType);
        Assert.Equal("[DISABLED]", node.DisplayValue);

        node.IsScriptEnabled = true;
        Assert.Equal("[ENABLED]", node.DisplayValue);
    }
}

public class CheatTableExporterTests
{
    [Fact]
    public void RoundTrip_PreservesGroupHierarchy()
    {
        // Arrange: build a group with two children
        var group = new AddressTableNode("g1", "Player Stats", true);
        var health = new AddressTableNode("h1", "Health", false)
        {
            Address = "00400000",
            DataType = Engine.Abstractions.MemoryDataType.Int32,
            CurrentValue = "100"
        };
        var speed = new AddressTableNode("s1", "Speed", false)
        {
            Address = "00400010",
            DataType = Engine.Abstractions.MemoryDataType.Float,
            CurrentValue = "1.5"
        };
        group.Children.Add(health);
        group.Children.Add(speed);

        // Act: export → re-import
        var exporter = new CheatTableExporter();
        var xml = exporter.ExportToXml(new[] { group });

        var parser = new CheatTableParser();
        var ct = parser.Parse(xml);
        var nodes = parser.ToAddressTableNodes(ct);

        // Assert
        Assert.Single(nodes);
        var importedGroup = nodes[0];
        Assert.True(importedGroup.IsGroup);
        Assert.Equal("Player Stats", importedGroup.Label);
        Assert.Equal(2, importedGroup.Children.Count);
        Assert.Equal("Health", importedGroup.Children[0].Label);
        Assert.Equal(Engine.Abstractions.MemoryDataType.Int32, importedGroup.Children[0].DataType);
        Assert.Equal("Speed", importedGroup.Children[1].Label);
        Assert.Equal(Engine.Abstractions.MemoryDataType.Float, importedGroup.Children[1].DataType);
    }

    [Fact]
    public void RoundTrip_PreservesPointerEntry()
    {
        var node = new AddressTableNode("p1", "Money", false)
        {
            Address = "game.exe+123456",
            DataType = Engine.Abstractions.MemoryDataType.Int32,
            IsPointer = true,
            PointerOffsets = new List<long> { 0x10, 0x1C }
        };

        var exporter = new CheatTableExporter();
        var xml = exporter.ExportToXml(new[] { node });

        var parser = new CheatTableParser();
        var ct = parser.Parse(xml);
        var nodes = parser.ToAddressTableNodes(ct);

        Assert.Single(nodes);
        var imported = nodes[0];
        Assert.Equal("Money", imported.Label);
        Assert.Equal("game.exe+123456", imported.Address);
        Assert.True(imported.IsPointer);
        Assert.Equal(2, imported.PointerOffsets.Count);
        Assert.Equal(0x10, imported.PointerOffsets[0]);
        Assert.Equal(0x1C, imported.PointerOffsets[1]);
    }

    [Fact]
    public void RoundTrip_PreservesScriptEntry()
    {
        var node = new AddressTableNode("sc1", "God Mode", false)
        {
            AssemblerScript = "[ENABLE]\nnop\n[DISABLE]\ndb 90"
        };

        var exporter = new CheatTableExporter();
        var xml = exporter.ExportToXml(new[] { node });

        var parser = new CheatTableParser();
        var ct = parser.Parse(xml);
        var nodes = parser.ToAddressTableNodes(ct);

        Assert.Single(nodes);
        var imported = nodes[0];
        Assert.Equal("God Mode", imported.Label);
        Assert.True(imported.IsScriptEntry);
        Assert.Contains("[ENABLE]", imported.AssemblerScript);
        Assert.Contains("[DISABLE]", imported.AssemblerScript);
    }

    [Fact]
    public void RoundTrip_PreservesShowAsSigned()
    {
        var node = new AddressTableNode("u1", "Unsigned Val", false)
        {
            Address = "00500000",
            DataType = Engine.Abstractions.MemoryDataType.Int32,
            ShowAsSigned = false
        };

        var exporter = new CheatTableExporter();
        var xml = exporter.ExportToXml(new[] { node });

        var parser = new CheatTableParser();
        var ct = parser.Parse(xml);
        var nodes = parser.ToAddressTableNodes(ct);

        Assert.Single(nodes);
        Assert.False(nodes[0].ShowAsSigned);
    }

    [Fact]
    public void RoundTrip_PreservesDataTypes()
    {
        var types = new (Engine.Abstractions.MemoryDataType Type, string Label)[]
        {
            (Engine.Abstractions.MemoryDataType.Byte, "ByteVal"),
            (Engine.Abstractions.MemoryDataType.Int16, "ShortVal"),
            (Engine.Abstractions.MemoryDataType.Int32, "IntVal"),
            (Engine.Abstractions.MemoryDataType.Int64, "LongVal"),
            (Engine.Abstractions.MemoryDataType.Float, "FloatVal"),
            (Engine.Abstractions.MemoryDataType.Double, "DoubleVal"),
            (Engine.Abstractions.MemoryDataType.String, "StringVal"),
        };

        var roots = types.Select((t, i) => new AddressTableNode($"dt{i}", t.Label, false)
        {
            Address = $"0040000{i}",
            DataType = t.Type
        }).ToArray();

        var exporter = new CheatTableExporter();
        var xml = exporter.ExportToXml(roots);

        var parser = new CheatTableParser();
        var ct = parser.Parse(xml);
        var nodes = parser.ToAddressTableNodes(ct);

        Assert.Equal(types.Length, nodes.Count);
        for (int i = 0; i < types.Length; i++)
        {
            Assert.Equal(types[i].Type, nodes[i].DataType);
            Assert.Equal(types[i].Label, nodes[i].Label);
        }
    }

    [Fact]
    public void RoundTrip_FullTable_PreservesStructure()
    {
        // Build a complex table: group with children, pointer entry, script entry
        var group = new AddressTableNode("g1", "Stats", true);
        group.Children.Add(new AddressTableNode("c1", "HP", false)
        {
            Address = "00400000",
            DataType = Engine.Abstractions.MemoryDataType.Int32,
            CurrentValue = "100"
        });
        group.Children.Add(new AddressTableNode("c2", "MP", false)
        {
            Address = "00400004",
            DataType = Engine.Abstractions.MemoryDataType.Float,
            CurrentValue = "50.5"
        });

        var pointer = new AddressTableNode("p1", "Gold", false)
        {
            Address = "GameAssembly.dll+ABC000",
            DataType = Engine.Abstractions.MemoryDataType.Int64,
            IsPointer = true,
            PointerOffsets = new List<long> { 0xB8, 0x20, 0x0 }
        };

        var script = new AddressTableNode("s1", "Inf Health", false)
        {
            AssemblerScript = "[ENABLE]\naobscanmodule(health_aob,game.exe,89 50 04)\n[DISABLE]\nhealth_aob:\ndb 89 50 04"
        };

        var roots = new[] { group, pointer, script };

        var exporter = new CheatTableExporter();
        var xml = exporter.ExportToXml(roots);

        var parser = new CheatTableParser();
        var ct = parser.Parse(xml);
        var reimported = parser.ToAddressTableNodes(ct);

        // Verify structure
        Assert.Equal(3, reimported.Count);

        // Group
        Assert.True(reimported[0].IsGroup);
        Assert.Equal("Stats", reimported[0].Label);
        Assert.Equal(2, reimported[0].Children.Count);
        Assert.Equal("HP", reimported[0].Children[0].Label);
        Assert.Equal("MP", reimported[0].Children[1].Label);

        // Pointer
        Assert.Equal("Gold", reimported[1].Label);
        Assert.True(reimported[1].IsPointer);
        Assert.Equal(3, reimported[1].PointerOffsets.Count);
        Assert.Equal(0xB8, reimported[1].PointerOffsets[0]);
        Assert.Equal(0x20, reimported[1].PointerOffsets[1]);
        Assert.Equal(0x0, reimported[1].PointerOffsets[2]);

        // Script
        Assert.True(reimported[2].IsScriptEntry);
        Assert.Equal("Inf Health", reimported[2].Label);
        Assert.Contains("[ENABLE]", reimported[2].AssemblerScript);
    }
}
