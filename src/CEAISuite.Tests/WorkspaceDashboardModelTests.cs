using CEAISuite.Application;

namespace CEAISuite.Tests;

public class WorkspaceDashboardModelTests
{
    [Fact]
    public void CreateLoading_ReturnsNonNull()
    {
        var dashboard = WorkspaceDashboard.CreateLoading();
        Assert.NotNull(dashboard);
    }

    [Fact]
    public void CreateLoading_StatusMessage_IsLoading()
    {
        var dashboard = WorkspaceDashboard.CreateLoading();
        Assert.Contains("Loading", dashboard.StatusMessage);
    }

    [Fact]
    public void CreateLoading_HasEmptyCollections()
    {
        var dashboard = WorkspaceDashboard.CreateLoading();
        Assert.Empty(dashboard.RunningProcesses);
        Assert.Empty(dashboard.RecentSessions);
    }

    [Fact]
    public void CreateLoading_NullOptionalFields()
    {
        var dashboard = WorkspaceDashboard.CreateLoading();
        Assert.Null(dashboard.CurrentInspection);
        Assert.Null(dashboard.ScanResults);
        Assert.Null(dashboard.ScanStatus);
        Assert.Null(dashboard.Disassembly);
        Assert.Null(dashboard.AiChatHistory);
        Assert.Null(dashboard.BreakpointStatus);
    }

    [Fact]
    public void ProductName_DelegatesToOverview()
    {
        var dashboard = WorkspaceDashboard.CreateLoading();
        Assert.Equal(dashboard.Overview.ProductName, dashboard.ProductName);
    }

    [Fact]
    public void Summary_DelegatesToOverview()
    {
        var dashboard = WorkspaceDashboard.CreateLoading();
        Assert.Equal(dashboard.Overview.Summary, dashboard.Summary);
    }

    [Fact]
    public void Layers_DelegatesToOverview()
    {
        var dashboard = WorkspaceDashboard.CreateLoading();
        Assert.Same(dashboard.Overview.Layers, dashboard.Layers);
    }

    [Fact]
    public void Milestones_DelegatesToOverview()
    {
        var dashboard = WorkspaceDashboard.CreateLoading();
        Assert.Same(dashboard.Overview.Milestones, dashboard.Milestones);
    }

    [Fact]
    public void Tooling_DelegatesToOverview()
    {
        var dashboard = WorkspaceDashboard.CreateLoading();
        Assert.Same(dashboard.Overview.Tooling, dashboard.Tooling);
    }

    [Fact]
    public void EngineCapabilities_DelegatesToOverview()
    {
        var dashboard = WorkspaceDashboard.CreateLoading();
        Assert.Same(dashboard.Overview.EngineCapabilities, dashboard.EngineCapabilities);
    }

    [Fact]
    public void DefaultProfile_DelegatesToOverview()
    {
        var dashboard = WorkspaceDashboard.CreateLoading();
        Assert.Equal(dashboard.Overview.DefaultProfile, dashboard.DefaultProfile);
    }

    [Fact]
    public void AiConfigured_DefaultsFalse()
    {
        var dashboard = WorkspaceDashboard.CreateLoading();
        Assert.False(dashboard.AiConfigured);
    }
}
