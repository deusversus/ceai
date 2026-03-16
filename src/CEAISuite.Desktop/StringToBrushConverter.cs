using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CEAISuite.Desktop;

/// <summary>
/// Converts hex color strings like "#CC4444" to WPF Brush objects for use in data bindings.
/// </summary>
public sealed class StringToBrushConverter : IValueConverter
{
    private static readonly BrushConverter Converter = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string colorStr && !string.IsNullOrEmpty(colorStr))
        {
            try
            {
                return Converter.ConvertFromString(colorStr) ?? Brushes.Black;
            }
            catch
            {
                return Brushes.Black;
            }
        }
        return Brushes.Black;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
