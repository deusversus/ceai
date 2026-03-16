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
