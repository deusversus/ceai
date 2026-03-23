using System.Windows.Media;
using CEAISuite.Desktop.Services;

namespace CEAISuite.Tests.Stubs;

public sealed class StubThemeService : IThemeService
{
    public Brush FindBrush(string resourceKey) => Brushes.Transparent;
}
