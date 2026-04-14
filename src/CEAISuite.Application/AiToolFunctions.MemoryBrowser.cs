using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using CEAISuite.Application.AgentLoop;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed partial class AiToolFunctions
{
    private readonly ConcurrentDictionary<string, BookmarkEntry> _bookmarks = new(StringComparer.OrdinalIgnoreCase);

    private sealed record BookmarkEntry(string Address, string Label, string? Notes, DateTimeOffset Created);

    // ── Byte pattern search ──

    [ReadOnlyTool]
    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [SearchHint("pattern", "byte search", "AOB", "scan bytes", "hex search")]
    [Description("Search for a byte pattern in process memory starting from an address. Pattern uses space-separated hex bytes with ?? for wildcards (e.g., '48 8B ?? 24 38').")]
    public async Task<string> SearchMemoryPattern(
        [Description("Process ID")] int processId,
        [Description("Start address (hex)")] string startAddress,
        [Description("Byte pattern: space-separated hex, ?? for wildcard (e.g., '48 8B ?? 24 38')")] string pattern,
        [Description("Maximum results to return")] int maxResults = 10,
        [Description("Number of bytes to search through (default 0x10000)")] int searchLength = 0x10000)
    {
        if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
        if (maxResults is < 1 or > 100) maxResults = _limits.MaxPatternSearchResults;
        if (searchLength is < 1 or > 0x1000000) searchLength = 0x10000; // cap at 16MB

        // Parse pattern
        var parts = pattern.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var patternBytes = new byte[parts.Length];
        var mask = new bool[parts.Length]; // true = must match, false = wildcard
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "??" || parts[i] == "?")
            {
                mask[i] = false;
                patternBytes[i] = 0;
            }
            else if (byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                mask[i] = true;
                patternBytes[i] = b;
            }
            else
            {
                return $"Invalid pattern byte '{parts[i]}'. Use hex bytes (00-FF) or ?? for wildcards.";
            }
        }

        var startAddr = ParseAddress(startAddress);
        var matches = new List<string>();
        const int chunkSize = 4096;

        for (int offset = 0; offset < searchLength && matches.Count < maxResults; offset += chunkSize)
        {
            var readAddr = (nuint)((ulong)startAddr + (ulong)offset);
            var readLen = Math.Min(chunkSize + patternBytes.Length - 1, searchLength - offset);
            if (readLen < patternBytes.Length) break;

            try
            {
                var mem = await engineFacade.ReadMemoryAsync(processId, readAddr, readLen).ConfigureAwait(false);
                var bytes = mem.Bytes;

                for (int i = 0; i <= bytes.Count - patternBytes.Length && matches.Count < maxResults; i++)
                {
                    bool found = true;
                    for (int j = 0; j < patternBytes.Length; j++)
                    {
                        if (mask[j] && bytes[i + j] != patternBytes[j])
                        {
                            found = false;
                            break;
                        }
                    }
                    if (found)
                    {
                        var matchAddr = (ulong)readAddr + (ulong)i;
                        matches.Add($"0x{matchAddr:X}");
                    }
                }
            }
            catch { /* unreadable region, skip */ }
        }

        if (matches.Count == 0)
            return $"Pattern '{pattern}' not found in 0x{searchLength:X} bytes from 0x{(ulong)startAddr:X}.";

        return ToJson(new { pattern, matches, count = matches.Count, searchedBytes = searchLength });
    }

    // ── Pointer following ──

    [ReadOnlyTool]
    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [SearchHint("pointer", "dereference", "follow", "chain", "pointer chain")]
    [Description("Follow a pointer chain from an address. Reads pointer-sized values and dereferences them up to the specified depth.")]
    public async Task<string> FollowPointer(
        [Description("Process ID")] int processId,
        [Description("Starting address (hex)")] string address,
        [Description("Number of dereference levels (default 1, max 10)")] int depth = 1)
    {
        if (!IsProcessAlive(processId)) return $"Process {processId} is no longer running.";
        if (depth is < 1 or > 10) depth = 1;

        var chain = new List<string>();
        var currentAddr = ParseAddress(address);
        chain.Add($"0x{(ulong)currentAddr:X}");

        for (int i = 0; i < depth; i++)
        {
            try
            {
                var mem = await engineFacade.ReadMemoryAsync(processId, currentAddr, 8).ConfigureAwait(false);
                if (mem.Bytes.Count < 8) return ToJson(new { chain, error = $"Read failed at depth {i + 1}", depth = i });

                var ptrValue = BitConverter.ToUInt64(mem.Bytes is byte[] arr ? arr : mem.Bytes.ToArray());
                if (ptrValue == 0)
                {
                    chain.Add("0x0 (null)");
                    return ToJson(new { chain, terminatedAt = "null pointer", depth = i + 1 });
                }

                currentAddr = (nuint)ptrValue;
                chain.Add($"0x{ptrValue:X}");
            }
            catch (Exception ex)
            {
                return ToJson(new { chain, error = $"Read failed at depth {i + 1}: {ex.Message}", depth = i });
            }
        }

        return ToJson(new { chain, depth, finalAddress = $"0x{(ulong)currentAddr:X}" });
    }

    // ── Bookmarks ──

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [SearchHint("bookmark", "mark", "save address", "favorite")]
    [Description("Add a bookmark for a memory address. Bookmarks persist for the session and help track interesting locations.")]
    public Task<string> AddBookmark(
        [Description("Memory address (hex)")] string address,
        [Description("Bookmark label")] string label,
        [Description("Optional notes")] string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(address)) return Task.FromResult("Address is required.");
        if (string.IsNullOrWhiteSpace(label)) return Task.FromResult("Label is required.");

        var key = address.Trim();
        var entry = new BookmarkEntry(key, label.Trim(), notes?.Trim(), DateTimeOffset.UtcNow);
        _bookmarks[key] = entry;
        return Task.FromResult($"Bookmark '{label}' added at {key}.");
    }

    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Small)]
    [Description("Remove a bookmark by address or label.")]
    public Task<string> RemoveBookmark(
        [Description("Address or label of the bookmark to remove")] string addressOrLabel)
    {
        if (string.IsNullOrWhiteSpace(addressOrLabel)) return Task.FromResult("Address or label is required.");

        var key = addressOrLabel.Trim();
        if (_bookmarks.TryRemove(key, out _))
            return Task.FromResult($"Bookmark at '{key}' removed.");

        // Try matching by label
        var byLabel = _bookmarks.FirstOrDefault(kvp =>
            kvp.Value.Label.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (byLabel.Key is not null && _bookmarks.TryRemove(byLabel.Key, out _))
            return Task.FromResult($"Bookmark '{byLabel.Value.Label}' removed.");

        return Task.FromResult($"Bookmark '{key}' not found.");
    }

    [ReadOnlyTool]
    [ConcurrencySafe]
    [MaxResultSize(MaxResultSizeAttribute.Medium)]
    [Description("List all bookmarked memory addresses.")]
    public Task<string> ListBookmarks()
    {
        if (_bookmarks.IsEmpty)
            return Task.FromResult("No bookmarks saved.");

        var entries = _bookmarks.Values
            .OrderBy(b => b.Created)
            .Take(_limits.MaxBookmarkResults)
            .Select(b => new { b.Address, b.Label, b.Notes, created = b.Created.ToString("g", System.Globalization.CultureInfo.InvariantCulture) });
        var truncated = _bookmarks.Count > _limits.MaxBookmarkResults;
        return Task.FromResult(ToJson(new { bookmarks = entries, count = _bookmarks.Count, truncated }));
    }

    // ── Export tools ──

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [SearchHint("export", "report", "summary", "markdown", "investigation")]
    [Description("Export a summary report of the current investigation (process, address table, breakpoints, session log). Optionally write to a markdown file.")]
    public Task<string> ExportReport(
        [Description("File path to write report (optional, returns content if omitted)")] string? filePath = null)
    {
        var dashboard = dashboardService.CurrentDashboard;
        var processName = dashboard?.CurrentInspection?.ProcessName ?? "none";
        var processId = dashboard?.CurrentInspection?.ProcessId ?? 0;

        var report = ScriptGenerationService.SummarizeInvestigation(
            processName, processId, addressTableService.Entries.ToList(),
            null, null);

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (filePath.Contains("..")) return Task.FromResult("Path traversal not allowed.");
            System.IO.File.WriteAllText(filePath, report);
            return Task.FromResult($"Report exported to {filePath} ({report.Length:#,0} chars).");
        }
        return Task.FromResult(report.Length > _limits.MaxExportChars
            ? TokenLimits.Truncate(report, _limits.MaxExportChars)
            : report);
    }

    [ReadOnlyTool]
    [MaxResultSize(MaxResultSizeAttribute.Large)]
    [SearchHint("export", "chat", "history", "conversation", "transcript")]
    [Description("Export the current AI chat history as JSON. Optionally write to a file.")]
    public Task<string> ExportChat(
        [Description("File path to write JSON (optional, returns content if omitted)")] string? filePath = null)
    {
        var messages = currentChatProvider?.Invoke();
        if (messages is null || messages.Count == 0)
            return Task.FromResult("No chat messages to export.");

        var exported = messages.Select(m => new
        {
            role = m.Role,
            content = m.Content?.Length > 500 ? m.Content[..500] + "..." : m.Content,
            hasToolCalls = m.ToolCalls?.Count > 0,
            timestamp = m.Timestamp.ToString("o")
        });

        var json = System.Text.Json.JsonSerializer.Serialize(exported, _jsonOptsIndented);

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (filePath.Contains("..")) return Task.FromResult("Path traversal not allowed.");
            System.IO.File.WriteAllText(filePath, json);
            return Task.FromResult($"Chat exported to {filePath} ({messages.Count} messages).");
        }
        return Task.FromResult(json.Length > _limits.MaxExportChars
            ? TokenLimits.Truncate(json, _limits.MaxExportChars)
            : json);
    }
}
