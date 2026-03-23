using CEAISuite.Desktop.Services;

namespace CEAISuite.Tests.Stubs;

/// <summary>
/// Test-only dispatcher that runs actions synchronously on the calling thread.
/// </summary>
public sealed class StubDispatcherService : IDispatcherService
{
    public void Invoke(Action action) => action();
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
}
