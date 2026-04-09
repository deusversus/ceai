using System.Text.Json;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

public sealed class McpClientTests : IAsyncDisposable
{
    // ── StubMcpTransport ──

    private sealed class StubMcpTransport : IMcpTransport
    {
        private bool _connected;
        public bool IsConnected => _connected;
        public int ConnectCallCount { get; private set; }
        public List<(string Method, int Id)> SentRequests { get; } = [];
        public List<string> SentNotifications { get; } = [];

        /// <summary>Queue of responses to return for SendRequestAsync calls.</summary>
        public Queue<JsonElement> ResponseQueue { get; } = new();

        /// <summary>If set, ConnectAsync will throw this on next call.</summary>
        public Exception? ConnectException { get; set; }

        public Task ConnectAsync(CancellationToken ct)
        {
            ConnectCallCount++;
            if (ConnectException is not null)
                throw ConnectException;
            _connected = true;
            return Task.CompletedTask;
        }

        public Task<JsonElement> SendRequestAsync(string method, object? parameters, int requestId, CancellationToken ct)
        {
            SentRequests.Add((method, requestId));
            if (ResponseQueue.Count > 0)
                return Task.FromResult(ResponseQueue.Dequeue());
            return Task.FromResult(default(JsonElement));
        }

        public Task SendNotificationAsync(string method, object? parameters, CancellationToken ct)
        {
            SentNotifications.Add(method);
            return Task.CompletedTask;
        }

        public void SimulateDisconnect() => _connected = false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static McpServerConfig CreateConfig(string name = "test-server", string transport = "stdio") =>
        new()
        {
            Name = name,
            Command = "echo",
            Transport = transport,
        };

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── McpServerConfig Tests ──

    [Fact]
    public void McpServerConfig_Defaults_AreCorrect()
    {
        var config = CreateConfig();
        Assert.Equal("test-server", config.Name);
        Assert.Equal("echo", config.Command);
        Assert.True(config.AutoConnect);
        Assert.Equal("stdio", config.Transport);
        Assert.Null(config.SseUrl);
        Assert.Null(config.Arguments);
        Assert.Null(config.Environment);
    }

    // ── McpToolInfo Tests ──

    [Fact]
    public void McpToolInfo_Defaults_AreCorrect()
    {
        var info = new McpToolInfo();
        Assert.Equal("", info.Name);
        Assert.Null(info.Description);
        Assert.Null(info.InputSchema);
        Assert.Null(info.Annotations);
    }

    [Fact]
    public void McpToolInfo_Deserialization_Works()
    {
        var json = """{"name":"readFile","description":"Read a file","annotations":{"readOnlyHint":true}}""";
        var info = JsonSerializer.Deserialize<McpToolInfo>(json, StdioMcpTransport.JsonOpts)!;
        Assert.Equal("readFile", info.Name);
        Assert.Equal("Read a file", info.Description);
        Assert.NotNull(info.Annotations);
        Assert.True(info.Annotations!.ReadOnlyHint);
    }

    // ── McpToolAnnotations Tests ──

    [Fact]
    public void McpToolAnnotations_AllNullByDefault()
    {
        var annotations = new McpToolAnnotations();
        Assert.Null(annotations.ReadOnlyHint);
        Assert.Null(annotations.DestructiveHint);
        Assert.Null(annotations.IdempotentHint);
        Assert.Null(annotations.OpenWorldHint);
    }

    [Fact]
    public void McpToolAnnotations_Deserialization_ParsesAllHints()
    {
        var json = """{"readOnlyHint":true,"destructiveHint":false,"idempotentHint":true,"openWorldHint":false}""";
        var annotations = JsonSerializer.Deserialize<McpToolAnnotations>(json, StdioMcpTransport.JsonOpts)!;
        Assert.True(annotations.ReadOnlyHint);
        Assert.False(annotations.DestructiveHint);
        Assert.True(annotations.IdempotentHint);
        Assert.False(annotations.OpenWorldHint);
    }

    // ── McpClient with StubTransport ──

    [Fact]
    public async Task ConnectAsync_SendsInitializeAndNotification()
    {
        var transport = new StubMcpTransport();
        transport.ResponseQueue.Enqueue(ParseJson("""{"protocolVersion":"2024-11-05","capabilities":{}}"""));

        var client = new McpClient(CreateConfig(), transport);
        await client.ConnectAsync();

        Assert.True(client.IsConnected);
        Assert.Single(transport.SentRequests);
        Assert.Equal("initialize", transport.SentRequests[0].Method);
        Assert.Single(transport.SentNotifications);
        Assert.Equal("notifications/initialized", transport.SentNotifications[0]);
    }

    [Fact]
    public async Task DiscoverToolsAsync_ReturnsWrappedFunctions()
    {
        var transport = new StubMcpTransport();
        // Initialize response
        transport.ResponseQueue.Enqueue(ParseJson("""{"protocolVersion":"2024-11-05"}"""));
        // tools/list response
        transport.ResponseQueue.Enqueue(ParseJson("""{"tools":[{"name":"readFile","description":"Read file"},{"name":"writeFile","description":"Write file"}]}"""));

        var client = new McpClient(CreateConfig(), transport);
        await client.ConnectAsync();
        var functions = await client.DiscoverToolsAsync();

        Assert.Equal(2, functions.Count);
        Assert.Equal(2, client.DiscoveredTools.Count);
        Assert.Equal("readFile", client.DiscoveredTools[0].Name);
        Assert.Equal("writeFile", client.DiscoveredTools[1].Name);
    }

    [Fact]
    public async Task DiscoverToolsAsync_NullTools_ReturnsEmpty()
    {
        var transport = new StubMcpTransport();
        transport.ResponseQueue.Enqueue(ParseJson("""{"protocolVersion":"2024-11-05"}"""));
        transport.ResponseQueue.Enqueue(ParseJson("""{}"""));

        var client = new McpClient(CreateConfig(), transport);
        await client.ConnectAsync();
        var functions = await client.DiscoverToolsAsync();

        Assert.Empty(functions);
        Assert.Empty(client.DiscoveredTools);
    }

    [Fact]
    public async Task CallToolAsync_ReturnsTextContent()
    {
        var transport = new StubMcpTransport();
        transport.ResponseQueue.Enqueue(ParseJson("""{"protocolVersion":"2024-11-05"}"""));
        transport.ResponseQueue.Enqueue(ParseJson("""{"content":[{"type":"text","text":"file contents here"}]}"""));

        var client = new McpClient(CreateConfig(), transport);
        await client.ConnectAsync();
        var result = await client.CallToolAsync("readFile", new Dictionary<string, object?> { ["path"] = "/test.txt" });

        Assert.Equal("file contents here", result);
    }

    [Fact]
    public async Task CallToolAsync_EmptyContent_ReturnsNoOutput()
    {
        var transport = new StubMcpTransport();
        transport.ResponseQueue.Enqueue(ParseJson("""{"protocolVersion":"2024-11-05"}"""));
        transport.ResponseQueue.Enqueue(ParseJson("""{"content":[]}"""));

        var client = new McpClient(CreateConfig(), transport);
        await client.ConnectAsync();
        var result = await client.CallToolAsync("emptyTool");

        Assert.Equal("(no output)", result);
    }

    [Fact]
    public async Task CallToolAsync_NullContent_ReturnsNoOutput()
    {
        var transport = new StubMcpTransport();
        transport.ResponseQueue.Enqueue(ParseJson("""{"protocolVersion":"2024-11-05"}"""));
        transport.ResponseQueue.Enqueue(ParseJson("""{}"""));

        var client = new McpClient(CreateConfig(), transport);
        await client.ConnectAsync();
        var result = await client.CallToolAsync("nullTool");

        Assert.Equal("(no output)", result);
    }

    [Fact]
    public async Task CallToolAsync_MultipleTextBlocks_JoinsWithNewline()
    {
        var transport = new StubMcpTransport();
        transport.ResponseQueue.Enqueue(ParseJson("""{"protocolVersion":"2024-11-05"}"""));
        transport.ResponseQueue.Enqueue(ParseJson("""{"content":[{"type":"text","text":"line1"},{"type":"text","text":"line2"}]}"""));

        var client = new McpClient(CreateConfig(), transport);
        await client.ConnectAsync();
        var result = await client.CallToolAsync("multiTool");

        Assert.Equal("line1\nline2", result);
    }

    [Fact]
    public void Config_ExposedCorrectly()
    {
        var config = CreateConfig("my-server");
        var transport = new StubMcpTransport();
        var client = new McpClient(config, transport);

        Assert.Equal("my-server", client.Config.Name);
        Assert.Same(transport, client.Transport);
    }

    [Fact]
    public void IsConnected_DelegatesToTransport()
    {
        var transport = new StubMcpTransport();
        var client = new McpClient(CreateConfig(), transport);

        Assert.False(client.IsConnected);
    }

    // ── McpManager Tests ──

    [Fact]
    public void McpManager_GetStatusSummary_NoServers_ReturnsMessage()
    {
        var manager = new McpManager();
        Assert.Equal("No MCP servers connected.", manager.GetStatusSummary());
    }

    [Fact]
    public async Task McpManager_RemoveServer_NonExistent_DoesNotThrow()
    {
        var manager = new McpManager();
        await manager.RemoveServerAsync("nonexistent");
        // Should not throw
    }

    [Fact]
    public async Task McpManager_DisposeAsync_ClearsClients()
    {
        var manager = new McpManager();
        await manager.DisposeAsync();
        Assert.Empty(manager.Clients);
    }

    // ── JsonSerializerOptions ──

    [Fact]
    public void JsonOpts_CamelCasePolicy()
    {
        var opts = StdioMcpTransport.JsonOpts;
        Assert.Equal(JsonNamingPolicy.CamelCase, opts.PropertyNamingPolicy);
        Assert.True(opts.PropertyNameCaseInsensitive);
    }

    public async ValueTask DisposeAsync()
    {
        // Nothing to dispose in test fixture
        await Task.CompletedTask;
    }
}
