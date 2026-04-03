using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for SkillLoader.ParseFrontmatter — all fields, folded scalars, YAML lists, defaults.
/// </summary>
public class SkillFrontmatterTests
{
    private static SkillFrontmatter Parse(string yaml)
    {
        var lines = ("---\n" + yaml + "\n---").Split('\n');
        return SkillLoader.ParseFrontmatter(lines, 1, lines.Length - 1);
    }

    [Fact]
    public void ParseFrontmatter_BasicFields_Parsed()
    {
        var fm = Parse("name: test\ndescription: A test skill\nversion: \"1.0\"\nauthor: \"Me\"\ncategory: core");
        Assert.Equal("test", fm.Name);
        Assert.Equal("A test skill", fm.Description);
        Assert.Equal("1.0", fm.Version);
        Assert.Equal("Me", fm.Author);
        Assert.Equal("core", fm.Category);
    }

    [Fact]
    public void ParseFrontmatter_FoldedScalar_JoinsContinuationLines()
    {
        var fm = Parse("name: folded\ndescription: >\n  First line of description\n  second line continues here.\nversion: \"2.0\"");
        Assert.Equal("folded", fm.Name);
        Assert.Contains("First line of description", fm.Description);
        Assert.Contains("second line continues here", fm.Description);
        Assert.Equal("2.0", fm.Version);
    }

    [Fact]
    public void ParseFrontmatter_YamlLists_Parsed()
    {
        var fm = Parse("name: lists\ndescription: test\ntags:\n  - alpha\n  - beta\ntriggers:\n  - scan\n  - find value");
        Assert.Equal(2, fm.Tags.Count);
        Assert.Contains("alpha", fm.Tags);
        Assert.Contains("beta", fm.Tags);
        Assert.Equal(2, fm.Triggers.Count);
        Assert.Contains("scan", fm.Triggers);
        Assert.Contains("find value", fm.Triggers);
    }

    [Fact]
    public void ParseFrontmatter_AllowedTools_YamlList()
    {
        var fm = Parse("name: tools\ndescription: test\nallowed-tools:\n  - WriteMemory\n  - SetBreakpoint");
        Assert.Equal(2, fm.AllowedTools.Count);
        Assert.Contains("WriteMemory", fm.AllowedTools);
        Assert.Contains("SetBreakpoint", fm.AllowedTools);
    }

    [Fact]
    public void ParseFrontmatter_AllowedTools_InlineCommaSeparated()
    {
        var fm = Parse("name: tools\ndescription: test\nallowed-tools: WriteMemory, SetBreakpoint");
        Assert.Equal(2, fm.AllowedTools.Count);
        Assert.Contains("WriteMemory", fm.AllowedTools);
        Assert.Contains("SetBreakpoint", fm.AllowedTools);
    }

    [Fact]
    public void ParseFrontmatter_UserInvocable_DefaultTrue()
    {
        var fm = Parse("name: invocable\ndescription: test");
        Assert.True(fm.UserInvocable);
    }

    [Fact]
    public void ParseFrontmatter_UserInvocable_ExplicitFalse()
    {
        var fm = Parse("name: hidden\ndescription: test\nuser-invocable: false");
        Assert.False(fm.UserInvocable);
    }

    [Fact]
    public void ParseFrontmatter_Context_Fork()
    {
        var fm = Parse("name: forked\ndescription: test\ncontext: fork");
        Assert.Equal(SkillContext.Fork, fm.Context);
    }

    [Fact]
    public void ParseFrontmatter_Context_DefaultInline()
    {
        var fm = Parse("name: inline\ndescription: test");
        Assert.Equal(SkillContext.Inline, fm.Context);
    }

    [Fact]
    public void ParseFrontmatter_UnknownKeys_Ignored()
    {
        var fm = Parse("name: test\ndescription: test\nunknown-key: some value\nanother: thing");
        Assert.Equal("test", fm.Name);
        Assert.Equal("test", fm.Description);
    }

    [Fact]
    public void ParseFrontmatter_Dependencies_Parsed()
    {
        var fm = Parse("name: dependent\ndescription: test\ndependencies:\n  - base-ops\n  - memory-scanning");
        Assert.Equal(2, fm.Dependencies.Count);
        Assert.Contains("base-ops", fm.Dependencies);
        Assert.Contains("memory-scanning", fm.Dependencies);
    }

    [Fact]
    public void SplitFrontmatterLines_FullDocument_ParsesCorrectly()
    {
        var content = "---\nname: test\ndescription: A skill\n---\n\n# Instructions\n\nDo stuff.";
        var (fm, body) = SkillLoader.SplitFrontmatterLines(content);
        Assert.NotNull(fm);
        Assert.Equal("test", fm!.Name);
        Assert.Contains("Do stuff", body);
    }

    [Fact]
    public void SplitFrontmatterLines_NoFrontmatter_ReturnsNull()
    {
        var (fm, body) = SkillLoader.SplitFrontmatterLines("Just plain content.");
        Assert.Null(fm);
        Assert.Contains("Just plain content", body);
    }
}
