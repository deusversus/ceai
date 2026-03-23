using System.Windows.Media;

namespace CEAISuite.Desktop.Services;

/// <summary>
/// Abstracts WPF resource dictionary lookups so ViewModels
/// can resolve theme brushes without coupling to <see cref="System.Windows.Application"/>.
/// </summary>
public interface IThemeService
{
    Brush FindBrush(string resourceKey);
}
