using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for skill catalog budget — description truncation and names-only fallback.
/// </summary>
public class SkillCatalogBudgetTests
{
    [Fact]
    public void UnderBudget_FullDescriptions()
    {
        var system = new SkillSystem { MaxCatalogChars = 5000 };
        system.Register(new SkillDefinition
        {
            Name = "small-skill",
            Description = "A short description.",
            Instructions = "test",
        });

        var output = system.BuildCatalogSummary();
        Assert.Contains("A short description.", output);
    }

    [Fact]
    public void OverBudget_NonBundled_Truncated()
    {
        var system = new SkillSystem { MaxCatalogChars = 200 };
        var longDesc = new string('A', 500);
        system.Register(new SkillDefinition
        {
            Name = "verbose-skill",
            Description = longDesc,
            Instructions = "test",
            IsBundled = false,
        });

        var output = system.BuildCatalogSummary();
        // Description should be truncated (not the full 500 chars)
        Assert.DoesNotContain(longDesc, output);
        Assert.Contains("verbose-skill", output);
    }

    [Fact]
    public void BundledSkills_NeverTruncatedInNamesOnly()
    {
        var system = new SkillSystem { MaxCatalogChars = 100 }; // Very tight budget

        system.Register(new SkillDefinition
        {
            Name = "bundled",
            Description = "This bundled description must always appear in full.",
            Instructions = "test",
            IsBundled = true,
        });

        // Add several non-bundled to force names-only mode
        for (int i = 0; i < 10; i++)
        {
            system.Register(new SkillDefinition
            {
                Name = $"user-skill-{i}",
                Description = new string('X', 300),
                Instructions = "test",
                IsBundled = false,
            });
        }

        var output = system.BuildCatalogSummary();
        // Bundled description should always be present
        Assert.Contains("This bundled description must always appear in full.", output);
    }

    [Fact]
    public void CategoryGrouping_SkillsGroupedByCategory()
    {
        var system = new SkillSystem { MaxCatalogChars = 5000 };
        system.Register(new SkillDefinition
        {
            Name = "scan-skill",
            Description = "Scanning",
            Instructions = "test",
            Category = "Scanning",
        });
        system.Register(new SkillDefinition
        {
            Name = "debug-skill",
            Description = "Debugging",
            Instructions = "test",
            Category = "Debugging",
        });

        var output = system.BuildCatalogSummary();
        Assert.Contains("[Scanning]", output);
        Assert.Contains("[Debugging]", output);
    }

    [Fact]
    public void UserFacing_HidesNonInvocableSkills()
    {
        var system = new SkillSystem();
        system.Register(new SkillDefinition
        {
            Name = "visible",
            Description = "Visible to users",
            Instructions = "test",
            UserInvocable = true,
        });
        system.Register(new SkillDefinition
        {
            Name = "hidden",
            Description = "Hidden from users",
            Instructions = "test",
            UserInvocable = false,
        });

        var userOutput = system.BuildCatalogSummary(userFacing: true);
        Assert.Contains("visible", userOutput);
        Assert.DoesNotContain("hidden", userOutput);

        var modelOutput = system.BuildCatalogSummary(userFacing: false);
        Assert.Contains("visible", modelOutput);
        Assert.Contains("hidden", modelOutput);
    }

    [Fact]
    public void VersionDisplayed_InCatalog()
    {
        var system = new SkillSystem { MaxCatalogChars = 5000 };
        system.Register(new SkillDefinition
        {
            Name = "versioned",
            Description = "Has a version",
            Instructions = "test",
            Version = "2.1",
        });

        var output = system.BuildCatalogSummary();
        Assert.Contains("v2.1", output);
    }
}
