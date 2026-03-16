using CEAISuite.AI.Contracts;
using CEAISuite.Domain;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Application;

public sealed record LayerOverview(string Name, string Project, string Responsibility);

public sealed record ToolOverview(string Name, string Category, string Risk);

public sealed record WorkspaceOverview(
    string ProductName,
    string Summary,
    IReadOnlyList<LayerOverview> Layers,
    IReadOnlyList<string> Milestones,
    IReadOnlyList<ToolOverview> Tooling,
    IReadOnlyList<EngineCapability> EngineCapabilities,
    ProjectProfile DefaultProfile);

public static class WorkspaceBootstrap
{
    public static WorkspaceOverview CreateOverview()
    {
        var layers = new[]
        {
            new LayerOverview("Domain model", "CEAISuite.Domain", "Session, scan, address, patch, and audit entities."),
            new LayerOverview("Engine boundary", "CEAISuite.Engine.Abstractions", "Contracts for process attach, memory access, and scan/debugger services."),
            new LayerOverview("AI tool contract", "CEAISuite.AI.Contracts", "Explicit AI-facing tools with risk classification."),
            new LayerOverview("Application layer", "CEAISuite.Application", "Composition root for desktop workflows and future orchestration."),
            new LayerOverview("Desktop shell", "CEAISuite.Desktop", "Windows-first WPF shell for scans, debugger, and AI operator.")
        };

        var milestones = new[]
        {
            "Milestone 1: core foundation and session model",
            "Milestone 2: scanner flow and result management",
            "Milestone 3: debugger, disassembly, and trace logging",
            "Milestone 4: AI operator, approvals, and action log",
            "Milestone 5: reusable scripts, table artifacts, and persistence"
        };

        var tooling = ToolCatalog.RequiredTools
            .Select(
                tool => new ToolOverview(
                    tool.Name,
                    tool.Category,
                    tool.Risk switch
                    {
                        ToolRisk.ReadOnly => "Read-only",
                        ToolRisk.WritesRequireConfirmation => "Confirmation required",
                        ToolRisk.HighRisk => "High risk",
                        _ => tool.Risk.ToString()
                    }))
            .ToArray();

        var defaultProfile = new ProjectProfile(
            "windows-x64-local",
            "Windows x64 local analysis",
            "game.exe",
            "Windows x64",
            new[] { "main module", "engine DLLs" });

        return new WorkspaceOverview(
            "CE AI Suite",
            "Cheat Engine-class analysis workspace with strict engine and AI boundaries.",
            layers,
            milestones,
            tooling,
            new[]
            {
                EngineCapability.ProcessEnumeration,
                EngineCapability.MemoryRead,
                EngineCapability.MemoryWrite,
                EngineCapability.ValueScanning,
                EngineCapability.Disassembly,
                EngineCapability.BreakpointTracing,
                EngineCapability.SessionPersistence
            },
            defaultProfile);
    }
}
