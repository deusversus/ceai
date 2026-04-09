using System.IO;
using System.Xml.Linq;
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

        await sut.RefreshAllAsync(1000, TestContext.Current.CancellationToken);

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

        await sut.RefreshAllAsync(1000, TestContext.Current.CancellationToken);

        Assert.Equal("42", node.CurrentValue);
        Assert.Equal((nuint)0x400100, node.ResolvedAddress);
    }

    [Fact]
    public async Task RefreshAll_ResolvesPointerChain()
    {
        var engine = new StubEngineFacade();
        engine.AttachModules = new[] { new ModuleDescriptor("game.dll", (nuint)0x10000000, 0x1000000) };
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

        await sut.RefreshAllAsync(1000, TestContext.Current.CancellationToken);

        Assert.Equal("99", node.CurrentValue);
        Assert.Equal((nuint)0x20000030, node.ResolvedAddress);
    }

    [Fact]
    public async Task RefreshAll_ResolvesMultiLevelPointer()
    {
        var engine = new StubEngineFacade();
        engine.AttachModules = new[] { new ModuleDescriptor("game.dll", (nuint)0x10000000, 0x1000000) };
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

        await sut.RefreshAllAsync(1000, TestContext.Current.CancellationToken);

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
        var ct = CheatTableParser.Parse(xml);
        var nodes = CheatTableParser.ToAddressTableNodes(ct);

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
        var ct = CheatTableParser.Parse(xml);
        var nodes = CheatTableParser.ToAddressTableNodes(ct);

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

        var json = AddressTableExportService.ExportToJson(entries);
        var imported = AddressTableExportService.ImportFromJson(json);

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

        var script = ScriptGenerationService.GenerateTrainerScript(entries, "TestGame.exe");

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

        var script = ScriptGenerationService.GenerateAutoAssemblerScript(entries, "TestGame.exe");

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

        var script = ScriptGenerationService.GenerateLuaScript(entries, "TestGame.exe");

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

        var summary = ScriptGenerationService.SummarizeInvestigation("TestGame.exe", 1234, entries, null, null);

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
        await repo.InitializeAsync(TestContext.Current.CancellationToken);
        var sut = new SessionService(repo);

        var entries = new[]
        {
            new AddressTableEntry("a1", "TestAddr", "0x1000", MemoryDataType.Int32, "42", null, "note", false, null)
        };

        var sessionId = await sut.SaveSessionAsync("game.exe", 1234, entries, Array.Empty<AiActionLogEntry>(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(sessionId);

        var sessions = await sut.ListSessionsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(sessions.Count >= 1);

        var loaded = await sut.LoadSessionAsync(sessionId, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Single(loaded.Value.Entries);
        Assert.Equal("TestAddr", loaded.Value.Entries[0].Label);
        Assert.Equal("0x1000", loaded.Value.Entries[0].Address);
        Assert.Equal("game.exe", loaded.Value.ProcessName);

        // Cleanup is best-effort; SQLite may hold the file briefly
        try { File.Delete(dbPath); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[ApplicationTests] Failed to cleanup test DB: {ex.Message}"); }
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
        var ct = CheatTableParser.Parse(SampleCtXml, "test.ct");

        Assert.Equal(44, ct.TableVersion);
        Assert.Equal("test.ct", ct.FileName);
    }

    [Fact]
    public void Parse_ParsesTopLevelAndNestedEntries()
    {
        var parser = new CheatTableParser();
        var ct = CheatTableParser.Parse(SampleCtXml);

        // 3 top-level entries (group + money + script)
        Assert.Equal(3, ct.Entries.Count);
        // Total = group + 2 children + money + script = 5
        Assert.Equal(5, ct.TotalEntryCount);
    }

    [Fact]
    public void Parse_RecognizesGroupHeaders()
    {
        var parser = new CheatTableParser();
        var ct = CheatTableParser.Parse(SampleCtXml);

        var group = ct.Entries[0];
        Assert.True(group.IsGroupHeader);
        Assert.Equal("Player Stats", group.Description);
        Assert.Equal(2, group.Children.Count);
    }

    [Fact]
    public void Parse_ParsesPointerOffsets()
    {
        var parser = new CheatTableParser();
        var ct = CheatTableParser.Parse(SampleCtXml);

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
        var ct = CheatTableParser.Parse(SampleCtXml);

        var script = ct.Entries[2];
        Assert.NotNull(script.AssemblerScript);
        Assert.Contains("[ENABLE]", script.AssemblerScript);
    }

    [Fact]
    public void Parse_CapturesLuaScript()
    {
        var parser = new CheatTableParser();
        var ct = CheatTableParser.Parse(SampleCtXml);

        Assert.NotNull(ct.LuaScript);
        Assert.Contains("hello", ct.LuaScript);
    }

    [Fact]
    public void ToAddressTableEntries_FlattensGroupsAndIncludesScripts()
    {
        var parser = new CheatTableParser();
        var ct = CheatTableParser.Parse(SampleCtXml);
        var entries = CheatTableParser.ToAddressTableEntries(ct);

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
        var ct = CheatTableParser.Parse(SampleCtXml);

        var group = ct.Entries[0];
        Assert.Equal(MemoryDataType.Int32, group.Children[0].DataType); // 4 Bytes
        Assert.Equal(MemoryDataType.Float, group.Children[1].DataType); // Float
    }

    [Fact]
    public void Parse_CleansQuotedDescriptions()
    {
        var parser = new CheatTableParser();
        var ct = CheatTableParser.Parse(SampleCtXml);

        // Descriptions should not have surrounding quotes
        Assert.Equal("Player Stats", ct.Entries[0].Description);
        Assert.Equal("Health", ct.Entries[0].Children[0].Description);
    }

    [Fact]
    public void ToAddressTableNodes_PreservesScriptEntries()
    {
        var parser = new CheatTableParser();
        var ct = CheatTableParser.Parse(SampleCtXml);
        var nodes = CheatTableParser.ToAddressTableNodes(ct);

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
        var ct = CheatTableParser.Parse(SampleCtXml);
        var nodes = CheatTableParser.ToAddressTableNodes(ct);

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
        var ct = CheatTableParser.Parse(xml);
        var nodes = CheatTableParser.ToAddressTableNodes(ct);

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
        var ct = CheatTableParser.Parse(xml);
        var nodes = CheatTableParser.ToAddressTableNodes(ct);

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
        var ct = CheatTableParser.Parse(xml);
        var nodes = CheatTableParser.ToAddressTableNodes(ct);

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
        var ct = CheatTableParser.Parse(xml);
        var nodes = CheatTableParser.ToAddressTableNodes(ct);

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
        var ct = CheatTableParser.Parse(xml);
        var nodes = CheatTableParser.ToAddressTableNodes(ct);

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
        var ct = CheatTableParser.Parse(xml);
        var reimported = CheatTableParser.ToAddressTableNodes(ct);

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


public class CtFormatGapClosureTests
{
    [Fact]
    public void RoundTrip_UnknownTableLevelElements_Preserved()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""Test""</Description>
      <VariableType>4 Bytes</VariableType>
      <Address>00400000</Address>
    </CheatEntry>
  </CheatEntries>
  <UserdefinedSymbols>
    <SymbolEntry>
      <Name>mySymbol</Name>
      <Address>12345678</Address>
    </SymbolEntry>
  </UserdefinedSymbols>
  <Structures>
    <Structure Name=""PlayerStruct"" AutoFill=""0"" AutoCreate=""1"" />
  </Structures>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        Assert.NotNull(ct.PreservedElements);
        Assert.Equal(2, ct.PreservedElements.Count);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes, ct.PreservedElements);

        Assert.Contains("UserdefinedSymbols", exported);
        Assert.Contains("mySymbol", exported);
        Assert.Contains("Structures", exported);
        Assert.Contains("PlayerStruct", exported);
    }

    [Fact]
    public void RoundTrip_UnknownEntryLevelElements_Preserved()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""Test""</Description>
      <VariableType>4 Bytes</VariableType>
      <Address>00400000</Address>
      <SomeCustomElement>custom data</SomeCustomElement>
      <AnotherUnknown attr=""val"">nested<child/></AnotherUnknown>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        var entry = ct.Entries[0];
        Assert.NotNull(entry.PreservedElements);
        Assert.Equal(2, entry.PreservedElements.Count);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes);

        Assert.Contains("SomeCustomElement", exported);
        Assert.Contains("custom data", exported);
        Assert.Contains("AnotherUnknown", exported);
    }

    [Fact]
    public void RoundTrip_Color_BgrRgbConversion()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""Red Entry""</Description>
      <VariableType>4 Bytes</VariableType>
      <Address>00400000</Address>
      <Color>0000FF</Color>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        Assert.Equal("0000FF", ct.Entries[0].Color);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        // BGR 0000FF -> RGB #FF0000
        Assert.Equal("#FF0000", nodes[0].UserColor);

        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes);
        // Should convert back to BGR
        Assert.Contains("<Color>0000FF</Color>", exported);
    }

    [Fact]
    public void ConvertBgrToRgb_CorrectConversion()
    {
        Assert.Equal("#FF0000", CheatTableParser.ConvertBgrToRgb("0000FF")); // BGR blue -> RGB red
        Assert.Equal("#0000FF", CheatTableParser.ConvertBgrToRgb("FF0000")); // BGR red -> RGB blue
        Assert.Equal("#00FF00", CheatTableParser.ConvertBgrToRgb("00FF00")); // green stays
        Assert.Null(CheatTableParser.ConvertBgrToRgb(null));
    }

    [Fact]
    public void ConvertRgbToBgr_CorrectConversion()
    {
        Assert.Equal("0000FF", CheatTableParser.ConvertRgbToBgr("#FF0000")); // RGB red -> BGR
        Assert.Equal("FF0000", CheatTableParser.ConvertRgbToBgr("#0000FF")); // RGB blue -> BGR
        Assert.Null(CheatTableParser.ConvertRgbToBgr(null));
    }

    [Fact]
    public void RoundTrip_Options_Flags()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""Group""</Description>
      <GroupHeader>1</GroupHeader>
      <Options moHideChildren=""1"" moActivateChildrenAsWell=""1"" moDeactivateChildrenAsWell=""1""/>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        var entry = ct.Entries[0];
        Assert.True(entry.Options.HasFlag(CheatEntryOptions.HideChildren));
        Assert.True(entry.Options.HasFlag(CheatEntryOptions.ActivateChildrenAsWell));
        Assert.True(entry.Options.HasFlag(CheatEntryOptions.DeactivateChildrenAsWell));
        Assert.False(entry.Options.HasFlag(CheatEntryOptions.RecursiveSetValue));

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        Assert.True(nodes[0].Options.HasFlag(CheatEntryOptions.HideChildren));

        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes);
        Assert.Contains("moHideChildren", exported);
        Assert.Contains("moActivateChildrenAsWell", exported);
        Assert.Contains("moDeactivateChildrenAsWell", exported);
        Assert.DoesNotContain("moRecursiveSetValue", exported);
    }

    [Fact]
    public void RoundTrip_Hotkeys_NestedStructure()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""Test""</Description>
      <VariableType>4 Bytes</VariableType>
      <Address>00400000</Address>
      <Hotkeys>
        <Hotkey>
          <Action>Toggle Activation</Action>
          <ID>1</ID>
          <Keys>
            <Key>17</Key>
            <Key>72</Key>
          </Keys>
        </Hotkey>
        <Hotkey>
          <Action>Set Value</Action>
          <Value>999</Value>
          <ID>2</ID>
          <Keys>
            <Key>113</Key>
          </Keys>
        </Hotkey>
      </Hotkeys>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        var entry = ct.Entries[0];
        Assert.NotNull(entry.Hotkeys);
        Assert.Equal(2, entry.Hotkeys.Count);
        Assert.Equal("Toggle Activation", entry.Hotkeys[0].Action);
        Assert.Equal(2, entry.Hotkeys[0].Keys.Count);
        Assert.Equal(17, entry.Hotkeys[0].Keys[0]);
        Assert.Equal(72, entry.Hotkeys[0].Keys[1]);
        Assert.Equal("Set Value", entry.Hotkeys[1].Action);
        Assert.Equal("999", entry.Hotkeys[1].Value);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        Assert.Equal(2, nodes[0].Hotkeys.Count);

        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes);
        Assert.Contains("<Hotkeys>", exported);
        Assert.Contains("<Action>Toggle Activation</Action>", exported);
        Assert.Contains("<Key>17</Key>", exported);
        Assert.Contains("<Key>72</Key>", exported);
        Assert.Contains("<Value>999</Value>", exported);
    }

    [Fact]
    public void RoundTrip_LastState_FullAttributes()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""Health""</Description>
      <VariableType>4 Bytes</VariableType>
      <Address>00400000</Address>
      <LastState Value=""100"" RealAddress=""00400000"" Activated=""1""/>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        var entry = ct.Entries[0];
        Assert.Equal("100", entry.LastValue);
        Assert.Equal("00400000", entry.LastRealAddress);
        Assert.True(entry.LastActivated);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        Assert.Equal("00400000", nodes[0].LastRealAddress);

        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes);
        Assert.Contains("RealAddress", exported);
    }

    [Fact]
    public void RoundTrip_OffsetAttributes()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""PtrVal""</Description>
      <VariableType>4 Bytes</VariableType>
      <Address>game.dll+100</Address>
      <Offsets>
        <Offset Interval=""60000"" UpdateOnFullRefresh=""1"">B8</Offset>
        <Offset>10</Offset>
      </Offsets>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        var entry = ct.Entries[0];
        Assert.NotNull(entry.RichOffsets);
        Assert.Equal(2, entry.RichOffsets.Count);
        Assert.Equal(60000, entry.RichOffsets[0].Interval);
        Assert.True(entry.RichOffsets[0].UpdateOnFullRefresh);
        Assert.Null(entry.RichOffsets[1].Interval);
        Assert.False(entry.RichOffsets[1].UpdateOnFullRefresh);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        Assert.NotNull(nodes[0].OriginalOffsets);
        Assert.Equal(2, nodes[0].OriginalOffsets!.Count);

        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes);
        Assert.Contains("Interval=\"60000\"", exported);
        Assert.Contains("UpdateOnFullRefresh=\"1\"", exported);
    }

    [Fact]
    public void RoundTrip_Phase2_StringConfig()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""Name""</Description>
      <VariableType>String</VariableType>
      <Address>00400000</Address>
      <Length>32</Length>
      <Unicode>1</Unicode>
      <ZeroTerminate>1</ZeroTerminate>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        Assert.Equal(32, ct.Entries[0].StringLength);
        Assert.True(ct.Entries[0].IsUnicode);
        Assert.True(ct.Entries[0].ZeroTerminate);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        Assert.Equal(32, nodes[0].StringLength);

        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes);
        Assert.Contains("<Length>32</Length>", exported);
        Assert.Contains("<Unicode>1</Unicode>", exported);
        Assert.Contains("<ZeroTerminate>1</ZeroTerminate>", exported);
    }

    [Fact]
    public void RoundTrip_Phase2_BitFields()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""Flag""</Description>
      <VariableType>4 Bytes</VariableType>
      <Address>00400000</Address>
      <BitStart>3</BitStart>
      <BitLength>5</BitLength>
      <ShowAsBinary>1</ShowAsBinary>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        Assert.Equal(3, ct.Entries[0].BitStart);
        Assert.Equal(5, ct.Entries[0].BitLength);
        Assert.True(ct.Entries[0].ShowAsBinary);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes);
        Assert.Contains("<BitStart>3</BitStart>", exported);
        Assert.Contains("<BitLength>5</BitLength>", exported);
        Assert.Contains("<ShowAsBinary>1</ShowAsBinary>", exported);
    }

    [Fact]
    public void RoundTrip_Phase2_CustomType()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""Special""</Description>
      <VariableType>4 Bytes</VariableType>
      <Address>00400000</Address>
      <CustomType>MySpecialType</CustomType>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        Assert.Equal("MySpecialType", ct.Entries[0].CustomTypeName);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes);
        Assert.Contains("<CustomType>MySpecialType</CustomType>", exported);
    }

    [Fact]
    public void RoundTrip_Phase2_DropDownAttributes()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""Item""</Description>
      <VariableType>4 Bytes</VariableType>
      <Address>00400000</Address>
      <DropDownList DescriptionOnly=""1"" ReadOnly=""1"" DisplayValueAsItem=""1"">0:None
1:Sword
2:Shield
</DropDownList>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        Assert.True(ct.Entries[0].DropDownDescriptionOnly);
        Assert.True(ct.Entries[0].DropDownReadOnly);
        Assert.True(ct.Entries[0].DropDownDisplayValueAsItem);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        Assert.True(nodes[0].DropDownDescriptionOnly);
        Assert.True(nodes[0].DropDownReadOnly);

        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes);
        Assert.Contains("DescriptionOnly=\"1\"", exported);
        Assert.Contains("ReadOnly=\"1\"", exported);
        Assert.Contains("DisplayValueAsItem=\"1\"", exported);
    }

    [Fact]
    public void RoundTrip_Phase2_ScriptAsync()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""AsyncScript""</Description>
      <VariableType>Auto Assembler Script</VariableType>
      <AssemblerScript Async=""1""><![CDATA[[ENABLE]
nop
[DISABLE]]]></AssemblerScript>
      <Address>0</Address>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        Assert.True(ct.Entries[0].ScriptAsync);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        Assert.True(nodes[0].ScriptAsync);

        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes);
        Assert.Contains("Async=\"1\"", exported);
    }

    [Fact]
    public void RoundTrip_Phase2_Comment()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""Health""</Description>
      <VariableType>4 Bytes</VariableType>
      <Address>00400000</Address>
      <Comment>This is the player health address</Comment>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        Assert.Equal("This is the player health address", ct.Entries[0].Comment);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        // Comment should map to Notes
        Assert.Equal("This is the player health address", nodes[0].Notes);
        Assert.Equal("This is the player health address", nodes[0].Comment);

        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes);
        Assert.Contains("<Comment>This is the player health address</Comment>", exported);
    }

    [Fact]
    public void RoundTrip_Phase2_ByteLength()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""Bytes""</Description>
      <VariableType>Array of Byte</VariableType>
      <Address>00400000</Address>
      <ByteLength>16</ByteLength>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        Assert.Equal(16, ct.Entries[0].ByteLength);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        Assert.Equal(16, nodes[0].ByteLength);

        var exporter = new CheatTableExporter();
        var exported = exporter.ExportToXml(nodes);
        Assert.Contains("<ByteLength>16</ByteLength>", exported);
    }

    [Fact]
    public void RoundTrip_LastActivated_SetsIsScriptEnabled()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CheatTable CheatEngineTableVersion=""46"">
  <CheatEntries>
    <CheatEntry>
      <ID>0</ID>
      <Description>""God Mode""</Description>
      <VariableType>Auto Assembler Script</VariableType>
      <AssemblerScript><![CDATA[[ENABLE]
nop
[DISABLE]]]></AssemblerScript>
      <Address>0</Address>
      <LastState Activated=""1""/>
    </CheatEntry>
  </CheatEntries>
</CheatTable>";

        var ct = CheatTableParser.Parse(xml);
        Assert.True(ct.Entries[0].LastActivated);

        var nodes = CheatTableParser.ToAddressTableNodes(ct);
        // Script entry with LastActivated should have IsScriptEnabled = true
        Assert.True(nodes[0].IsScriptEnabled);
    }
}
