using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CEAISuite.Desktop;

/// <summary>
/// Returns Visible when the value is non-null and non-empty, Collapsed otherwise.
/// Used for conditionally showing tool results in streaming content blocks.
/// </summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
