using System.IO;
using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;
using Microsoft.Extensions.AI;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for <see cref="PluginHost"/>: discovery, loading from empty/nonexistent
/// directories, status summary, tool collection, and disposal.
/// Does NOT test real DLL loading (requires compiled plugin assemblies).
/// </summary>
public class PluginSystemTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private PluginHost? _host;

    public PluginSystemTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ceai-plugin-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask InitializeAsync() => default;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private PluginContext MakeContext() => new()
    {
        Log = (level, msg) => { },
        Limits = TokenLimits.Balanced,
        ResultStore = new ToolResultStore(),
        StorageDirectory = _tempDir,
    };

    // ── Empty directory ─────────────────────────────────────────────

    [Fact]
    public async Task LoadAllAsync_EmptyDirectory_ReturnsZeroTools()
    {
        _host = new PluginHost(_tempDir);
        var toolCount = await _host.LoadAllAsync(MakeContext());

        Assert.Equal(0, toolCount);
        Assert.Empty(_host.Plugins);
    }

    // ── Nonexistent directory ───────────────────────────────────────

    [Fact]
    public async Task LoadAllAsync_NonexistentDirectory_ReturnsZeroTools()
    {
        var nonexistent = Path.Combine(_tempDir, "does-not-exist");
        _host = new PluginHost(nonexistent);
        var toolCount = await _host.LoadAllAsync(MakeContext());

        Assert.Equal(0, toolCount);
        Assert.Empty(_host.Plugins);
    }

    // ── Invalid DLL files ───────────────────────────────────────────

    [Fact]
    public async Task LoadAllAsync_InvalidDll_SkipsGracefully()
    {
        // Create a file that's not a valid .NET assembly
        File.WriteAllText(Path.Combine(_tempDir, "fake-plugin.dll"), "not a real dll");

        _host = new PluginHost(_tempDir);
        var toolCount = await _host.LoadAllAsync(MakeContext());

        Assert.Equal(0, toolCount);
        Assert.Empty(_host.Plugins);
    }

    // ── GetAllTools / GetAllSkills with no plugins ──────────────────

    [Fact]
    public async Task GetAllTools_NoPlugins_ReturnsEmpty()
    {
        _host = new PluginHost(_tempDir);
        await _host.LoadAllAsync(MakeContext());

        var tools = _host.GetAllTools();
        Assert.Empty(tools);
    }

    [Fact]
    public async Task GetAllSkills_NoPlugins_ReturnsEmpty()
    {
        _host = new PluginHost(_tempDir);
        await _host.LoadAllAsync(MakeContext());

        var skills = _host.GetAllSkills();
        Assert.Empty(skills);
    }

    // ── Status summary ──────────────────────────────────────────────

    [Fact]
    public async Task GetStatusSummary_NoPlugins_ReportsNone()
    {
        _host = new PluginHost(_tempDir);
        await _host.LoadAllAsync(MakeContext());

        var summary = _host.GetStatusSummary();
        Assert.Contains("No plugins loaded", summary);
    }

    // ── UnloadPluginAsync with no matching plugin ───────────────────

    [Fact]
    public async Task UnloadPluginAsync_NonexistentName_NoError()
    {
        _host = new PluginHost(_tempDir);
        await _host.LoadAllAsync(MakeContext());

        // Should not throw
        await _host.UnloadPluginAsync("nonexistent-plugin");
    }

    // ── DisposeAsync ────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_EmptyHost_NoError()
    {
        var host = new PluginHost(_tempDir);
        await host.LoadAllAsync(MakeContext());

        // Should not throw
        await host.DisposeAsync();
    }

    // ── ICeaiPlugin interface contract ──────────────────────────────

    [Fact]
    public async Task StubPlugin_ImplementsInterface_Correctly()
    {
        var plugin = new StubCeaiPlugin();

        Assert.Equal("test-plugin", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal("A stub plugin for testing", plugin.Description);
        Assert.Empty(plugin.GetTools());
        Assert.Null(((ICeaiPlugin)plugin).GetSkills());

        await plugin.InitializeAsync(MakeContext(), CancellationToken.None);
        Assert.True(plugin.Initialized);

        await plugin.ShutdownAsync();
        Assert.True(plugin.ShutDown);
    }

    // ── PluginContext ────────────────────────────────────────────────

    [Fact]
    public void PluginContext_RequiredFields_SetCorrectly()
    {
        var ctx = MakeContext();
        Assert.NotNull(ctx.Log);
        Assert.NotNull(ctx.Limits);
        Assert.NotNull(ctx.ResultStore);
        Assert.Equal(_tempDir, ctx.StorageDirectory);
    }

    // ── Non-DLL files ignored ───────────────────────────────────────

    [Fact]
    public async Task LoadAllAsync_NonDllFiles_Ignored()
    {
        File.WriteAllText(Path.Combine(_tempDir, "readme.txt"), "not a plugin");
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), "{}");

        _host = new PluginHost(_tempDir);
        var toolCount = await _host.LoadAllAsync(MakeContext());
        Assert.Equal(0, toolCount);
    }

    // ── Stub plugin for interface testing ────────────────────────────

    private sealed class StubCeaiPlugin : ICeaiPlugin
    {
        public string Name => "test-plugin";
        public string Version => "1.0.0";
        public string Description => "A stub plugin for testing";
        public bool Initialized { get; private set; }
        public bool ShutDown { get; private set; }

        public IReadOnlyList<AIFunction> GetTools() => [];

        public Task InitializeAsync(PluginContext context, CancellationToken ct)
        {
            Initialized = true;
            return Task.CompletedTask;
        }

        public Task ShutdownAsync()
        {
            ShutDown = true;
            return Task.CompletedTask;
        }
    }
}
