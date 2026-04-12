using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CEAISuite.Application;

namespace CEAISuite.Tests;

public class PluginCatalogServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ceai-catalog-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static HttpClient MockHttp(string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new MockHandler(content, status);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task FetchCatalog_ValidJson_ReturnsEntries()
    {
        var json = """
        [
            {"name": "TestPlugin", "version": "1.0", "description": "A test", "author": "Dev", "downloadUrl": "https://example.com/test.dll", "checksum": "abc123", "sizeBytes": 1024}
        ]
        """;
        using var svc = new PluginCatalogService(MockHttp(json));
        var entries = await svc.FetchCatalogAsync();

        Assert.Single(entries);
        Assert.Equal("TestPlugin", entries[0].Name);
        Assert.Equal("1.0", entries[0].Version);
        Assert.Equal("Dev", entries[0].Author);
        Assert.Equal(1024, entries[0].SizeBytes);
    }

    [Fact]
    public async Task FetchCatalog_NetworkError_ReturnsEmpty()
    {
        using var svc = new PluginCatalogService(MockHttp("", HttpStatusCode.InternalServerError));
        var entries = await svc.FetchCatalogAsync();
        Assert.Empty(entries);
    }

    [Fact]
    public async Task FetchCatalog_MalformedJson_ReturnsEmpty()
    {
        using var svc = new PluginCatalogService(MockHttp("not json"));
        var entries = await svc.FetchCatalogAsync();
        Assert.Empty(entries);
    }

    [Fact]
    public async Task FetchCatalog_EmptyArray_ReturnsEmpty()
    {
        using var svc = new PluginCatalogService(MockHttp("[]"));
        var entries = await svc.FetchCatalogAsync();
        Assert.Empty(entries);
    }

    [Fact]
    public async Task DownloadAndVerify_NullChecksum_ThrowsRefusingUnverified()
    {
        var content = "fake dll content"u8.ToArray();
        var handler = new MockDownloadHandler(content);
        using var svc = new PluginCatalogService(new HttpClient(handler));

        var entry = new PluginCatalogService.CatalogEntry(
            "Test", "1.0", "desc", "author",
            "https://example.com/test.dll", null, content.Length);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DownloadAndVerifyAsync(entry, _tempDir));
        Assert.Contains("no checksum", ex.Message);
    }

    [Fact]
    public async Task DownloadAndVerify_HttpUrl_ThrowsRequiringHttps()
    {
        var content = "fake dll content"u8.ToArray();
        var checksum = Convert.ToHexStringLower(SHA256.HashData(content));
        var handler = new MockDownloadHandler(content);
        using var svc = new PluginCatalogService(new HttpClient(handler));

        var entry = new PluginCatalogService.CatalogEntry(
            "Test", "1.0", "desc", "author",
            "http://example.com/test.dll", checksum, content.Length);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DownloadAndVerifyAsync(entry, _tempDir));
        Assert.Contains("HTTPS", ex.Message);
    }

    [Fact]
    public async Task DownloadAndVerify_ChecksumMatch_Succeeds()
    {
        var content = "valid plugin bytes"u8.ToArray();
        var checksum = Convert.ToHexStringLower(SHA256.HashData(content));
        var handler = new MockDownloadHandler(content);
        using var svc = new PluginCatalogService(new HttpClient(handler));

        var entry = new PluginCatalogService.CatalogEntry(
            "Test", "1.0", "desc", "author",
            "https://example.com/test.dll", checksum, content.Length);

        var path = await svc.DownloadAndVerifyAsync(entry, _tempDir);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DownloadAndVerify_ChecksumMismatch_Throws()
    {
        var content = "some bytes"u8.ToArray();
        var handler = new MockDownloadHandler(content);
        using var svc = new PluginCatalogService(new HttpClient(handler));

        var entry = new PluginCatalogService.CatalogEntry(
            "Test", "1.0", "desc", "author",
            "https://example.com/test.dll", "0000000000000000000000000000000000000000000000000000000000000000", content.Length);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DownloadAndVerifyAsync(entry, _tempDir));
    }

    [Fact]
    public async Task DownloadAndVerify_ReportsProgress()
    {
        var content = new byte[10000];
        Array.Fill<byte>(content, 0xDE);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(content));
        var handler = new MockDownloadHandler(content);
        using var svc = new PluginCatalogService(new HttpClient(handler));

        var entry = new PluginCatalogService.CatalogEntry(
            "Test", "1.0", "desc", "author",
            "https://example.com/test.dll", checksum, content.Length);

        // Use direct IProgress<T> to avoid Progress<T>'s async SynchronizationContext.Post
        var progressValues = new List<double>();
        var progress = new DirectProgress<double>(v => progressValues.Add(v));

        await svc.DownloadAndVerifyAsync(entry, _tempDir, progress);

        // Should have reported some progress
        Assert.True(progressValues.Count > 0);
    }

    /// <summary>Synchronous IProgress that invokes callback inline (no SynchronizationContext.Post).</summary>
    private sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    // ── Mock handlers ──

    private sealed class MockHandler : HttpMessageHandler
    {
        private readonly string _content;
        private readonly HttpStatusCode _status;

        public MockHandler(string content, HttpStatusCode status = HttpStatusCode.OK)
        {
            _content = content;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class MockDownloadHandler : HttpMessageHandler
    {
        private readonly byte[] _content;

        public MockDownloadHandler(byte[] content) => _content = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_content)
            };
            response.Content.Headers.ContentLength = _content.Length;
            return Task.FromResult(response);
        }
    }
}
