using CEAISuite.Application;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for <see cref="AiChatStore"/>: save, load, list, delete, rename.
/// Uses unique chat IDs prefixed with "test-" to avoid collisions with real data.
/// Cleans up after each test.
/// </summary>
public class AiChatStoreTests : IDisposable
{
    private readonly List<string> _createdChatIds = [];

    public void Dispose()
    {
        // Clean up all test chats
        foreach (var id in _createdChatIds)
        {
            try { AiChatStore.Delete(id); } catch { /* best-effort */ }
        }
    }

    private string UniqueChatId()
    {
        var id = $"test-chat-{Guid.NewGuid():N}";
        _createdChatIds.Add(id);
        return id;
    }

    private AiChatSession CreateSession(string? id = null, string title = "Test Chat")
    {
        var chatId = id ?? UniqueChatId();
        if (id is not null && !_createdChatIds.Contains(id))
            _createdChatIds.Add(id);

        return new AiChatSession
        {
            Id = chatId,
            Title = title,
            Messages = [new AiChatMessage("user", "Hello", DateTimeOffset.UtcNow)],
        };
    }

    // ── Save & Load ─────────────────────────────────────────────────

    [Fact]
    public void Save_NewSession_CanBeLoaded()
    {
        var session = CreateSession();
        AiChatStore.Save(session);

        var loaded = AiChatStore.Load(session.Id);
        Assert.NotNull(loaded);
        Assert.Equal(session.Id, loaded.Id);
        Assert.Equal("Test Chat", loaded.Title);
        Assert.Single(loaded.Messages);
        Assert.Equal("Hello", loaded.Messages[0].Content);
    }

    [Fact]
    public void Save_UpdatesTimestamp()
    {
        var session = CreateSession();
        var beforeSave = DateTimeOffset.UtcNow;
        Thread.Sleep(10);

        AiChatStore.Save(session);

        var loaded = AiChatStore.Load(session.Id);
        Assert.NotNull(loaded);
        Assert.True(loaded.UpdatedAt >= beforeSave);
    }

    [Fact]
    public void Save_OverwritesExisting()
    {
        var session = CreateSession();
        AiChatStore.Save(session);

        session.Title = "Updated Title";
        session.Messages.Add(new AiChatMessage("assistant", "Hi!", DateTimeOffset.UtcNow));
        AiChatStore.Save(session);

        var loaded = AiChatStore.Load(session.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Updated Title", loaded.Title);
        Assert.Equal(2, loaded.Messages.Count);
    }

    [Fact]
    public void Save_PermissionMode_Persists()
    {
        var session = CreateSession();
        session.PermissionMode = "YesMan";
        AiChatStore.Save(session);

        var loaded = AiChatStore.Load(session.Id);
        Assert.NotNull(loaded);
        Assert.Equal("YesMan", loaded.PermissionMode);
    }

    // ── Load ────────────────────────────────────────────────────────

    [Fact]
    public void Load_NonexistentId_ReturnsNull()
    {
        var loaded = AiChatStore.Load("nonexistent-chat-id-" + Guid.NewGuid());
        Assert.Null(loaded);
    }

    // ── ListAll ─────────────────────────────────────────────────────

    [Fact]
    public void ListAll_IncludesSavedSessions()
    {
        var session = CreateSession();
        AiChatStore.Save(session);

        var all = AiChatStore.ListAll();
        Assert.Contains(all, s => s.Id == session.Id);
    }

    [Fact]
    public void ListAll_OrderedByUpdatedAtDescending()
    {
        var session1 = CreateSession(title: "First");
        AiChatStore.Save(session1);
        Thread.Sleep(50);

        var session2 = CreateSession(title: "Second");
        AiChatStore.Save(session2);

        var all = AiChatStore.ListAll();
        var idx1 = all.FindIndex(s => s.Id == session1.Id);
        var idx2 = all.FindIndex(s => s.Id == session2.Id);

        if (idx1 >= 0 && idx2 >= 0)
        {
            Assert.True(idx2 < idx1, "Most recently updated session should come first");
        }
    }

    // ── Delete ──────────────────────────────────────────────────────

    [Fact]
    public void Delete_RemovesSession()
    {
        var session = CreateSession();
        AiChatStore.Save(session);

        AiChatStore.Delete(session.Id);

        var loaded = AiChatStore.Load(session.Id);
        Assert.Null(loaded);
    }

    [Fact]
    public void Delete_NonexistentId_NoError()
    {
        // Should not throw
        AiChatStore.Delete("nonexistent-" + Guid.NewGuid());
    }

    // ── Rename ──────────────────────────────────────────────────────

    [Fact]
    public void Rename_ChangesTitle()
    {
        var session = CreateSession(title: "Original");
        AiChatStore.Save(session);

        AiChatStore.Rename(session.Id, "Renamed");

        var loaded = AiChatStore.Load(session.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Renamed", loaded.Title);
    }

    [Fact]
    public void Rename_NonexistentId_NoError()
    {
        // Should not throw
        AiChatStore.Rename("nonexistent-" + Guid.NewGuid(), "New Title");
    }

    [Fact]
    public void Rename_PreservesMessages()
    {
        var session = CreateSession();
        session.Messages.Add(new AiChatMessage("assistant", "response", DateTimeOffset.UtcNow));
        AiChatStore.Save(session);

        AiChatStore.Rename(session.Id, "New Name");

        var loaded = AiChatStore.Load(session.Id);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Messages.Count);
    }

    // ── AiChatSession defaults ──────────────────────────────────────

    [Fact]
    public void AiChatSession_Defaults_AreReasonable()
    {
        var session = new AiChatSession();
        Assert.Equal("New Chat", session.Title);
        Assert.Equal("Normal", session.PermissionMode);
        Assert.Empty(session.Messages);
    }

    // ── Constructor ─────────────────────────────────────────────────

    [Fact]
    public void Constructor_CreatesDirectory()
    {
        // Just verifying it doesn't throw
        _ = new AiChatStore();
    }
}
