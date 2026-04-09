using CEAISuite.Desktop;

namespace CEAISuite.Tests;

/// <summary>
/// Tests for ThemeManager. ApplyTheme and static properties require a WPF Application
/// with pack:// URI scheme registered, so they are not testable in headless xUnit.
/// We test DetectSystemTheme (registry-based) and enum values only.
/// </summary>
public sealed class ThemeManagerTests
{
    [Fact]
    public void AppTheme_HasExpectedValues()
    {
        Assert.Equal(0, (int)AppTheme.System);
        Assert.Equal(1, (int)AppTheme.Dark);
        Assert.Equal(2, (int)AppTheme.Light);
    }

    [Fact]
    public void DetectSystemTheme_ReturnsDarkOrLight()
    {
        // DetectSystemTheme reads the Windows registry and should return a valid value
        var theme = ThemeManager.DetectSystemTheme();
        Assert.True(theme == AppTheme.Dark || theme == AppTheme.Light,
            $"Expected Dark or Light, got {theme}");
    }

    [Fact]
    public void AppTheme_EnumNames_AreCorrect()
    {
        Assert.Equal("System", AppTheme.System.ToString());
        Assert.Equal("Dark", AppTheme.Dark.ToString());
        Assert.Equal("Light", AppTheme.Light.ToString());
    }

    [Fact]
    public void AppTheme_AllValues_AreDefined()
    {
        var values = Enum.GetValues<AppTheme>();
        Assert.Equal(3, values.Length);
        Assert.Contains(AppTheme.System, values);
        Assert.Contains(AppTheme.Dark, values);
        Assert.Contains(AppTheme.Light, values);
    }

    [Fact]
    public void DetectSystemTheme_NeverReturnsSystem()
    {
        // DetectSystemTheme resolves System to either Dark or Light
        var theme = ThemeManager.DetectSystemTheme();
        Assert.NotEqual(AppTheme.System, theme);
    }

    [Fact]
    public void DetectSystemTheme_IsIdempotent()
    {
        var t1 = ThemeManager.DetectSystemTheme();
        var t2 = ThemeManager.DetectSystemTheme();
        Assert.Equal(t1, t2);
    }

    [Theory]
    [InlineData("System", AppTheme.System)]
    [InlineData("Dark", AppTheme.Dark)]
    [InlineData("Light", AppTheme.Light)]
    public void AppTheme_ParseFromString(string name, AppTheme expected)
    {
        Assert.True(Enum.TryParse<AppTheme>(name, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("dark")]
    [InlineData("light")]
    public void AppTheme_ParseCaseInsensitive(string name)
    {
        Assert.True(Enum.TryParse<AppTheme>(name, ignoreCase: true, out _));
    }

    [Fact]
    public void AppTheme_InvalidString_ReturnsFalse()
    {
        Assert.False(Enum.TryParse<AppTheme>("Sepia", out _));
    }

    [Fact]
    public void AppTheme_CastToInt_IsSequential()
    {
        Assert.Equal(0, (int)AppTheme.System);
        Assert.Equal(1, (int)AppTheme.Dark);
        Assert.Equal(2, (int)AppTheme.Light);
    }

    // Note: CurrentTheme and ResolvedTheme require ThemeManager static init
    // which uses pack:// URIs (unavailable in headless xUnit), so we skip those.
}
