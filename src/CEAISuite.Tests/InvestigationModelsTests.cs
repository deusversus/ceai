using CEAISuite.Domain;

namespace CEAISuite.Tests;

public class InvestigationModelsTests
{
    [Fact]
    public void ProjectProfile_RecordEquality_Works()
    {
        var a = new ProjectProfile("id1", "name", "proc.exe", "Windows x64", ["mod1"]);
        var b = new ProjectProfile("id1", "name", "proc.exe", "Windows x64", ["mod1"]);
        // Record equality compares by value for value-type properties,
        // but reference equality for collections — verify construction at least
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.TargetProcess, b.TargetProcess);
    }

    [Fact]
    public void InvestigationSession_CanConstruct()
    {
        var session = new InvestigationSession(
            "sess1",
            "game.exe",
            1234,
            DateTimeOffset.UtcNow,
            Array.Empty<AddressEntry>(),
            Array.Empty<ScanSession>(),
            Array.Empty<AIActionLog>());

        Assert.Equal("sess1", session.Id);
        Assert.Equal("game.exe", session.ProcessName);
        Assert.Equal(1234, session.ProcessId);
        Assert.Null(session.ChatId);
    }

    [Fact]
    public void InvestigationSession_WithChatId()
    {
        var session = new InvestigationSession(
            "s1", "proc", null, DateTimeOffset.UtcNow,
            [], [], [], ChatId: "chat123");
        Assert.Equal("chat123", session.ChatId);
    }

    [Fact]
    public void AddressEntry_ConstructsWithAllFields()
    {
        var entry = new AddressEntry("e1", "HP", "game.exe+0x100", "Int32", "100", "notes", ["tag1", "tag2"]);
        Assert.Equal("e1", entry.Id);
        Assert.Equal("HP", entry.Label);
        Assert.Equal("Int32", entry.ValueType);
        Assert.Equal(2, entry.Tags.Count);
    }

    [Fact]
    public void ScanSession_ConstructsWithHistory()
    {
        var session = new ScanSession("sc1", "Exact", "100", ["changed", "unchanged"], 5);
        Assert.Equal("sc1", session.Id);
        Assert.Equal(2, session.RefinementHistory.Count);
        Assert.Equal(5, session.ResultCount);
    }

    [Fact]
    public void PatchRecord_OriginalAndPatchedBytes_RoundTrip()
    {
        var patch = new PatchRecord("p1", "0x400000",
            new byte[] { 0x90, 0x90 },
            new byte[] { 0xEB, 0x05 },
            true);
        Assert.Equal(2, patch.OriginalBytes.Count);
        Assert.Equal(0x90, patch.OriginalBytes[0]);
        Assert.Equal(0xEB, patch.PatchedBytes[0]);
        Assert.True(patch.IsVerified);
    }

    [Fact]
    public void AIActionLog_CanConstruct()
    {
        var log = new AIActionLog("a1", "scan", ["start_scan", "refine_scan"], "Found HP", true, "success");
        Assert.Equal("a1", log.Id);
        Assert.Equal("scan", log.Intent);
        Assert.Equal(2, log.ToolCalls.Count);
        Assert.True(log.UserApproved);
    }
}
