using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubCodeCaveEngine : ICodeCaveEngine
{
    private readonly List<CodeCaveHook> _hooks = new();

    public Task<CodeCaveInstallResult> InstallHookAsync(int processId, nuint address,
        bool captureRegisters = true, CancellationToken cancellationToken = default)
    {
        var hook = new CodeCaveHook($"hook-{_hooks.Count}", address, (nuint)0xDEAD0000, 14, true, 0);
        _hooks.Add(hook);
        return Task.FromResult(new CodeCaveInstallResult(true, hook, null));
    }

    public Task<bool> RemoveHookAsync(int processId, string hookId,
        CancellationToken cancellationToken = default)
    {
        var idx = _hooks.FindIndex(h => h.Id == hookId);
        if (idx >= 0) { _hooks.RemoveAt(idx); return Task.FromResult(true); }
        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<CodeCaveHook>> ListHooksAsync(int processId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CodeCaveHook>>(_hooks.ToList());

    public Task<IReadOnlyList<BreakpointHitEvent>> GetHookHitsAsync(string hookId,
        int maxEntries = 50, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BreakpointHitEvent>>([]);
}
