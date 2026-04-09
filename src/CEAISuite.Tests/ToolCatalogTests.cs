using CEAISuite.AI.Contracts;

namespace CEAISuite.Tests;

public class ToolCatalogTests
{
    [Fact]
    public void RequiredTools_IsNotEmpty()
    {
        Assert.NotEmpty(ToolCatalog.RequiredTools);
    }

    [Fact]
    public void RequiredTools_HasExpectedCount()
    {
        Assert.Equal(14, ToolCatalog.RequiredTools.Count);
    }

    [Fact]
    public void RequiredTools_AllHaveNonEmptyName()
    {
        foreach (var tool in ToolCatalog.RequiredTools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Name), $"Tool has empty name: {tool}");
        }
    }

    [Fact]
    public void RequiredTools_AllHaveNonEmptyCategory()
    {
        foreach (var tool in ToolCatalog.RequiredTools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Category), $"Tool '{tool.Name}' has empty category");
        }
    }

    [Fact]
    public void RequiredTools_AllHaveNonEmptyPurpose()
    {
        foreach (var tool in ToolCatalog.RequiredTools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Purpose), $"Tool '{tool.Name}' has empty purpose");
        }
    }

    [Fact]
    public void RequiredTools_NamesAreUnique()
    {
        var names = ToolCatalog.RequiredTools.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void RequiredTools_ContainsExpectedCoreTools()
    {
        var names = ToolCatalog.RequiredTools.Select(t => t.Name).ToHashSet();
        Assert.Contains("list_processes", names);
        Assert.Contains("read_memory", names);
        Assert.Contains("write_memory", names);
        Assert.Contains("disassemble", names);
        Assert.Contains("start_scan", names);
    }

    [Fact]
    public void RequiredTools_AllHaveValidRisk()
    {
        foreach (var tool in ToolCatalog.RequiredTools)
        {
            Assert.True(Enum.IsDefined(tool.Risk), $"Tool '{tool.Name}' has undefined risk: {tool.Risk}");
        }
    }

    [Fact]
    public void RequiredTools_HasHighRiskTools()
    {
        Assert.Contains(ToolCatalog.RequiredTools, t => t.Risk == ToolRisk.HighRisk);
    }

    [Fact]
    public void RequiredTools_HasReadOnlyTools()
    {
        Assert.Contains(ToolCatalog.RequiredTools, t => t.Risk == ToolRisk.ReadOnly);
    }
}
