namespace CEAISuite.Engine.Abstractions;

/// <summary>Result of capturing a process window as a PNG image.</summary>
public sealed record ScreenCaptureResult(
    byte[] PngData,
    int Width,
    int Height,
    string WindowTitle);

/// <summary>Engine for capturing screenshots of process windows.</summary>
public interface IScreenCaptureEngine
{
    /// <summary>Capture the main window of a process as a PNG image.</summary>
    Task<ScreenCaptureResult?> CaptureWindowAsync(int processId, CancellationToken ct = default);
}
