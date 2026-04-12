using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;
using CEAISuite.Tests.Stubs;

namespace CEAISuite.Tests;

public class SpeedHackServiceTests
{
    private const int TestPid = 1234;

    private static (SpeedHackService service, StubSpeedHackEngine engine) CreateService()
    {
        var engine = new StubSpeedHackEngine();
        var service = new SpeedHackService(engine);
        return (service, engine);
    }

    [Fact]
    public async Task Apply_Succeeds_StateBecomesActive()
    {
        var (svc, _) = CreateService();

        var result = await svc.ApplyAsync(TestPid, 2.0);

        Assert.True(result.Success);
        Assert.NotNull(result.PatchedFunctions);
        Assert.Contains("timeGetTime", result.PatchedFunctions!);
        var state = svc.GetState(TestPid);
        Assert.True(state.IsActive);
        Assert.Equal(2.0, state.Multiplier);
    }

    [Fact]
    public async Task Apply_WhenAlreadyActive_ReturnsError()
    {
        var (svc, _) = CreateService();
        await svc.ApplyAsync(TestPid, 2.0);

        var result = await svc.ApplyAsync(TestPid, 3.0);

        Assert.False(result.Success);
        Assert.Contains("already active", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remove_Succeeds_StateBecomesInactive()
    {
        var (svc, _) = CreateService();
        await svc.ApplyAsync(TestPid, 2.0);

        var result = await svc.RemoveAsync(TestPid);

        Assert.True(result.Success);
        var state = svc.GetState(TestPid);
        Assert.False(state.IsActive);
    }

    [Fact]
    public async Task Remove_WhenNotActive_ReturnsError()
    {
        var (svc, _) = CreateService();

        var result = await svc.RemoveAsync(TestPid);

        Assert.False(result.Success);
        Assert.Contains("not active", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateMultiplier_WhenActive_Succeeds()
    {
        var (svc, _) = CreateService();
        await svc.ApplyAsync(TestPid, 2.0);

        var result = await svc.UpdateMultiplierAsync(TestPid, 4.0);

        Assert.True(result.Success);
        var state = svc.GetState(TestPid);
        Assert.Equal(4.0, state.Multiplier);
    }

    [Fact]
    public async Task UpdateMultiplier_WhenNotActive_ReturnsError()
    {
        var (svc, _) = CreateService();

        var result = await svc.UpdateMultiplierAsync(TestPid, 4.0);

        Assert.False(result.Success);
        Assert.Contains("not active", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Multiplier_BelowMinimum_ClampedTo01()
    {
        var (svc, _) = CreateService();

        var result = await svc.ApplyAsync(TestPid, -5.0);

        Assert.True(result.Success);
        var state = svc.GetState(TestPid);
        Assert.Equal(0.1, state.Multiplier, precision: 5);
    }

    [Fact]
    public async Task Multiplier_AboveMaximum_ClampedTo8()
    {
        var (svc, _) = CreateService();

        var result = await svc.ApplyAsync(TestPid, 100.0);

        Assert.True(result.Success);
        var state = svc.GetState(TestPid);
        Assert.Equal(8.0, state.Multiplier, precision: 5);
    }

    [Fact]
    public async Task EngineNotAvailable_ReturnsDescriptiveError()
    {
        var svc = new SpeedHackService(engine: null);

        var applyResult = await svc.ApplyAsync(TestPid, 2.0);
        var removeResult = await svc.RemoveAsync(TestPid);
        var updateResult = await svc.UpdateMultiplierAsync(TestPid, 2.0);

        Assert.False(applyResult.Success);
        Assert.Contains("not available", applyResult.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.False(removeResult.Success);
        Assert.Contains("not available", removeResult.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.False(updateResult.Success);
        Assert.Contains("not available", updateResult.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetState_WhenInactive_ReturnsDefault()
    {
        var (svc, _) = CreateService();

        var state = svc.GetState(TestPid);

        Assert.False(state.IsActive);
        Assert.Equal(1.0, state.Multiplier);
        Assert.Empty(state.PatchedFunctions);
    }
}
