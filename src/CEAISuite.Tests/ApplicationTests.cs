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
        Assert.Equal("Script", node.DisplayType);
        Assert.Equal("❌ Disabled", node.DisplayValue);

        node.IsScriptEnabled = true;
        Assert.Equal("✅ Enabled", node.DisplayValue);
    }
}
