using System.IO;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for skill reference file loading — SKILL_DIR substitution, path traversal prevention,
/// auto-inclusion, and the ReadSkillReference API.
/// </summary>
public class SkillReferenceTests
{
    private readonly string _tempDir;

    public SkillReferenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ceai-ref-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void SkillDir_Substitution_ReplacesVariable()
    {
        var skillDir = Path.Combine(_tempDir, "my-skill");
        Directory.CreateDirectory(skillDir);

        var system = new SkillSystem();
        system.Register(new SkillDefinition
        {
            Name = "my-skill",
            Description = "test",
            Instructions = "See ${SKILL_DIR}/references/guide.md for details.",
            SourceDirectory = skillDir,
        });
        system.LoadSkill("my-skill");

        var output = system.BuildActiveSkillInstructions();
        Assert.NotNull(output);
        Assert.DoesNotContain("${SKILL_DIR}", output);
        Assert.Contains(skillDir.Replace('\\', '/'), output);
    }

    [Fact]
    public void ReadSkillReference_ActiveSkill_ReturnsContent()
    {
        var skillDir = Path.Combine(_tempDir, "ref-skill");
        var refsDir = Path.Combine(skillDir, "references");
        Directory.CreateDirectory(refsDir);
        File.WriteAllText(Path.Combine(refsDir, "guide.md"), "# Guide\nStep 1: Do the thing.");

        var system = new SkillSystem();
        system.Register(new SkillDefinition
        {
            Name = "ref-skill",
            Description = "test",
            Instructions = "test",
            SourceDirectory = skillDir,
        });
        system.LoadSkill("ref-skill");

        var content = system.ReadSkillReference("ref-skill", "guide.md");
        Assert.Contains("Step 1: Do the thing", content);
    }

    [Fact]
    public void ReadSkillReference_PathTraversal_Blocked()
    {
        var skillDir = Path.Combine(_tempDir, "traversal-skill");
        var refsDir = Path.Combine(skillDir, "references");
        Directory.CreateDirectory(refsDir);

        var system = new SkillSystem();
        system.Register(new SkillDefinition
        {
            Name = "traversal-skill",
            Description = "test",
            Instructions = "test",
            SourceDirectory = skillDir,
        });
        system.LoadSkill("traversal-skill");

        var result = system.ReadSkillReference("traversal-skill", "../../etc/passwd");
        Assert.Contains("access denied", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadSkillReference_UnloadedSkill_ReturnsError()
    {
        var system = new SkillSystem();
        system.Register(new SkillDefinition
        {
            Name = "inactive",
            Description = "test",
            Instructions = "test",
            SourceDirectory = _tempDir,
        });

        var result = system.ReadSkillReference("inactive", "file.md");
        Assert.Contains("not loaded", result);
    }

    [Fact]
    public void ReadSkillReference_MissingFile_ListsAvailable()
    {
        var skillDir = Path.Combine(_tempDir, "avail-skill");
        var refsDir = Path.Combine(skillDir, "references");
        Directory.CreateDirectory(refsDir);
        File.WriteAllText(Path.Combine(refsDir, "exists.md"), "content");

        var system = new SkillSystem();
        system.Register(new SkillDefinition
        {
            Name = "avail-skill",
            Description = "test",
            Instructions = "test",
            SourceDirectory = skillDir,
        });
        system.LoadSkill("avail-skill");

        var result = system.ReadSkillReference("avail-skill", "missing.md");
        Assert.Contains("exists.md", result);
    }

    [Fact]
    public void AutoInclude_SmallReferenceFiles_IncludedInInstructions()
    {
        var skillDir = Path.Combine(_tempDir, "auto-include");
        var refsDir = Path.Combine(skillDir, "references");
        Directory.CreateDirectory(refsDir);
        File.WriteAllText(Path.Combine(refsDir, "small.md"), "Small reference content.");

        var system = new SkillSystem();
        system.Register(new SkillDefinition
        {
            Name = "auto-include",
            Description = "test",
            Instructions = "Main instructions.",
            SourceDirectory = skillDir,
        });
        system.LoadSkill("auto-include");

        var output = system.BuildActiveSkillInstructions();
        Assert.NotNull(output);
        Assert.Contains("Main instructions", output);
        Assert.Contains("Small reference content", output);
        Assert.Contains("Reference: small.md", output);
    }
}
