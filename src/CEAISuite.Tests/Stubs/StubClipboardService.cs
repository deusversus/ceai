using CEAISuite.Desktop.Services;

namespace CEAISuite.Tests.Stubs;

public sealed class StubClipboardService : IClipboardService
{
    public string? LastText { get; private set; }
    public int CallCount { get; private set; }

    public string? TextToReturn { get; set; }

    public void SetText(string text)
    {
        LastText = text;
        CallCount++;
    }

    public string? GetText() => TextToReturn;
}
