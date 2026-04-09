using System.IO;
using System.Text.Json;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for SessionMetadata and SessionIndex: metadata tracking, search, and rebuild.
/// </summary>
public class SessionIndexTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _tempDir;

    public SessionIndexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ceai-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── SessionMetadata ──

    [Fact]
    public void SessionMetadata_DefaultsAreSet()
    {
        var meta = new SessionMetadata();
        Assert.NotNull(meta.Id);
        Assert.NotEmpty(meta.Id);
        Assert.Equal(12, meta.Id.Length);
        Assert.True(meta.StartedAt > DateTimeOffset.MinValue);
        Assert.Empty(meta.Discoveries);
        Assert.Empty(meta.Timeline);
        Assert.Empty(meta.Tags);
        Assert.Equal(0, meta.TotalToolCalls);
    }

    [Fact]
    public void AddEvent_AddsToTimeline()
    {
        var meta = new SessionMetadata();
        meta.AddEvent("Attached to game", "AttachProcess");
        Assert.Single(meta.Timeline);
        Assert.Equal("Attached to game", meta.Timeline[0].Description);
        Assert.Equal("AttachProcess", meta.Timeline[0].ToolName);
    }

    [Fact]
    public void AddDiscovery_AddsToList()
    {
        var meta = new SessionMetadata();
        meta.AddDiscovery("HP offset", "0x38", "Int32");
        Assert.Single(meta.Discoveries);
        Assert.Equal("HP offset", meta.Discoveries[0].Name);
        Assert.Equal("0x38", meta.Discoveries[0].Address);
        Assert.Equal("Int32", meta.Discoveries[0].Type);
    }

    [Fact]
    public void GetSummary_ContainsSessionInfo()
    {
        var meta = new SessionMetadata { TargetProcessName = "Game.exe" };
        meta.AddEvent("test");
        meta.AddDiscovery("HP");
        meta.TotalToolCalls = 10;
        meta.CumulativeCost = 0.05m;

        var summary = meta.GetSummary();
        Assert.Contains("Game.exe", summary);
        Assert.Contains("1 events", summary);
        Assert.Contains("1 discoveries", summary);
        Assert.Contains("10 tool calls", summary);
    }

    [Fact]
    public void GetSummary_NoTarget_OmitsProcessName()
    {
        var meta = new SessionMetadata();
        var summary = meta.GetSummary();
        Assert.DoesNotContain("target:", summary);
    }

    // ── SessionIndex ──

    [Fact]
    public void Rebuild_EmptyDirectory_DoesNotThrow()
    {
        var index = new SessionIndex(_tempDir);
        index.Rebuild();
        var results = index.Search("anything");
        Assert.Empty(results);
    }

    [Fact]
    public void Rebuild_NonexistentDirectory_DoesNotThrow()
    {
        var index = new SessionIndex(Path.Combine(_tempDir, "nonexistent"));
        index.Rebuild();
        var results = index.Search("anything");
        Assert.Empty(results);
    }

    [Fact]
    public void Rebuild_IndexesSessionFiles()
    {
        // Write a session metadata file
        var meta = new SessionMetadata
        {
            TargetProcessName = "BDFFHD.exe",
            Tags = ["rpg", "bravely-default"],
        };
        meta.AddDiscovery("HP field", "0x100");
        meta.AddEvent("Found HP");

        var json = JsonSerializer.Serialize(meta, s_jsonOpts);
        File.WriteAllText(Path.Combine(_tempDir, $"{meta.Id}.session.json"), json);

        var index = new SessionIndex(_tempDir);
        index.Rebuild();

        var results = index.Search("BDFFHD");
        Assert.Single(results);
        Assert.Equal("BDFFHD.exe", results[0].TargetProcessName);
    }

    [Fact]
    public void Search_ByTag_FindsSession()
    {
        var meta = new SessionMetadata { Tags = ["rpg"] };
        var json = JsonSerializer.Serialize(meta, s_jsonOpts);
        File.WriteAllText(Path.Combine(_tempDir, $"{meta.Id}.session.json"), json);

        var index = new SessionIndex(_tempDir);
        index.Rebuild();

        var results = index.Search("rpg");
        Assert.Single(results);
    }

    [Fact]
    public void Search_ByDiscovery_FindsSession()
    {
        var meta = new SessionMetadata();
        meta.AddDiscovery("HP offset");
        var json = JsonSerializer.Serialize(meta, s_jsonOpts);
        File.WriteAllText(Path.Combine(_tempDir, $"{meta.Id}.session.json"), json);

        var index = new SessionIndex(_tempDir);
        index.Rebuild();

        var results = index.Search("HP offset");
        Assert.Single(results);
    }

    [Fact]
    public void Search_ByTimeline_FindsSession()
    {
        var meta = new SessionMetadata();
        meta.AddEvent("Scanned for health value");
        var json = JsonSerializer.Serialize(meta, s_jsonOpts);
        File.WriteAllText(Path.Combine(_tempDir, $"{meta.Id}.session.json"), json);

        var index = new SessionIndex(_tempDir);
        index.Rebuild();

        var results = index.Search("health");
        Assert.Single(results);
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        var meta = new SessionMetadata { TargetProcessName = "notepad.exe" };
        var json = JsonSerializer.Serialize(meta, s_jsonOpts);
        File.WriteAllText(Path.Combine(_tempDir, $"{meta.Id}.session.json"), json);

        var index = new SessionIndex(_tempDir);
        index.Rebuild();

        var results = index.Search("nonexistent-query-xyz");
        Assert.Empty(results);
    }

    [Fact]
    public void Search_MaxResults_LimitsOutput()
    {
        // Create multiple sessions
        for (int i = 0; i < 5; i++)
        {
            var meta = new SessionMetadata { TargetProcessName = "Game.exe" };
            var json = JsonSerializer.Serialize(meta, s_jsonOpts);
            File.WriteAllText(Path.Combine(_tempDir, $"{meta.Id}.session.json"), json);
        }

        var index = new SessionIndex(_tempDir);
        index.Rebuild();

        var results = index.Search("Game", maxResults: 3);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Search_OrderedByStartTime_Descending()
    {
        var older = new SessionMetadata
        {
            TargetProcessName = "Game.exe",
            StartedAt = DateTimeOffset.UtcNow.AddDays(-2)
        };
        var newer = new SessionMetadata
        {
            TargetProcessName = "Game.exe",
            StartedAt = DateTimeOffset.UtcNow
        };

        var opts = s_jsonOpts;
        File.WriteAllText(Path.Combine(_tempDir, $"{older.Id}.session.json"), JsonSerializer.Serialize(older, opts));
        File.WriteAllText(Path.Combine(_tempDir, $"{newer.Id}.session.json"), JsonSerializer.Serialize(newer, opts));

        var index = new SessionIndex(_tempDir);
        index.Rebuild();

        var results = index.Search("Game");
        Assert.Equal(2, results.Count);
        Assert.True(results[0].StartedAt >= results[1].StartedAt);
    }

    [Fact]
    public void Rebuild_CorruptedFile_SkipsItGracefully()
    {
        // Write a valid session
        var meta = new SessionMetadata { TargetProcessName = "Valid.exe" };
        var opts = s_jsonOpts;
        File.WriteAllText(Path.Combine(_tempDir, $"{meta.Id}.session.json"), JsonSerializer.Serialize(meta, opts));

        // Write corrupted JSON
        File.WriteAllText(Path.Combine(_tempDir, "corrupted.session.json"), "{invalid json!!");

        var index = new SessionIndex(_tempDir);
        index.Rebuild();

        var results = index.Search("Valid");
        Assert.Single(results);
    }
}
