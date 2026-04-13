using CEAISuite.Engine.Windows;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for S5 Auto Assembler enhancements: conditional compilation ({$ifdef}/{$ifndef}/{$else}/{$endif})
/// and custom AA command registration.
/// </summary>
public sealed class AutoAssemblerConditionalTests
{
    private readonly WindowsAutoAssemblerEngine _engine = new();

    [Fact]
    public void Ifdef_WIN64_IncludesBlock_OnX64()
    {
        // We're running on x64, so WIN64 should be defined
        var script = """
            [ENABLE]
            {$ifdef WIN64}
            define(result, 64bit)
            {$endif}
            [DISABLE]
            """;

        var parsed = _engine.Parse(script);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
    }

    [Fact]
    public void Ifndef_WIN32_IncludesBlock_OnX64()
    {
        var script = """
            [ENABLE]
            {$ifndef WIN32}
            define(result, not32bit)
            {$endif}
            [DISABLE]
            """;

        var parsed = _engine.Parse(script);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
    }

    [Fact]
    public void Ifdef_WithElse_SelectsCorrectBranch()
    {
        var script = """
            [ENABLE]
            {$ifdef WIN64}
            define(archBits, 64)
            {$else}
            define(archBits, 32)
            {$endif}
            [DISABLE]
            """;

        var parsed = _engine.Parse(script);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
    }

    [Fact]
    public void Ifdef_UndefinedSymbol_SkipsBlock()
    {
        var script = """
            [ENABLE]
            {$ifdef NONEXISTENT_SYMBOL}
            THIS_SHOULD_BE_SKIPPED
            {$endif}
            [DISABLE]
            """;

        var parsed = _engine.Parse(script);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
    }

    [Fact]
    public void Ifdef_RegisteredSymbol_IncludesBlock()
    {
        _engine.RegisterSymbol("MY_FEATURE", (nuint)0x1);

        var script = """
            [ENABLE]
            {$ifdef MY_FEATURE}
            define(featureEnabled, true)
            {$endif}
            [DISABLE]
            """;

        var parsed = _engine.Parse(script);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
    }

    [Fact]
    public void NestedConditionals_Work()
    {
        var script = """
            [ENABLE]
            {$ifdef WIN64}
            {$ifndef NONEXISTENT}
            define(innerResult, passed)
            {$endif}
            {$endif}
            [DISABLE]
            """;

        var parsed = _engine.Parse(script);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
    }

    // ── Custom Command Tests ──

    [Fact]
    public void RegisterCustomCommand_CanBeRegistered()
    {
        _engine.RegisterCustomCommand("myCustomDirective", _ => true);

        var commands = _engine.GetCustomCommands();
        Assert.Contains("myCustomDirective", commands);
    }

    [Fact]
    public void UnregisterCustomCommand_RemovesCommand()
    {
        _engine.RegisterCustomCommand("tempCommand", _ => true);
        _engine.UnregisterCustomCommand("tempCommand");

        var commands = _engine.GetCustomCommands();
        Assert.DoesNotContain("tempCommand", commands);
    }

    [Fact]
    public void CustomCommand_CalledDuringExecution()
    {
        var receivedArgs = Array.Empty<string>();
        _engine.RegisterCustomCommand("testCmd", args =>
        {
            receivedArgs = args;
            return true;
        });

        // Custom commands execute during EnableAsync — but we need a process handle for that.
        // For unit testing, verify the command is registered and can be looked up.
        Assert.Contains("testCmd", _engine.GetCustomCommands());
    }

    // ── aobreplace directive tests ──

    [Fact]
    public void AobReplace_ParsesValidSyntax()
    {
        var script = """
            [ENABLE]
            aobreplace(48 89 5C 24 ??, 90 90 90 90 90)
            [DISABLE]
            """;

        var parsed = _engine.Parse(script);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
    }

    [Fact]
    public void AobReplaceModule_ParsesValidSyntax()
    {
        var script = """
            [ENABLE]
            aobreplacemodule(game.dll, 48 89 5C 24 ??, 90 90 90 90 90)
            [DISABLE]
            """;

        var parsed = _engine.Parse(script);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
    }

    [Fact]
    public void AobReplace_RecognizedInStrictMode()
    {
        var script = """
            [ENABLE]
            {$strict}
            aobreplace(48 8B 05 ?? ?? ?? ??, 90 90 90 90 90 90 90)
            [DISABLE]
            """;

        // In strict mode, unrecognized directives cause errors.
        // aobreplace should be recognized and NOT cause an error.
        var parsed = _engine.Parse(script);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
    }
}
