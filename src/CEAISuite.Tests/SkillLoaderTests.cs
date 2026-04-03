using System.IO;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for SkillLoader — subdirectory discovery, source tracking, bundled flag.
/// </summary>
public class SkillLoaderTests
{
    private readonly string _tempDir;

    public SkillLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ceai-skill-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void LoadFromDirectory_SubdirectorySkillMd_Discovered()
    {
        var skillDir = Path.Combine(_tempDir, "test-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: test-skill
            description: A test skill
            ---
            Instructions here.
            """);

        var skills = SkillLoader.LoadFromDirectory(_tempDir);
        Assert.Single(skills);
        Assert.Equal("test-skill", skills[0].Name);
        Assert.Contains("Instructions here", skills[0].Instructions);
    }

    [Fact]
    public void LoadFromDirectory_SourceDirectory_Tracked()
    {
        var skillDir = Path.Combine(_tempDir, "tracked");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: tracked
            description: Source tracking test
            ---
            Body.
            """);

        var skills = SkillLoader.LoadFromDirectory(_tempDir);
        Assert.Single(skills);
        Assert.Equal(skillDir, skills[0].SourceDirectory);
    }

    [Fact]
    public void LoadFromDirectory_IsBundled_FlowsThrough()
    {
        var skillDir = Path.Combine(_tempDir, "bundled-test");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: bundled-test
            description: Bundled test
            ---
            Body.
            """);

        var bundled = SkillLoader.LoadFromDirectory(_tempDir, isBundled: true);
        Assert.True(bundled[0].IsBundled);

        var user = SkillLoader.LoadFromDirectory(_tempDir, isBundled: false);
        Assert.False(user[0].IsBundled);
    }

    [Fact]
    public void LoadFromDirectory_TopLevelFiles_StillWork()
    {
        File.WriteAllText(Path.Combine(_tempDir, "simple.md"), """
            ---
            name: simple
            description: Top-level skill
            ---
            Simple instructions.
            """);

        var skills = SkillLoader.LoadFromDirectory(_tempDir);
        Assert.Single(skills);
        Assert.Equal("simple", skills[0].Name);
    }

    [Fact]
    public void LoadFromDirectory_MixedSubdirAndTopLevel_LoadsBoth()
    {
        var subDir = Path.Combine(_tempDir, "sub-skill");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "SKILL.md"), """
            ---
            name: sub-skill
            description: From subdir
            ---
            Sub instructions.
            """);

        File.WriteAllText(Path.Combine(_tempDir, "top.md"), """
            ---
            name: top-skill
            description: From top level
            ---
            Top instructions.
            """);

        var skills = SkillLoader.LoadFromDirectory(_tempDir);
        Assert.Equal(2, skills.Count);
        Assert.Contains(skills, s => s.Name == "sub-skill");
        Assert.Contains(skills, s => s.Name == "top-skill");
    }

    [Fact]
    public void LoadFromDirectory_EmptyDirectory_ReturnsEmpty()
    {
        var skills = SkillLoader.LoadFromDirectory(_tempDir);
        Assert.Empty(skills);
    }

    [Fact]
    public void LoadFromDirectory_NonexistentDirectory_ReturnsEmpty()
    {
        var skills = SkillLoader.LoadFromDirectory(Path.Combine(_tempDir, "does-not-exist"));
        Assert.Empty(skills);
    }
}
