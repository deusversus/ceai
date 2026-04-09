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
}
