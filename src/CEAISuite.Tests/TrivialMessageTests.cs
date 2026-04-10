using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for <see cref="AgentLoop.IsTrivialMessage"/> — the allowlist-based
/// classifier that determines whether to skip tool schemas on the first turn.
///
/// CRITICAL: False positives (marking a real request as trivial) cause tool
/// stripping — the model can't use tools and gives a useless conversational
/// reply. The classifier MUST be conservative: when in doubt, return false.
/// </summary>
public class TrivialMessageTests
{
    // ── TRUE: Known trivial messages that should bypass tools ──

    [Theory]
    [InlineData("hi")]
    [InlineData("Hi")]
    [InlineData("HI")]
    [InlineData("hello")]
    [InlineData("hey")]
    [InlineData("howdy")]
    [InlineData("yo")]
    [InlineData("sup")]
    [InlineData("good morning")]
    [InlineData("good afternoon")]
    [InlineData("good evening")]
    public void Greetings_AreTrivial(string message) =>
        Assert.True(AgentLoop.IsTrivialMessage(message));

    [Theory]
    [InlineData("ok")]
    [InlineData("okay")]
    [InlineData("sure")]
    [InlineData("thanks")]
    [InlineData("thank you")]
    [InlineData("thx")]
    [InlineData("ty")]
    [InlineData("got it")]
    [InlineData("understood")]
    [InlineData("cool")]
    [InlineData("nice")]
    [InlineData("great")]
    [InlineData("awesome")]
    [InlineData("sounds good")]
    [InlineData("perfect")]
    [InlineData("alright")]
    public void Acknowledgments_AreTrivial(string message) =>
        Assert.True(AgentLoop.IsTrivialMessage(message));

    [Theory]
    [InlineData("bye")]
    [InlineData("goodbye")]
    [InlineData("see you")]
    [InlineData("later")]
    [InlineData("cya")]
    public void Farewells_AreTrivial(string message) =>
        Assert.True(AgentLoop.IsTrivialMessage(message));

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("yep")]
    [InlineData("nope")]
    [InlineData("yeah")]
    [InlineData("nah")]
    public void YesNo_AreTrivial(string message) =>
        Assert.True(AgentLoop.IsTrivialMessage(message));

    [Theory]
    [InlineData("what can you do")]
    [InlineData("who are you")]
    [InlineData("help")]
    public void MetaQuestions_AreTrivial(string message) =>
        Assert.True(AgentLoop.IsTrivialMessage(message));

    [Theory]
    [InlineData("hi!")]
    [InlineData("hello.")]
    [InlineData("thanks!")]
    [InlineData("ok?")]
    [InlineData("  hi  ")]
    public void TrailingPunctuation_AndWhitespace_AreTrivial(string message) =>
        Assert.True(AgentLoop.IsTrivialMessage(message));

    [Theory]
    [InlineData("hi claude")]
    [InlineData("hey there")]
    [InlineData("hello friend")]
    public void GreetingWithName_AreTrivial(string message) =>
        Assert.True(AgentLoop.IsTrivialMessage(message));

    // ── FALSE: Real requests that MUST NOT be classified as trivial ──

    [Theory]
    [InlineData("what's my health?")]
    [InlineData("show me the value")]
    [InlineData("help me with this game")]
    [InlineData("open the exe")]
    [InlineData("what changed?")]
    [InlineData("fix it")]
    [InlineData("set it to 999")]
    [InlineData("lock it")]
    [InlineData("undo that")]
    [InlineData("compare them")]
    [InlineData("what's at this spot?")]
    [InlineData("show me gold")]
    [InlineData("make me invincible")]
    [InlineData("infinite ammo")]
    [InlineData("can you modify the game?")]
    public void AmbiguousRequests_AreNotTrivial(string message) =>
        Assert.False(AgentLoop.IsTrivialMessage(message));

    [Theory]
    [InlineData("scan for health")]
    [InlineData("read memory at 0x400000")]
    [InlineData("attach to game.exe")]
    [InlineData("find the pointer")]
    [InlineData("write 999 to address")]
    [InlineData("set a breakpoint")]
    [InlineData("disassemble this function")]
    [InlineData("freeze the value")]
    [InlineData("run the script")]
    [InlineData("search for 100")]
    public void TechnicalRequests_AreNotTrivial(string message) =>
        Assert.False(AgentLoop.IsTrivialMessage(message));

    [Theory]
    [InlineData("I need help finding the health value in Dark Souls")]
    [InlineData("Can you scan for a float value around 100.0?")]
    [InlineData("The game is running, what should I do first?")]
    public void LongRequests_AreNotTrivial(string message) =>
        Assert.False(AgentLoop.IsTrivialMessage(message));

    // ── Edge cases ──

    [Fact]
    public void Null_IsNotTrivial() =>
        Assert.False(AgentLoop.IsTrivialMessage(null));

    [Fact]
    public void Empty_IsNotTrivial() =>
        Assert.False(AgentLoop.IsTrivialMessage(""));

    [Fact]
    public void WhitespaceOnly_IsNotTrivial() =>
        Assert.False(AgentLoop.IsTrivialMessage("   "));

    [Theory]
    [InlineData("hi 0x400000")]  // greeting + hex address = not trivial
    [InlineData("hey 12345")]    // greeting + number = not trivial
    public void GreetingWithNumber_IsNotTrivial(string message) =>
        Assert.False(AgentLoop.IsTrivialMessage(message));

    [Fact]
    public void VeryLongGreeting_IsNotTrivial() =>
        Assert.False(AgentLoop.IsTrivialMessage("hello there my friend how are you doing today on this fine day"));
}
