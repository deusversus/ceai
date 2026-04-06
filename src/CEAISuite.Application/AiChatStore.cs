using System.Text.Json;

namespace CEAISuite.Application;

/// <summary>Persisted chat session metadata + messages.</summary>
public sealed class AiChatSession
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "New Chat";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AiChatMessage> Messages { get; set; } = new();

    /// <summary>Per-chat permission mode. Persisted so each chat retains its own access policy.</summary>
    public string PermissionMode { get; set; } = "Normal";
}

/// <summary>
/// Persists AI chat sessions to JSON files in %LOCALAPPDATA%/CEAISuite/chats/.
/// </summary>
public sealed class AiChatStore
{
    private static readonly string ChatsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CEAISuite", "chats");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AiChatStore()
    {
        try { Directory.CreateDirectory(ChatsDir); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[AiChatStore] Failed to create chats directory: {ex.Message}"); }
    }

    public static void Save(AiChatSession session)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        var path = Path.Combine(ChatsDir, $"{session.Id}.json");
        var json = JsonSerializer.Serialize(session, JsonOpts);
        File.WriteAllText(path, json);
    }

    public static AiChatSession? Load(string chatId)
    {
        var path = Path.Combine(ChatsDir, $"{chatId}.json");
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AiChatSession>(json, JsonOpts);
    }

    public static List<AiChatSession> ListAll()
    {
        var sessions = new List<AiChatSession>();
        if (!Directory.Exists(ChatsDir)) return sessions;

        foreach (var file in Directory.GetFiles(ChatsDir, "*.json"))
        {
            // Skip companion metadata files (e.g., chat-xxx.session.json)
            if (file.EndsWith(".session.json", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var json = File.ReadAllText(file);
                var session = JsonSerializer.Deserialize<AiChatSession>(json, JsonOpts);
                if (session is not null && !string.IsNullOrEmpty(session.Id))
                    sessions.Add(session);
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[AiChatStore] Failed to load chat file {file}: {ex.Message}"); }
        }

        return sessions.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public static void Delete(string chatId)
    {
        var path = Path.Combine(ChatsDir, $"{chatId}.json");
        if (File.Exists(path)) File.Delete(path);

        // Also delete companion metadata file if it exists
        var sessionPath = Path.Combine(ChatsDir, $"{chatId}.session.json");
        if (File.Exists(sessionPath)) File.Delete(sessionPath);
    }

    public static void Rename(string chatId, string newTitle)
    {
        var session = Load(chatId);
        if (session is null) return;
        session.Title = newTitle;
        Save(session);
    }
}
