using System.Globalization;
using System.Windows.Data;

namespace CEAISuite.Desktop;

/// <summary>
/// Converts a boolean to one of two strings specified in the ConverterParameter.
/// Parameter format: "TrueValue|FalseValue" (e.g., "■|↑" or "Stop|Send").
/// </summary>
public sealed class BoolToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string)?.Split('|');
        if (parts is not { Length: 2 }) return "";
        return value is true ? parts[0] : parts[1];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
