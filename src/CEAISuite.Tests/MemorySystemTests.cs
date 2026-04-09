using System.IO;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for <see cref="MemorySystem"/>: CRUD, persistence, deduplication,
/// query filtering, pruning, and system prompt formatting.
/// </summary>
public class MemorySystemTests : IDisposable
{
    private readonly string _tempFile;

    public MemorySystemTests()
    {
        _tempFile = Path.GetTempFileName();
    }

    public void Dispose()
    {
        try { File.Delete(_tempFile); } catch { /* best-effort */ }
    }

    private MemorySystem Create() => new(_tempFile);

    // ── Remember (Add) ──────────────────────────────────────────────

    [Fact]
    public void Remember_NewEntry_AddedToEntries()
    {
        var mem = Create();
        mem.Load();

        var result = mem.Remember("HP at 0x1A4", MemoryCategory.ProcessKnowledge, "Game.exe");

        Assert.Contains("Saved memory", result);
        Assert.Single(mem.Entries);
        Assert.Equal("HP at 0x1A4", mem.Entries[0].Content);
        Assert.Equal(MemoryCategory.ProcessKnowledge, mem.Entries[0].Category);
        Assert.Equal("Game.exe", mem.Entries[0].ProcessName);
    }

    [Fact]
    public void Remember_MultipleEntries_AllTracked()
    {
        var mem = Create();
        mem.Load();

        mem.Remember("entry one", MemoryCategory.LearnedPattern);
        mem.Remember("entry two", MemoryCategory.SafetyNote);

        Assert.Equal(2, mem.Entries.Count);
    }

    // ── Deduplication (Update) ──────────────────────────────────────

    [Fact]
    public void Remember_SimilarContent_UpdatesExistingEntry()
    {
        var mem = Create();
        mem.Load();

        mem.Remember("Player HP is at 0x1A4", MemoryCategory.ProcessKnowledge, "Game.exe");
        var result = mem.Remember("Player HP is at 0x1A4 confirmed", MemoryCategory.ProcessKnowledge, "Game.exe");

        Assert.Contains("Updated", result);
        Assert.Single(mem.Entries);
        Assert.Contains("confirmed", mem.Entries[0].Content);
    }

    [Fact]
    public void Remember_DifferentCategory_NotDeduplicated()
    {
        var mem = Create();
        mem.Load();

        mem.Remember("HP at 0x1A4", MemoryCategory.ProcessKnowledge, "Game.exe");
        mem.Remember("HP at 0x1A4", MemoryCategory.SafetyNote, "Game.exe");

        Assert.Equal(2, mem.Entries.Count);
    }

    // ── Forget (Remove) ─────────────────────────────────────────────

    [Fact]
    public void Forget_ExistingEntry_Removed()
    {
        var mem = Create();
        mem.Load();

        mem.Remember("to remove", MemoryCategory.ToolTip);
        var id = mem.Entries[0].Id;

        var result = mem.Forget(id);
        Assert.Contains("Forgotten", result);
        Assert.Empty(mem.Entries);
    }

    [Fact]
    public void Forget_UnknownId_ReturnsNotFound()
    {
        var mem = Create();
        mem.Load();

        var result = mem.Forget("no-such-id");
        Assert.Contains("not found", result);
    }

    // ── Recall (Query) ──────────────────────────────────────────────

    [Fact]
    public void Recall_ByCategory_FiltersCorrectly()
    {
        var mem = Create();
        mem.Load();

        mem.Remember("pref1", MemoryCategory.UserPreference);
        mem.Remember("pattern1", MemoryCategory.LearnedPattern);

        var prefs = mem.Recall(category: MemoryCategory.UserPreference);
        Assert.Single(prefs);
        Assert.Equal("pref1", prefs[0].Content);
    }

    [Fact]
    public void Recall_BySearchTerm_FiltersContent()
    {
        var mem = Create();
        mem.Load();

        mem.Remember("HP at 0x1A4", MemoryCategory.ProcessKnowledge);
        mem.Remember("MP at 0x1B0", MemoryCategory.ProcessKnowledge);

        var results = mem.Recall(searchTerm: "HP");
        Assert.Single(results);
        Assert.Contains("HP", results[0].Content);
    }

    [Fact]
    public void Recall_ByProcessName_IncludesGlobalAndProcessSpecific()
    {
        var mem = Create();
        mem.Load();

        mem.Remember("global note", MemoryCategory.SafetyNote); // No process
        mem.Remember("game note", MemoryCategory.SafetyNote, "Game.exe");
        mem.Remember("other note", MemoryCategory.SafetyNote, "Other.exe");

        var results = mem.Recall(processName: "Game.exe");
        Assert.Equal(2, results.Count); // global + Game.exe
    }

    [Fact]
    public void Recall_MaxResults_LimitsOutput()
    {
        var mem = Create();
        mem.Load();

        for (int i = 0; i < 10; i++)
            mem.Remember($"entry {i}", MemoryCategory.LearnedPattern);

        var results = mem.Recall(maxResults: 3);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Recall_NoEntries_ReturnsEmpty()
    {
        var mem = Create();
        mem.Load();

        var results = mem.Recall();
        Assert.Empty(results);
    }

    // ── BuildMemoryContext (FormatForSystemPrompt) ───────────────────

    [Fact]
    public void BuildMemoryContext_WithEntries_FormatsCorrectly()
    {
        var mem = Create();
        mem.Load();

        mem.Remember("Use float scans", MemoryCategory.UserPreference);
        mem.Remember("Base 0x7FF000", MemoryCategory.ProcessKnowledge, "Game.exe");

        var ctx = mem.BuildMemoryContext();
        Assert.NotNull(ctx);
        Assert.Contains("AGENT MEMORY", ctx);
        Assert.Contains("float scans", ctx);
    }

    [Fact]
    public void BuildMemoryContext_NoEntries_ReturnsNull()
    {
        var mem = Create();
        mem.Load();

        var ctx = mem.BuildMemoryContext();
        Assert.Null(ctx);
    }

    [Fact]
    public void BuildMemoryContext_RespectsMaxChars()
    {
        var mem = Create();
        mem.Load();

        // Add many entries to exceed a very small char limit
        for (int i = 0; i < 50; i++)
            mem.Remember(
                $"This is a really long memory entry number {i} that should definitely exceed the budget " +
                $"when combined with many other entries like this one {Guid.NewGuid()}",
                MemoryCategory.LearnedPattern);

        // Use a very small maxChars to guarantee truncation
        var ctx = mem.BuildMemoryContext(maxChars: 50);
        Assert.NotNull(ctx);
        Assert.Contains("more memories available", ctx);
    }

    [Fact]
    public void BuildMemoryContext_FiltersByProcessName()
    {
        var mem = Create();
        mem.Load();

        mem.Remember("game specific", MemoryCategory.ProcessKnowledge, "Game.exe");
        mem.Remember("other specific", MemoryCategory.ProcessKnowledge, "Other.exe");

        var ctx = mem.BuildMemoryContext(processName: "Game.exe");
        Assert.NotNull(ctx);
        Assert.Contains("game specific", ctx);
        // Other.exe should not appear since it doesn't match
    }

    // ── Persistence (Save/Load) ─────────────────────────────────────

    [Fact]
    public void Persistence_SaveAndLoad_RoundTrips()
    {
        var mem1 = Create();
        mem1.Load();
        mem1.Remember("persisted entry", MemoryCategory.SafetyNote, "Test.exe", "test-source");

        // Load in fresh instance
        var mem2 = Create();
        mem2.Load();

        Assert.Single(mem2.Entries);
        Assert.Equal("persisted entry", mem2.Entries[0].Content);
        Assert.Equal(MemoryCategory.SafetyNote, mem2.Entries[0].Category);
        Assert.Equal("Test.exe", mem2.Entries[0].ProcessName);
        Assert.Equal("test-source", mem2.Entries[0].Source);
    }

    [Fact]
    public void Load_CalledTwice_OnlyLoadsOnce()
    {
        var mem = Create();
        mem.Load();
        mem.Remember("entry", MemoryCategory.LearnedPattern);

        // Load again on same instance — should not duplicate
        mem.Load();
        Assert.Single(mem.Entries);
    }

    [Fact]
    public void Load_MissingFile_NoError()
    {
        var nonexistentPath = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid() + ".json");
        var mem = new MemorySystem(nonexistentPath);
        mem.Load(); // Should not throw
        Assert.Empty(mem.Entries);
    }

    // ── Prune ───────────────────────────────────────────────────────

    [Fact]
    public void Prune_ExceedsMaxEntries_TrimmsDown()
    {
        var mem = Create();
        mem.Load();

        for (int i = 0; i < 10; i++)
            mem.Remember($"entry {i} with unique words {Guid.NewGuid()}", MemoryCategory.LearnedPattern);

        var removed = mem.Prune(maxEntries: 3);
        Assert.True(removed >= 7);
        Assert.True(mem.Entries.Count <= 3);
    }

    [Fact]
    public void Prune_UserPreferences_NeverRemoved()
    {
        var mem = Create();
        mem.Load();

        mem.Remember("user pref", MemoryCategory.UserPreference);
        for (int i = 0; i < 5; i++)
            mem.Remember($"pattern {i} with unique words {Guid.NewGuid()}", MemoryCategory.LearnedPattern);

        mem.Prune(maxEntries: 1);
        // User preference should survive even though maxEntries is 1
        Assert.Contains(mem.Entries, e => e.Category == MemoryCategory.UserPreference);
    }

    [Fact]
    public void Prune_EmptyMemory_ReturnsZero()
    {
        var mem = Create();
        mem.Load();

        var removed = mem.Prune();
        Assert.Equal(0, removed);
    }
}
