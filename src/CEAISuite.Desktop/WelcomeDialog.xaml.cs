using System.Windows;

namespace CEAISuite.Desktop;

public partial class WelcomeDialog : Window
{
    public WelcomeDialog()
    {
        InitializeComponent();
    }

    /// <summary>The API key entered by the user (plaintext).</summary>
    public string ApiKey => ApiKeyBox.Password;

    /// <summary>The selected theme: "Dark" or "Light".</summary>
    public string SelectedTheme =>
        ThemeLight.IsChecked == true ? "Light" : "Dark";

    /// <summary>The selected density preset: "Clean", "Balanced", or "Dense".</summary>
    public string SelectedDensity =>
        DensityClean.IsChecked == true ? "Clean"
        : DensityDense.IsChecked == true ? "Dense"
        : "Balanced";

    private void GetStarted_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        var theme = ThemeLight?.IsChecked == true ? AppTheme.Light : AppTheme.Dark;
        ThemeManager.ApplyTheme(theme);
    }
}
