using System.Windows;

namespace CEAISuite.Desktop.Services;

public sealed class ClipboardService : IClipboardService
{
    public void SetText(string text) => Clipboard.SetText(text);
}
