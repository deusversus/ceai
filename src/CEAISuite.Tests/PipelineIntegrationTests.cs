using System.Xml.Linq;
using CEAISuite.Application;
using CEAISuite.Domain;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// In-memory session repository that actually persists data for integration testing.
/// </summary>
file sealed class InMemorySessionRepository : IInvestigationSessionRepository
{
    private readonly Dictionary<string, InvestigationSession> _sessions = new();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SaveAsync(InvestigationSession session, CancellationToken cancellationToken = default)
    {
        _sessions[session.Id] = session;
        return Task.CompletedTask;
    }

    public Task<InvestigationSession?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.TryGetValue(sessionId, out var s) ? s : null);

    public Task<IReadOnlyList<SavedInvestigationSession>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SavedInvestigationSession>>(
            _sessions.Values
                .OrderByDescending(s => s.CreatedAtUtc)
                .Take(limit)
                .Select(s => new SavedInvestigationSession(s.Id, s.ProcessName, s.ProcessId, s.CreatedAtUtc,
                    s.AddressEntries.Count, s.ScanSessions.Count, s.ActionLog.Count))
                .ToList());

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.Remove(sessionId);
        return Task.CompletedTask;
    }
}

public class PipelineIntegrationTests
{
    // ── Scan Pipeline ──

    [Fact]
    public async Task ScanPipeline_StartAndRefine_ProducesResults()
    {
        var engine = new StubScanEngine();
        var scanResults = new List<ScanResultEntry>
        {
            new((nuint)0x1000, "100", null, BitConverter.GetBytes(100), null),
            new((nuint)0x2000, "100", null, BitConverter.GetBytes(100), null),
            new((nuint)0x3000, "200", null, BitConverter.GetBytes(200), null),
        };
        engine.NextScanResult = new ScanResultSet("scan-1", 1000,
            new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            scanResults, 10, 40960, DateTimeOffset.UtcNow);

        var refinedResults = new List<ScanResultEntry>
        {
            new((nuint)0x1000, "100", "100", BitConverter.GetBytes(100), null),
        };
        engine.NextRefineResult = new ScanResultSet("scan-1", 1000,
            new ScanConstraints(MemoryDataType.Int32, ScanType.Unchanged, null),
            refinedResults, 5, 20480, DateTimeOffset.UtcNow);

        var sut = new ScanService(engine);

        var initial = await sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "100");
        Assert.Equal(3, initial.ResultCount);

        var refined = await sut.RefineScanAsync(ScanType.Unchanged, null);
        Assert.Equal(1, refined.ResultCount);
    }

    [Fact]
    public async Task ScanPipeline_UndoRestoresPreviousResults()
    {
        var engine = new StubScanEngine();
        var initialResults = new List<ScanResultEntry>
        {
            new((nuint)0x1000, "42", null, BitConverter.GetBytes(42), null),
            new((nuint)0x2000, "42", null, BitConverter.GetBytes(42), null),
        };
        engine.NextScanResult = new ScanResultSet("s1", 1000,
            new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "42"),
            initialResults, 10, 40960, DateTimeOffset.UtcNow);

        engine.NextRefineResult = new ScanResultSet("s1", 1000,
            new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "42"),
            initialResults.Take(1).ToList(), 5, 20480, DateTimeOffset.UtcNow);

        var sut = new ScanService(engine);
        await sut.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "42");
        await sut.RefineScanAsync(ScanType.ExactValue, "42");

        var undone = sut.UndoScan();
        Assert.NotNull(undone);
        Assert.Equal(2, undone!.ResultCount);
    }

    // ── Address Table Pipeline ──

    [Fact]
    public void AddressTable_AddAndRemove_WorksCorrectly()
    {
        var facade = new StubEngineFacade();
        var sut = new AddressTableService(facade);

        var entry = sut.AddEntry("0x100", MemoryDataType.Int32, "42", "Health");
        Assert.Single(sut.Roots);
        Assert.Single(sut.Entries);
        Assert.Equal("Health", sut.Entries[0].Label);

        sut.RemoveEntry(entry.Id);
        Assert.Empty(sut.Roots);
        Assert.Empty(sut.Entries);
    }

    [Fact]
    public void AddressTable_GroupHierarchy_PreservesStructure()
    {
        var facade = new StubEngineFacade();
        var sut = new AddressTableService(facade);

        var group = sut.CreateGroup("Player");
        sut.AddEntryToGroup(group.Id, "0x100", MemoryDataType.Int32, "100", "HP");
        sut.AddEntryToGroup(group.Id, "0x104", MemoryDataType.Float, "50.5", "MP");

        Assert.Single(sut.Roots);
        Assert.True(sut.Roots[0].IsGroup);
        Assert.Equal(2, sut.Roots[0].Children.Count);
        Assert.Equal(2, sut.Entries.Count); // flat entries include children
    }

    [Fact]
    public void AddressTable_ImportCTNodes_PreservesTree()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"Stats"</Description>
                  <GroupHeader>1</GroupHeader>
                  <CheatEntries>
                    <CheatEntry>
                      <ID>2</ID>
                      <Description>"HP"</Description>
                      <VariableType>4 Bytes</VariableType>
                      <Address>100</Address>
                    </CheatEntry>
                  </CheatEntries>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        var ctFile = CheatTableParser.Parse(xml, "test.ct");
        var nodes = CheatTableParser.ToAddressTableNodes(ctFile);

        var facade = new StubEngineFacade();
        var sut = new AddressTableService(facade);
        sut.ImportNodes(nodes);

        Assert.Single(sut.Roots);
        Assert.True(sut.Roots[0].IsGroup);
        Assert.Equal("Stats", sut.Roots[0].Label);
        Assert.Single(sut.Roots[0].Children);
        Assert.Equal("HP", sut.Roots[0].Children[0].Label);
    }

    // ── Script Pipeline ──

    [Fact]
    public void ScriptNode_IdentifiedAsScriptEntry()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"God Mode"</Description>
                  <VariableType>Auto Assembler Script</VariableType>
                  <AssemblerScript><![CDATA[[ENABLE]
            nop
            [DISABLE]
            db 90]]></AssemblerScript>
                  <Address>0</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        var ctFile = CheatTableParser.Parse(xml, "test.ct");
        var nodes = CheatTableParser.ToAddressTableNodes(ctFile);

        Assert.Single(nodes);
        Assert.True(nodes[0].IsScriptEntry);
        Assert.NotNull(nodes[0].AssemblerScript);
        Assert.Contains("[ENABLE]", nodes[0].AssemblerScript);
    }

    // ── Session Pipeline ──

    [Fact]
    public async Task SessionPipeline_SaveAndList_SessionAppears()
    {
        var repo = new InMemorySessionRepository();
        var sut = new SessionService(repo);

        var entries = new List<AddressTableEntry>
        {
            new("e1", "HP", "0x100", MemoryDataType.Int32, "100", null, null, false, null),
        };
        var actions = new List<AiActionLogEntry>
        {
            new("scan", "exact 100", "Found 1 result", DateTimeOffset.UtcNow),
        };

        var sessionId = await sut.SaveSessionAsync("TestGame.exe", 1000, entries, actions);
        Assert.NotNull(sessionId);

        var sessions = await sut.ListSessionsAsync(10);
        Assert.Single(sessions);
        Assert.Equal("TestGame.exe", sessions[0].ProcessName);
        Assert.Equal(1, sessions[0].AddressEntryCount);
    }

    [Fact]
    public async Task SessionPipeline_SaveAndLoad_RestoresState()
    {
        var repo = new InMemorySessionRepository();
        var sut = new SessionService(repo);

        var entries = new List<AddressTableEntry>
        {
            new("e1", "Health", "0x100", MemoryDataType.Int32, "500", null, null, true, null),
            new("e2", "Mana", "0x104", MemoryDataType.Float, "99.5", null, null, false, null),
        };

        var sessionId = await sut.SaveSessionAsync("Game.exe", 2000, entries, Array.Empty<AiActionLogEntry>());

        var loaded = await sut.LoadSessionAsync(sessionId);
        Assert.NotNull(loaded);
        Assert.Equal("Game.exe", loaded.Value.ProcessName);
        Assert.Equal(2000, loaded.Value.ProcessId);
        Assert.Equal(2, loaded.Value.Entries.Count);
        Assert.Equal("Health", loaded.Value.Entries[0].Label);
        Assert.Equal("Mana", loaded.Value.Entries[1].Label);
    }

    [Fact]
    public async Task SessionPipeline_Delete_RemovesSession()
    {
        var repo = new InMemorySessionRepository();
        var sut = new SessionService(repo);

        var sessionId = await sut.SaveSessionAsync("Game.exe", 1000,
            Array.Empty<AddressTableEntry>(), Array.Empty<AiActionLogEntry>());

        var before = await sut.ListSessionsAsync(10);
        Assert.Single(before);

        await sut.DeleteSessionAsync(sessionId);

        var after = await sut.ListSessionsAsync(10);
        Assert.Empty(after);
    }

    // ── Export Pipeline ──

    [Fact]
    public void ExportPipeline_AddressTableToXml_ProducesValidCT()
    {
        var facade = new StubEngineFacade();
        var table = new AddressTableService(facade);
        table.AddEntry("0x100", MemoryDataType.Int32, "42", "Score");
        table.AddEntry("0x200", MemoryDataType.Float, "3.14", "Speed");

        var exporter = new CheatTableExporter();
        var xml = exporter.ExportToXml(table.Roots);

        Assert.Contains("CheatTable", xml);
        Assert.Contains("Score", xml);
        Assert.Contains("Speed", xml);

        // Re-parse should succeed
        var reparsed = CheatTableParser.Parse(xml, "export.ct");
        Assert.Equal(2, reparsed.TotalEntryCount);
    }

    [Fact]
    public void ExportPipeline_GroupedTable_ProducesValidHierarchy()
    {
        var facade = new StubEngineFacade();
        var table = new AddressTableService(facade);
        var group = table.CreateGroup("Inventory");
        table.AddEntryToGroup(group.Id, "0x300", MemoryDataType.Int32, "5", "Potions");
        table.AddEntryToGroup(group.Id, "0x304", MemoryDataType.Int32, "3", "Elixirs");

        var exporter = new CheatTableExporter();
        var xml = exporter.ExportToXml(table.Roots);

        var reparsed = CheatTableParser.Parse(xml, "grouped.ct");
        Assert.Single(reparsed.Entries);
        Assert.True(reparsed.Entries[0].IsGroupHeader);
        Assert.Equal(2, reparsed.Entries[0].Children.Count);
    }

    // ── Snapshot Pipeline ──

    [Fact]
    public async Task SnapshotPipeline_CaptureAndCompare_DetectsChanges()
    {
        var engine = new StubEngineFacade();
        engine.WriteMemoryDirect((nuint)0x1000, new byte[] { 1, 2, 3, 4 });

        var sut = new MemorySnapshotService(engine);
        var snap1 = await sut.CaptureAsync(1000, (nuint)0x1000, 4, "Before");

        // Ensure timestamp-based IDs don't collide (IDs use millisecond precision)
        await Task.Delay(5);

        engine.WriteMemoryDirect((nuint)0x1000, new byte[] { 5, 6, 7, 8 });
        var snap2 = await sut.CaptureAsync(1000, (nuint)0x1000, 4, "After");

        Assert.NotEqual(snap1.Id, snap2.Id);
        var diff = sut.Compare(snap1.Id, snap2.Id);
        Assert.True(diff.ChangedByteCount > 0);
        Assert.NotEmpty(diff.Changes);
    }

    [Fact]
    public async Task SnapshotPipeline_CaptureAndList_SnapshotTracked()
    {
        var engine = new StubEngineFacade();
        engine.WriteMemoryDirect((nuint)0x2000, new byte[] { 10, 20, 30 });

        var sut = new MemorySnapshotService(engine);
        var snap = await sut.CaptureAsync(1000, (nuint)0x2000, 3, "Test Snap");

        var list = sut.ListSnapshots();
        Assert.Single(list);
        Assert.Equal("Test Snap", list[0].Label);

        var retrieved = sut.GetSnapshot(snap.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(snap.Id, retrieved!.Id);
    }

    [Fact]
    public async Task SnapshotPipeline_Delete_RemovesSnapshot()
    {
        var engine = new StubEngineFacade();
        engine.WriteMemoryDirect((nuint)0x3000, new byte[] { 0xFF });

        var sut = new MemorySnapshotService(engine);
        var snap = await sut.CaptureAsync(1000, (nuint)0x3000, 1);

        Assert.Single(sut.ListSnapshots());
        sut.DeleteSnapshot(snap.Id);
        Assert.Empty(sut.ListSnapshots());
    }

    // ── Multi-Service Pipeline ──

    [Fact]
    public async Task FullPipeline_ScanAddSaveLoadVerify()
    {
        // Step 1: Scan for a value
        var scanEngine = new StubScanEngine();
        scanEngine.NextScanResult = new ScanResultSet("s1", 1000,
            new ScanConstraints(MemoryDataType.Int32, ScanType.ExactValue, "100"),
            new[] { new ScanResultEntry((nuint)0x1000, "100", null, BitConverter.GetBytes(100), null) },
            10, 40960, DateTimeOffset.UtcNow);
        var scanService = new ScanService(scanEngine);
        var scanResult = await scanService.StartScanAsync(1000, MemoryDataType.Int32, ScanType.ExactValue, "100");
        Assert.Equal(1, scanResult.ResultCount);

        // Step 2: Add scan result to address table
        var facade = new StubEngineFacade();
        var table = new AddressTableService(facade);
        table.AddFromScanResult(scanResult.Results[0], MemoryDataType.Int32, "Found HP");
        Assert.Single(table.Entries);
        Assert.Equal("Found HP", table.Entries[0].Label);

        // Step 3: Save session
        var repo = new InMemorySessionRepository();
        var sessionService = new SessionService(repo);
        var sessionId = await sessionService.SaveSessionAsync(
            "TestGame.exe", 1000, table.Entries, Array.Empty<AiActionLogEntry>());

        // Step 4: Load session
        var loaded = await sessionService.LoadSessionAsync(sessionId);
        Assert.NotNull(loaded);
        Assert.Single(loaded.Value.Entries);
        Assert.Equal("Found HP", loaded.Value.Entries[0].Label);

        // Step 5: Re-import into a new table and verify
        var table2 = new AddressTableService(facade);
        table2.ImportFlat(loaded.Value.Entries);
        Assert.Single(table2.Entries);
    }

    [Fact]
    public void AddressTable_ClearAll_RemovesEverything()
    {
        var facade = new StubEngineFacade();
        var sut = new AddressTableService(facade);
        sut.AddEntry("0x100", MemoryDataType.Int32, "1", "A");
        sut.AddEntry("0x200", MemoryDataType.Int32, "2", "B");
        var group = sut.CreateGroup("G");
        sut.AddEntryToGroup(group.Id, "0x300", MemoryDataType.Int32, "3", "C");

        Assert.Equal(3, sut.Entries.Count);

        sut.ClearAll();
        Assert.Empty(sut.Roots);
        Assert.Empty(sut.Entries);
    }
}
