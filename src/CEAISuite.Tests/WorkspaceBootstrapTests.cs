using CEAISuite.Application;

namespace CEAISuite.Tests;

public class WorkspaceBootstrapTests
{
    [Fact]
    public void CreateOverview_ReturnsNonNull()
    {
        var overview = WorkspaceBootstrap.CreateOverview();
        Assert.NotNull(overview);
    }

    [Fact]
    public void CreateOverview_HasExpectedProductName()
    {
        var overview = WorkspaceBootstrap.CreateOverview();
        Assert.Equal("CE AI Suite", overview.ProductName);
    }

    [Fact]
    public void CreateOverview_HasFiveLayers()
    {
        var overview = WorkspaceBootstrap.CreateOverview();
        Assert.Equal(5, overview.Layers.Count);
    }

    [Fact]
    public void CreateOverview_LayerNames_AreDistinct()
    {
        var overview = WorkspaceBootstrap.CreateOverview();
        var names = overview.Layers.Select(l => l.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void CreateOverview_HasFiveMilestones()
    {
        var overview = WorkspaceBootstrap.CreateOverview();
        Assert.Equal(5, overview.Milestones.Count);
    }

    [Fact]
    public void CreateOverview_ToolingNotEmpty()
    {
        var overview = WorkspaceBootstrap.CreateOverview();
        Assert.NotEmpty(overview.Tooling);
    }

    [Fact]
    public void CreateOverview_EngineCapabilitiesNotEmpty()
    {
        var overview = WorkspaceBootstrap.CreateOverview();
        Assert.NotEmpty(overview.EngineCapabilities);
    }

    [Fact]
    public void CreateOverview_DefaultProfile_IsWindowsX64()
    {
        var overview = WorkspaceBootstrap.CreateOverview();
        Assert.Contains("windows", overview.DefaultProfile.Id, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x64", overview.DefaultProfile.TargetPlatform, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateOverview_ToolingMatchesToolCatalog()
    {
        var overview = WorkspaceBootstrap.CreateOverview();
        Assert.Equal(
            CEAISuite.AI.Contracts.ToolCatalog.RequiredTools.Count,
            overview.Tooling.Count);
    }
}
