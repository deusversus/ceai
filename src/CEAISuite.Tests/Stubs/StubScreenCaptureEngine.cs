using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests.Stubs;

public sealed class StubScreenCaptureEngine : IScreenCaptureEngine
{
    public ScreenCaptureResult? NextResult { get; set; }

    public Task<ScreenCaptureResult?> CaptureWindowAsync(int processId, CancellationToken ct = default)
        => Task.FromResult(NextResult);
}
