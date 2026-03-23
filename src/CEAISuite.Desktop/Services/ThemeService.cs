using System.Windows.Media;

namespace CEAISuite.Desktop.Services;

public sealed class ThemeService : IThemeService
{
    public Brush FindBrush(string resourceKey) =>
        System.Windows.Application.Current.FindResource(resourceKey) as Brush ?? Brushes.Transparent;
}
