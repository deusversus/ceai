using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for Phase 12A process information features:
/// extended records, dashboard wiring, and AI tool output.
/// </summary>
public class ProcessInfoTests
{
    // ──────────────────────────────────────────────────────────
    // Record construction & default values
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void ProcessDescriptor_NewFields_HaveDefaults()
    {
        var desc = new ProcessDescriptor(42, "game.exe", "x64");
        Assert.Null(desc.ParentProcessId);
        Assert.Null(desc.ExecutablePath);
        Assert.Null(desc.CommandLine);
        Assert.Null(desc.WindowTitle);
        Assert.False(desc.IsElevated);
    }

    [Fact]
    public void ProcessDescriptor_NewFields_CanBeSet()
    {
        var desc = new ProcessDescriptor(42, "game.exe", "x64",
            ParentProcessId: 10,
            ExecutablePath: @"C:\Games\game.exe",
            CommandLine: @"C:\Games\game.exe --windowed",
            WindowTitle: "My Game",
            IsElevated: true);
        Assert.Equal(10, desc.ParentProcessId);
        Assert.Equal(@"C:\Games\game.exe", desc.ExecutablePath);
        Assert.Equal(@"C:\Games\game.exe --windowed", desc.CommandLine);
        Assert.Equal("My Game", desc.WindowTitle);
        Assert.True(desc.IsElevated);
    }

    [Fact]
    public void ModuleDescriptor_FullPath_DefaultNull()
    {
        var mod = new ModuleDescriptor("kernel32.dll", 0x7FF80000, 65536);
        Assert.Null(mod.FullPath);
    }

    [Fact]
    public void ModuleDescriptor_FullPath_CanBeSet()
    {
        var mod = new ModuleDescriptor("kernel32.dll", 0x7FF80000, 65536,
            @"C:\Windows\System32\kernel32.dll");
        Assert.Equal(@"C:\Windows\System32\kernel32.dll", mod.FullPath);
    }

    [Fact]
    public void EngineAttachment_NewFields_HaveDefaults()
    {
        var modules = new[] { new ModuleDescriptor("main.exe", 0x400000, 4096) };
        var attach = new EngineAttachment(42, "game.exe", modules);
        Assert.Null(attach.Architecture);
        Assert.Null(attach.ParentProcessId);
        Assert.Null(attach.CommandLine);
        Assert.Null(attach.ExecutablePath);
        Assert.False(attach.IsElevated);
    }

    [Fact]
    public void EngineAttachment_NewFields_CanBeSet()
    {
        var modules = new[] { new ModuleDescriptor("main.exe", 0x400000, 4096) };
        var attach = new EngineAttachment(42, "game.exe", modules,
            Architecture: "x64",
            ParentProcessId: 10,
            CommandLine: @"game.exe --debug",
            ExecutablePath: @"C:\Games\game.exe",
            IsElevated: true);
        Assert.Equal("x64", attach.Architecture);
        Assert.Equal(10, attach.ParentProcessId);
        Assert.Equal(@"game.exe --debug", attach.CommandLine);
        Assert.Equal(@"C:\Games\game.exe", attach.ExecutablePath);
        Assert.True(attach.IsElevated);
    }

    // ──────────────────────────────────────────────────────────
    // Application layer — RunningProcessOverview, ModuleOverview, ProcessInspectionOverview
    // ──────────────────────────────────────────────────────────

    [Fact]
    public void RunningProcessOverview_NewFields_HaveDefaults()
    {
        var rpo = new RunningProcessOverview(42, "game.exe", "x64");
        Assert.Null(rpo.ExecutablePath);
        Assert.Null(rpo.WindowTitle);
        Assert.False(rpo.IsElevated);
    }

    [Fact]
    public void ModuleOverview_FullPath_DefaultNull()
    {
        var mo = new ModuleOverview("kernel32.dll", "0x7FF80000", "65,536 bytes");
        Assert.Null(mo.FullPath);
    }

    [Fact]
    public void ProcessInspectionOverview_NewFields_HaveDefaults()
    {
        var pio = new ProcessInspectionOverview(
            42, "game.exe", "x64",
            Array.Empty<ModuleOverview>(),
            null, null, null, "OK");
        Assert.Null(pio.ParentProcessId);
        Assert.Null(pio.ParentProcessName);
        Assert.Null(pio.CommandLine);
        Assert.Null(pio.ExecutablePath);
        Assert.Null(pio.WindowTitle);
        Assert.False(pio.IsElevated);
    }

    [Fact]
    public void ProcessInspectionOverview_NewFields_CanBeSet()
    {
        var pio = new ProcessInspectionOverview(
            42, "game.exe", "x64",
            Array.Empty<ModuleOverview>(),
            null, null, null, "OK",
            ParentProcessId: 10,
            ParentProcessName: "explorer.exe",
            CommandLine: @"game.exe --debug",
            ExecutablePath: @"C:\Games\game.exe",
            WindowTitle: "My Game",
            IsElevated: true);
        Assert.Equal(10, pio.ParentProcessId);
        Assert.Equal("explorer.exe", pio.ParentProcessName);
        Assert.Equal(@"game.exe --debug", pio.CommandLine);
        Assert.Equal(@"C:\Games\game.exe", pio.ExecutablePath);
        Assert.Equal("My Game", pio.WindowTitle);
        Assert.True(pio.IsElevated);
    }

    // ──────────────────────────────────────────────────────────
    // Dashboard service wiring (via stub facade)
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task InspectProcessAsync_WiresNewFields()
    {
        var facade = new StubEngineFacade();
        var service = new WorkspaceDashboardService(facade, new StubSessionRepository());

        // StubEngineFacade returns default-valued records, so new fields will be null/false.
        // This verifies the wiring compiles and runs without throwing.
        var overview = await service.InspectProcessAsync(1000);

        Assert.Equal(1000, overview.ProcessId);
        Assert.Equal("TestGame.exe", overview.ProcessName);
        Assert.NotEmpty(overview.Modules);
    }

    [Fact]
    public async Task BuildAsync_WiresProcessOverviewFields()
    {
        var facade = new StubEngineFacade();
        var repo = new StubSessionRepository();
        var service = new WorkspaceDashboardService(facade, repo);

        var dashboard = await service.BuildAsync(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test.db"));

        // StubEngineFacade.ListProcessesAsync returns 3 processes with default new fields
        Assert.Equal(3, dashboard.RunningProcesses.Count);
        foreach (var proc in dashboard.RunningProcesses)
        {
            // Stub returns defaults, so these should be null/false
            Assert.Null(proc.ExecutablePath);
            Assert.Null(proc.WindowTitle);
            Assert.False(proc.IsElevated);
        }
    }
}
