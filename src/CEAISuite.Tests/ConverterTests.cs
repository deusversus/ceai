using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CEAISuite.Desktop;

namespace CEAISuite.Tests;

public sealed class ConverterTests
{
    // ── NullToCollapsedConverter ──

    private readonly NullToCollapsedConverter _nullConverter = new();

    [Fact]
    public void NullToCollapsed_NonEmptyString_ReturnsVisible()
    {
        var result = _nullConverter.Convert("hello", typeof(Visibility), null, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void NullToCollapsed_EmptyString_ReturnsCollapsed()
    {
        var result = _nullConverter.Convert("", typeof(Visibility), null, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void NullToCollapsed_NullValue_ReturnsCollapsed()
    {
        var result = _nullConverter.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void NullToCollapsed_NonStringObject_ReturnsCollapsed()
    {
        var result = _nullConverter.Convert(42, typeof(Visibility), null, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void NullToCollapsed_WhitespaceString_ReturnsVisible()
    {
        // Whitespace is non-empty, so it should be Visible
        var result = _nullConverter.Convert("  ", typeof(Visibility), null, CultureInfo.InvariantCulture);
        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void NullToCollapsed_ConvertBack_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            _nullConverter.ConvertBack(Visibility.Visible, typeof(string), null, CultureInfo.InvariantCulture));
    }

    // ── StringToBrushConverter ──

    private readonly StringToBrushConverter _brushConverter = new();

    [Fact]
    public void StringToBrush_ValidHexColor_ReturnsBrush()
    {
        var result = _brushConverter.Convert("#CC4444", typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(0xCC, brush.Color.R);
        Assert.Equal(0x44, brush.Color.G);
        Assert.Equal(0x44, brush.Color.B);
    }

    [Fact]
    public void StringToBrush_NullValue_ReturnsDefaultBrush()
    {
        var result = _brushConverter.Convert(null, typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(0xCC, brush.Color.R);
        Assert.Equal(0xCC, brush.Color.G);
        Assert.Equal(0xCC, brush.Color.B);
    }

    [Fact]
    public void StringToBrush_EmptyString_ReturnsDefaultBrush()
    {
        var result = _brushConverter.Convert("", typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(0xCC, brush.Color.R);
    }

    [Fact]
    public void StringToBrush_InvalidColor_ReturnsDefaultBrush()
    {
        var result = _brushConverter.Convert("not-a-color", typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(0xCC, brush.Color.R);
    }

    [Fact]
    public void StringToBrush_NamedColor_ReturnsBrush()
    {
        var result = _brushConverter.Convert("Red", typeof(Brush), null, CultureInfo.InvariantCulture);
        Assert.IsType<SolidColorBrush>(result);
        var brush = (SolidColorBrush)result;
        Assert.Equal(0xFF, brush.Color.R);
        Assert.Equal(0x00, brush.Color.G);
        Assert.Equal(0x00, brush.Color.B);
    }

    [Fact]
    public void StringToBrush_ConvertBack_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            _brushConverter.ConvertBack(Brushes.Red, typeof(string), null, CultureInfo.InvariantCulture));
    }
}
