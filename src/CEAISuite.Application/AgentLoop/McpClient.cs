using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace CEAISuite.Application.AgentLoop;

// ── Transport Abstraction ──────────────────────────────────────────

/// <summary>
/// Transport layer for MCP JSON-RPC 2.0 communication. Implementations
/// handle the physical channel (stdio pipes, SSE + HTTP POST, etc.)
/// while <see cref="McpClient"/> handles the protocol logic.
///
/// Modeled after Claude Code's MCP transport abstraction supporting
/// both stdio and SSE transports.
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>Whether the transport is currently connected and usable.</summary>
    bool IsConnected { get; }

    /// <summary>Establish the transport connection.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Send a JSON-RPC request and return the raw JSON response.</summary>
    Task<JsonElement> SendRequestAsync(string method, object? parameters, int requestId, CancellationToken ct);

    /// <summary>Send a JSON-RPC notification (fire-and-forget, no response expected).</summary>
    Task SendNotificationAsync(string method, object? parameters, CancellationToken ct);
}

// ── Stdio Transport ────────────────────────────────────────────────

/// <summary>
/// Standard I/O transport for MCP servers that run as child processes.
/// Communication happens via stdin (requests) and stdout (responses)
/// using newline-delimited JSON-RPC 2.0.
/// </summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly McpServerConfig _config;
    private readonly Action<string, string>? _log;
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public StdioMcpTransport(McpServerConfig config, Action<string, string>? log = null)
    {
        _config = config;
        _log = log;
    }

    public bool IsConnected => _process is { HasExited: false };

    public async Task ConnectAsync(CancellationToken ct)
    {
        _log?.Invoke("MCP", $"[stdio] Connecting to {_config.Name} ({_config.Command})");

        var psi = new ProcessStartInfo
        {
            FileName = _config.Command,
            Arguments = _config.Arguments ?? "",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (_config.Environment is not null)
        {
            foreach (var (key, value) in _config.Environment)
                psi.EnvironmentVariables[key] = value;
        }

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start MCP server: {_config.Command}");

        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;

        _log?.Invoke("MCP", $"[stdio] Process started for {_config.Name} (PID {_process.Id})");
    }

    public async Task<JsonElement> SendRequestAsync(string method, object? parameters, int requestId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var request = new
            {
                jsonrpc = "2.0",
                id = requestId,
                method,
                @params = parameters,
            };

            var json = JsonSerializer.Serialize(request, JsonOpts);
            await _stdin!.WriteLineAsync(json.AsMemory(), ct);
            await _stdin.FlushAsync(ct);

            // Read response lines until we get our id back
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await _stdout!.ReadLineAsync(ct);
                if (line is null)
                    throw new InvalidOperationException("MCP server closed stdout unexpectedly");

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var respId)
                        && respId.ValueKind == JsonValueKind.Number
                        && respId.GetInt32() == requestId)
                    {
                        if (doc.RootElement.TryGetProperty("error", out var error))
                        {
                            var errMsg = error.TryGetProperty("message", out var m) ? m.GetString() : "Unknown MCP error";
                            throw new InvalidOperationException($"MCP error: {errMsg}");
                        }

                        if (doc.RootElement.TryGetProperty("result", out var result))
                            return result.Clone();

                        return default;
                    }
                }
                catch (JsonException)
                {
                    _log?.Invoke("MCP", $"Non-JSON from server: {line[..Math.Min(100, line.Length)]}");
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SendNotificationAsync(string method, object? parameters, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var notification = new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters,
            };

            var json = JsonSerializer.Serialize(notification, JsonOpts);
            await _stdin!.WriteLineAsync(json.AsMemory(), ct);
            await _stdin.FlushAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _stdin?.Close();
                if (!_process.WaitForExit(3000))
                    _process.Kill();
            }
            catch (Exception ex) { _log?.Invoke("MCP", $"[stdio] Cleanup error: {ex.Message}"); }
        }
        _process?.Dispose();
        _lock.Dispose();
        await Task.CompletedTask;
    }

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

// ── SSE Transport ──────────────────────────────────────────────────

/// <summary>
/// Server-Sent Events (SSE) transport for remote MCP servers.
///
/// Communication model:
/// - Outbound requests: HTTP POST with JSON-RPC body to the server's message endpoint
/// - Inbound responses: SSE stream from the server's events endpoint, correlated by request ID
///
/// The SSE stream is opened during <see cref="ConnectAsync"/> and maintained
/// for the lifetime of the transport. Each JSON-RPC response arrives as an
/// SSE "message" event and is routed to the waiting caller via a
/// <see cref="TaskCompletionSource{T}"/>.
///
/// Modeled after Claude Code's SSE MCP transport.
/// </summary>
public sealed class SseMcpTransport : IMcpTransport
{
    private readonly McpServerConfig _config;
    private readonly Action<string, string>? _log;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private CancellationTokenSource? _sseCts;
    private Task? _sseListenerTask;
    private string? _messageEndpoint;
    private volatile bool _connected;

    public SseMcpTransport(McpServerConfig config, Action<string, string>? log = null)
    {
        _config = config;
        _log = log;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public bool IsConnected => _connected;

    public async Task ConnectAsync(CancellationToken ct)
    {
        var sseUrl = _config.SseUrl
            ?? throw new InvalidOperationException($"SSE URL is required for SSE transport (server: {_config.Name})");

        _log?.Invoke("MCP", $"[sse] Connecting to {_config.Name} ({sseUrl})");

        // Set auth headers if configured via environment
        if (_config.Environment?.TryGetValue("MCP_AUTH_TOKEN", out var token) == true)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Open SSE stream
        _sseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var request = new HttpRequestMessage(HttpMethod.Get, sseUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var reader = new StreamReader(stream);

        // Read the initial endpoint event to discover the message URL
        _messageEndpoint = await ReadEndpointFromSse(reader, sseUrl, ct);
        _log?.Invoke("MCP", $"[sse] Message endpoint: {_messageEndpoint}");

        // Start background listener for response events
        _sseListenerTask = Task.Run(() => ListenForSseEvents(reader, _sseCts.Token), _sseCts.Token);
        _connected = true;
    }

    public async Task<JsonElement> SendRequestAsync(string method, object? parameters, int requestId, CancellationToken ct)
    {
        if (_messageEndpoint is null)
            throw new InvalidOperationException("SSE transport not connected");

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = tcs;

        // Register cancellation
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        try
        {
            var rpcRequest = new
            {
                jsonrpc = "2.0",
                id = requestId,
                method,
                @params = parameters,
            };

            var json = JsonSerializer.Serialize(rpcRequest, StdioMcpTransport.JsonOpts);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_messageEndpoint, content, ct);
            response.EnsureSuccessStatusCode();

            // Wait for the SSE listener to deliver the matching response
            return await tcs.Task;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    public async Task SendNotificationAsync(string method, object? parameters, CancellationToken ct)
    {
        if (_messageEndpoint is null)
            throw new InvalidOperationException("SSE transport not connected");

        var notification = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
        };

        var json = JsonSerializer.Serialize(notification, StdioMcpTransport.JsonOpts);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_messageEndpoint, content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Read the initial "endpoint" SSE event that tells us the POST URL.
    /// The server sends: event: endpoint\ndata: /message?sessionId=xxx
    /// </summary>
    private static async Task<string> ReadEndpointFromSse(StreamReader reader, string baseUrl, CancellationToken ct)
    {
        string? eventType = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) throw new InvalidOperationException("SSE stream closed before endpoint event");

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal) && eventType == "endpoint")
            {
                var endpoint = line[5..].Trim();
                // Resolve relative URL against base
                if (endpoint.StartsWith('/'))
                {
                    var uri = new Uri(baseUrl);
                    return $"{uri.Scheme}://{uri.Authority}{endpoint}";
                }
                return endpoint;
            }
            else if (string.IsNullOrEmpty(line))
            {
                eventType = null; // Reset on blank line (event boundary)
            }
        }

        throw new OperationCanceledException("Cancelled while waiting for SSE endpoint event");
    }

    /// <summary>
    /// Background listener that reads SSE events and routes JSON-RPC responses
    /// to the matching pending request via <see cref="TaskCompletionSource{T}"/>.
    /// </summary>
    private async Task ListenForSseEvents(StreamReader reader, CancellationToken ct)
    {
        string? eventType = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break; // Stream closed

                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventType = line[6..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal) && eventType == "message")
                {
                    var data = line[5..].Trim();
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (doc.RootElement.TryGetProperty("id", out var idProp)
                            && idProp.ValueKind == JsonValueKind.Number)
                        {
                            var id = idProp.GetInt32();
                            if (_pendingRequests.TryRemove(id, out var tcs))
                            {
                                if (doc.RootElement.TryGetProperty("error", out var error))
                                {
                                    var errMsg = error.TryGetProperty("message", out var m)
                                        ? m.GetString() : "Unknown MCP error";
                                    tcs.TrySetException(new InvalidOperationException($"MCP error: {errMsg}"));
                                }
                                else if (doc.RootElement.TryGetProperty("result", out var result))
                                {
                                    tcs.TrySetResult(result.Clone());
                                }
                                else
                                {
                                    tcs.TrySetResult(default);
                                }
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        _log?.Invoke("MCP", $"[sse] Invalid JSON in SSE data: {ex.Message}");
                    }
                }
                else if (string.IsNullOrEmpty(line))
                {
                    eventType = null;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _log?.Invoke("MCP", $"[sse] SSE listener error: {ex.Message}");
        }
        finally
        {
            _connected = false;
            // Cancel all pending requests
            foreach (var (_, tcs) in _pendingRequests)
                tcs.TrySetCanceled(ct);
            _pendingRequests.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _connected = false;
        _sseCts?.Cancel();

        if (_sseListenerTask is not null)
        {
            try { await _sseListenerTask; } catch (Exception ex) { _log?.Invoke("MCP", $"[sse] Listener task ended: {ex.Message}"); }
        }

        _sseCts?.Dispose();
        _httpClient.Dispose();

        // Cancel all pending requests
        foreach (var (_, tcs) in _pendingRequests)
            tcs.TrySetCanceled();
        _pendingRequests.Clear();
    }
}

// ── MCP Client (protocol layer) ────────────────────────────────────

/// <summary>
/// Lightweight Model Context Protocol (MCP) client that connects to external
/// tool servers and bridges their tools into our <see cref="AgentLoop"/>.
///
/// MCP is the standard protocol (open spec from Anthropic) for connecting
/// AI agents to external capabilities. An MCP server exposes tools, resources,
/// and prompts over a JSON-RPC 2.0 transport (stdio or SSE).
///
/// This client:
/// 1. Delegates transport to an <see cref="IMcpTransport"/> implementation
/// 2. Discovers available tools via <c>tools/list</c>
/// 3. Wraps each tool as an <see cref="AIFunction"/> compatible with our tool system
/// 4. Forwards <c>tools/call</c> invocations to the server
///
/// Modeled after Claude Code's MCP integration where servers are configured
/// in settings and their tools appear alongside native tools.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly McpServerConfig _config;
    private readonly IMcpTransport _transport;
    private readonly Action<string, string>? _log;
    private int _requestId;
    private readonly List<McpToolInfo> _discoveredTools = [];
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 5;
    private static readonly TimeSpan[] ReconnectDelays = [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
    ];

    public McpClient(McpServerConfig config, Action<string, string>? log = null)
        : this(config, CreateDefaultTransport(config, log), log) { }

    public McpClient(McpServerConfig config, IMcpTransport transport, Action<string, string>? log = null)
    {
        _config = config;
        _transport = transport;
        _log = log;
    }

    /// <summary>The server configuration.</summary>
    public McpServerConfig Config => _config;

    /// <summary>Whether the server is connected.</summary>
    public bool IsConnected => _transport.IsConnected;

    /// <summary>Tools discovered from this server.</summary>
    public IReadOnlyList<McpToolInfo> DiscoveredTools => _discoveredTools;

    /// <summary>The underlying transport (exposed for testing).</summary>
    public IMcpTransport Transport => _transport;

    /// <summary>
    /// Connect to the MCP server and perform the initialize handshake.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _log?.Invoke("MCP", $"Connecting to {_config.Name} via {_config.Transport}");

        await _transport.ConnectAsync(ct);

        // Send initialize request
        var initResult = await SendRequestAsync<JsonElement>("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "CEAISuite", version = "1.0" },
        }, ct);

        _log?.Invoke("MCP", $"Server {_config.Name} initialized: {initResult}");

        // Send initialized notification
        await _transport.SendNotificationAsync("notifications/initialized", null, ct);
    }

    /// <summary>
    /// Discover tools from the MCP server and return them as AIFunction wrappers.
    /// </summary>
    public async Task<List<AIFunction>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        var result = await SendRequestAsync<McpToolListResult>("tools/list", new { }, ct);
        _discoveredTools.Clear();

        var functions = new List<AIFunction>();

        if (result.Tools is null) return functions;

        foreach (var tool in result.Tools)
        {
            _discoveredTools.Add(tool);

            var mcpToolName = tool.Name;
            var description = tool.Description ?? mcpToolName;
            var serverName = _config.Name;
            var client = this;

            var fn = AIFunctionFactory.Create(
                async (IDictionary<string, object?> arguments) =>
                {
                    return await client.CallToolAsync(mcpToolName, arguments);
                },
                $"mcp_{serverName}_{mcpToolName}",
                $"[MCP:{serverName}] {description}");

            functions.Add(fn);
            _log?.Invoke("MCP", $"Discovered tool: {_config.Name}/{mcpToolName} — {description}");
        }

        _log?.Invoke("MCP", $"Server {_config.Name}: {functions.Count} tools discovered");
        return functions;
    }

    /// <summary>Call a tool on the MCP server.</summary>
    public async Task<string> CallToolAsync(
        string toolName,
        IDictionary<string, object?>? arguments = null,
        CancellationToken ct = default)
    {
        var result = await SendRequestAsync<McpToolCallResult>("tools/call", new
        {
            name = toolName,
            arguments = arguments ?? new Dictionary<string, object?>(),
        }, ct);

        if (result.Content is null or { Length: 0 })
            return "(no output)";

        var texts = result.Content
            .Where(c => c.Type == "text")
            .Select(c => c.Text ?? "");
        return string.Join("\n", texts);
    }

    /// <summary>
    /// Attempt to reconnect with exponential backoff.
    /// </summary>
    public async Task<bool> TryReconnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return true;

        for (int i = 0; i < MaxReconnectAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            _reconnectAttempts++;

            var delay = ReconnectDelays[Math.Min(i, ReconnectDelays.Length - 1)];
            _log?.Invoke("MCP", $"Reconnect attempt {i + 1}/{MaxReconnectAttempts} in {delay.TotalSeconds:F0}s...");

            await Task.Delay(delay, ct);

            try
            {
                await _transport.ConnectAsync(ct);
                _reconnectAttempts = 0;
                _log?.Invoke("MCP", $"Reconnected to {_config.Name}");
                return true;
            }
            catch (Exception ex)
            {
                _log?.Invoke("MCP", $"Reconnect attempt {i + 1} failed: {ex.Message}");
            }
        }

        _log?.Invoke("MCP", $"Failed to reconnect to {_config.Name} after {MaxReconnectAttempts} attempts");
        return false;
    }

    private async Task<T> SendRequestAsync<T>(string method, object? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _requestId);
        var result = await _transport.SendRequestAsync(method, parameters, id, ct);
        return JsonSerializer.Deserialize<T>(result.GetRawText(), StdioMcpTransport.JsonOpts)!;
    }

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
    }

    /// <summary>Create the default transport based on config.</summary>
    private static IMcpTransport CreateDefaultTransport(McpServerConfig config, Action<string, string>? log)
    {
        return config.Transport.ToLowerInvariant() switch
        {
            "sse" => new SseMcpTransport(config, log),
            _ => new StdioMcpTransport(config, log),
        };
    }
}

/// <summary>Configuration for an MCP server connection.</summary>
public sealed record McpServerConfig
{
    /// <summary>Human-readable server name.</summary>
    public required string Name { get; init; }

    /// <summary>Command to execute (e.g., "npx", "python", path to binary).</summary>
    public required string Command { get; init; }

    /// <summary>Command-line arguments.</summary>
    public string? Arguments { get; init; }

    /// <summary>Environment variables to set for the server process.</summary>
    public Dictionary<string, string>? Environment { get; init; }

    /// <summary>Whether to auto-connect on startup.</summary>
    public bool AutoConnect { get; init; } = true;

    /// <summary>Transport type: "stdio" (default) or "sse".</summary>
    public string Transport { get; init; } = "stdio";

    /// <summary>SSE endpoint URL (required when Transport is "sse").</summary>
    public string? SseUrl { get; init; }
}

/// <summary>Tool information returned by MCP tools/list.</summary>
public sealed record McpToolInfo
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("inputSchema")] public JsonElement? InputSchema { get; init; }
    [JsonPropertyName("annotations")] public McpToolAnnotations? Annotations { get; init; }
}

/// <summary>
/// MCP tool annotations that hint at tool behavior. Used to automatically
/// classify tools for permission and concurrency decisions.
/// </summary>
public sealed record McpToolAnnotations
{
    [JsonPropertyName("readOnlyHint")] public bool? ReadOnlyHint { get; init; }
    [JsonPropertyName("destructiveHint")] public bool? DestructiveHint { get; init; }
    [JsonPropertyName("idempotentHint")] public bool? IdempotentHint { get; init; }
    [JsonPropertyName("openWorldHint")] public bool? OpenWorldHint { get; init; }
}

// ── MCP response DTOs ──

internal sealed record McpToolListResult
{
    [JsonPropertyName("tools")] public McpToolInfo[]? Tools { get; init; }
}

internal sealed record McpToolCallResult
{
    [JsonPropertyName("content")] public McpContentBlock[]? Content { get; init; }
    [JsonPropertyName("isError")] public bool IsError { get; init; }
}

internal sealed record McpContentBlock
{
    [JsonPropertyName("type")] public string Type { get; init; } = "text";
    [JsonPropertyName("text")] public string? Text { get; init; }
}

/// <summary>
/// Manages multiple MCP server connections. Handles lifecycle, tool discovery,
/// and bridges MCP tools into the agent's tool list.
/// </summary>
public sealed class McpManager : IAsyncDisposable
{
    private readonly List<McpClient> _clients = [];
    private readonly object _clientsLock = new();
    private readonly Action<string, string>? _log;

    public McpManager(Action<string, string>? log = null) => _log = log;

    /// <summary>Active MCP server connections (snapshot).</summary>
    public IReadOnlyList<McpClient> Clients { get { lock (_clientsLock) return _clients.ToList(); } }

    /// <summary>
    /// Add and connect to an MCP server. Automatically selects the correct
    /// transport based on <see cref="McpServerConfig.Transport"/>.
    /// Returns the AIFunctions discovered.
    /// </summary>
    public async Task<List<AIFunction>> AddServerAsync(
        McpServerConfig config, CancellationToken ct = default)
    {
        // Transport selection happens inside McpClient constructor
        var client = new McpClient(config, _log);

        try
        {
            await client.ConnectAsync(ct);
            var tools = await client.DiscoverToolsAsync(ct);
            lock (_clientsLock) _clients.Add(client);
            _log?.Invoke("MCP", $"Server '{config.Name}' connected via {config.Transport} with {tools.Count} tools");
            return tools;
        }
        catch (Exception ex)
        {
            _log?.Invoke("MCP", $"Failed to connect to '{config.Name}': {ex.Message}");
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Disconnect and remove a server by name.</summary>
    public async Task RemoveServerAsync(string name)
    {
        McpClient? client;
        lock (_clientsLock)
        {
            client = _clients.FirstOrDefault(c =>
                string.Equals(c.Config.Name, name, StringComparison.OrdinalIgnoreCase));
            if (client is null) return;
            _clients.Remove(client);
        }

        await client.DisposeAsync();
        _log?.Invoke("MCP", $"Server '{name}' disconnected");
    }

    /// <summary>Get a summary of all connected servers for display.</summary>
    public string GetStatusSummary()
    {
        List<McpClient> snapshot;
        lock (_clientsLock) snapshot = _clients.ToList();

        if (snapshot.Count == 0)
            return "No MCP servers connected.";

        var lines = snapshot.Select(c =>
            $"  {(c.IsConnected ? "✓" : "✗")} {c.Config.Name} ({c.Config.Transport}) — {c.DiscoveredTools.Count} tools");
        return $"MCP servers ({snapshot.Count}):\n{string.Join("\n", lines)}";
    }

    public async ValueTask DisposeAsync()
    {
        List<McpClient> snapshot;
        lock (_clientsLock)
        {
            snapshot = _clients.ToList();
            _clients.Clear();
        }

        foreach (var client in snapshot)
            await client.DisposeAsync();
    }
}
