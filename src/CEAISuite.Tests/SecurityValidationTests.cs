using System.IO;
using System.Net;
using System.Net.Http;
using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Phase 10K security validation tests. Verify that PID mismatch guards,
/// catalog checksum enforcement, and CT Lua warnings work correctly.
/// </summary>
public class SecurityValidationTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    private static AiToolFunctions CreateToolsAttachedTo(int attachedPid)
    {
        var facade = new StubEngineFacade();
        facade.AttachAsync(attachedPid).Wait();
        var sessionRepo = new StubSessionRepository();
        var dashboard = new WorkspaceDashboardService(facade, sessionRepo);
        var scanEngine = new StubScanEngine();
        var scanService = new ScanService(scanEngine);
        var disasmEngine = new StubDisassemblyEngine();
        var disasmService = new DisassemblyService(disasmEngine);
        var scriptGen = new ScriptGenerationService();
        var addressTable = new AddressTableService(facade);
        var assembler = new StubAutoAssemblerEngine();

        return new AiToolFunctions(facade, dashboard, scanService, addressTable,
            disasmService, scriptGen, autoAssemblerEngine: assembler);
    }

    private static AiToolFunctions CreateToolsNotAttached()
    {
        var facade = new StubEngineFacade();
        var sessionRepo = new StubSessionRepository();
        var dashboard = new WorkspaceDashboardService(facade, sessionRepo);
        var scanEngine = new StubScanEngine();
        var scanService = new ScanService(scanEngine);
        var disasmEngine = new StubDisassemblyEngine();
        var disasmService = new DisassemblyService(disasmEngine);
        var scriptGen = new ScriptGenerationService();
        var addressTable = new AddressTableService(facade);
        var assembler = new StubAutoAssemblerEngine();

        return new AiToolFunctions(facade, dashboard, scanService, addressTable,
            disasmService, scriptGen, autoAssemblerEngine: assembler);
    }

    // ── 1. WriteMemory with wrong PID returns mismatch error ──

    [Fact]
    public async Task WriteMemory_WrongProcessId_ReturnsMismatchError()
    {
        var tools = CreateToolsAttachedTo(1000);

        var result = await tools.WriteMemory(2000, "0x400000", "Int32", "42");

        Assert.Contains("mismatch", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── 2. WriteMemory with correct PID does NOT return mismatch ──

    [Fact]
    public async Task WriteMemory_CorrectProcessId_DoesNotReturnMismatch()
    {
        var tools = CreateToolsAttachedTo(1000);

        var result = await tools.WriteMemory(1000, "0x400000", "Int32", "42");

        Assert.DoesNotContain("mismatch", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── 3. SetBreakpoint with wrong PID returns mismatch error ──

    [Fact]
    public async Task SetBreakpoint_WrongProcessId_ReturnsMismatchError()
    {
        var tools = CreateToolsAttachedTo(1000);

        var result = await tools.SetBreakpoint(2000, "0x400000", "Software", "LogAndContinue");

        Assert.Contains("mismatch", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── 4. ExecuteAutoAssemblerScript with wrong PID returns mismatch error ──

    [Fact]
    public async Task ExecuteAutoAssemblerScript_WrongProcessId_ReturnsMismatchError()
    {
        var tools = CreateToolsAttachedTo(1000);

        var result = await tools.ExecuteAutoAssemblerScript(2000, "[ENABLE]\n[DISABLE]");

        Assert.Contains("mismatch", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── 5. WriteMemory when not attached returns not-attached error ──

    [Fact]
    public async Task WriteMemory_NotAttached_ReturnsNotAttachedError()
    {
        var tools = CreateToolsNotAttached();

        var result = await tools.WriteMemory(1000, "0x400000", "Int32", "42");

        Assert.Contains("No process attached", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── 6. Catalog download with null checksum throws ──

    [Fact]
    public async Task CatalogDownload_NoChecksum_Throws()
    {
        var content = "fake dll"u8.ToArray();
        var handler = new MockDownloadHandler(content);
        using var svc = new PluginCatalogService(new HttpClient(handler));

        var entry = new PluginCatalogService.CatalogEntry(
            "Evil", "1.0", "desc", "attacker",
            "https://example.com/evil.dll", null, content.Length);

        var tempDir = Path.Combine(Path.GetTempPath(), "ceai-sec-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.DownloadAndVerifyAsync(entry, tempDir));
            Assert.Contains("no checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ── 7. Catalog download with empty checksum throws ──

    [Fact]
    public async Task CatalogDownload_EmptyChecksum_Throws()
    {
        var content = "fake dll"u8.ToArray();
        var handler = new MockDownloadHandler(content);
        using var svc = new PluginCatalogService(new HttpClient(handler));

        var entry = new PluginCatalogService.CatalogEntry(
            "Evil", "1.0", "desc", "attacker",
            "https://example.com/evil.dll", "", content.Length);

        var tempDir = Path.Combine(Path.GetTempPath(), "ceai-sec-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.DownloadAndVerifyAsync(entry, tempDir));
            Assert.Contains("no checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ── 8. LoadCheatTable with Lua contains warning ──

    [Fact]
    public async Task LoadCheatTable_WithLua_ContainsWarning()
    {
        var ctContent = """
            <?xml version="1.0" encoding="utf-8"?>
            <CheatTable>
              <CheatEntries/>
              <LuaScript>print("hello")</LuaScript>
            </CheatTable>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"test-lua-{Guid.NewGuid():N}.CT");
        _tempFiles.Add(path);
        File.WriteAllText(path, ctContent);

        var tools = CreateToolsAttachedTo(1000);
        var result = await tools.LoadCheatTable(path);

        Assert.Contains("WARNING", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Lua", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── 9. LoadCheatTable without Lua has no warning ──

    [Fact]
    public async Task LoadCheatTable_WithoutLua_NoWarning()
    {
        var ctContent = """
            <?xml version="1.0" encoding="utf-8"?>
            <CheatTable>
              <CheatEntries/>
            </CheatTable>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"test-nolua-{Guid.NewGuid():N}.CT");
        _tempFiles.Add(path);
        File.WriteAllText(path, ctContent);

        var tools = CreateToolsAttachedTo(1000);
        var result = await tools.LoadCheatTable(path);

        Assert.DoesNotContain("WARNING", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── 10. ReadMemory with wrong PID does NOT return mismatch (read-only tools skip PID guard) ──

    [Fact]
    public async Task ReadMemory_WrongProcessId_NoMismatchError()
    {
        var tools = CreateToolsAttachedTo(1000);

        var result = await tools.ReadMemory(2000, "0x400000", "Int32");

        Assert.DoesNotContain("mismatch", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── Mock handler for catalog tests ──

    private sealed class MockDownloadHandler : HttpMessageHandler
    {
        private readonly byte[] _content;
        public MockDownloadHandler(byte[] content) => _content = content;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
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
