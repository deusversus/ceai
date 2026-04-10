using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CEAISuite.Desktop;

public partial class WelcomeDialog : Window
{
    private int _currentPage;
    private const int PageCount = 3;

    private static readonly string[] Titles =
    [
        "Let's get you set up",
        "Getting Started",
        "Meet the AI Operator"
    ];

    private static readonly string[] Subtitles =
    [
        "You can change these later in Settings.",
        "A quick overview of the core workflow.",
        "Your intelligent memory analysis assistant."
    ];

    public WelcomeDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChromeHelper.EnableRoundedCorners(this);
        UpdatePageVisibility();
    }

    /// <summary>The API key entered by the user (plaintext).</summary>
    public string ApiKey => ApiKeyBox.Password;

    /// <summary>The selected AI provider ("openai", "anthropic", "gemini", "copilot", "openai-compatible").</summary>
    public string SelectedProvider =>
        ProviderAnthropic.IsChecked == true ? "anthropic"
        : ProviderGemini.IsChecked == true ? "gemini"
        : ProviderCopilot.IsChecked == true ? "copilot"
        : ProviderOpenRouter.IsChecked == true ? "openrouter"
        : ProviderCompatible.IsChecked == true ? "openai-compatible"
        : "openai";

    /// <summary>The selected theme: "Dark" or "Light".</summary>
    public string SelectedTheme =>
        ThemeLight.IsChecked == true ? "Light" : "Dark";

    /// <summary>The selected density preset: "Clean", "Balanced", or "Dense".</summary>
    public string SelectedDensity =>
        DensityClean.IsChecked == true ? "Clean"
        : DensityDense.IsChecked == true ? "Dense"
        : "Balanced";

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage < PageCount - 1)
        {
            _currentPage++;
            UpdatePageVisibility();
        }
        else
        {
            // Last page — finish
            DialogResult = true;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 0)
        {
            _currentPage--;
            UpdatePageVisibility();
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void UpdatePageVisibility()
    {
        // Page content
        Page0.Visibility = _currentPage == 0 ? Visibility.Visible : Visibility.Collapsed;
        Page1.Visibility = _currentPage == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page2.Visibility = _currentPage == 2 ? Visibility.Visible : Visibility.Collapsed;

        // Title and subtitle
        PageTitle.Text = Titles[_currentPage];
        PageSubtitle.Text = Subtitles[_currentPage];

        // Back button: hidden on first page
        BackButton.Visibility = _currentPage > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Next button text: "Get Started" on last page, "Next" otherwise
        NextButton.Content = _currentPage == PageCount - 1 ? "Get Started" : "Next";

        // Page indicator dots
        Ellipse[] dots = [Dot0, Dot1, Dot2];
        for (int i = 0; i < dots.Length; i++)
        {
            dots[i].Fill = i == _currentPage
                ? (Brush)FindResource("AccentForeground")
                : (Brush)FindResource("SecondaryForeground");
            dots[i].Opacity = i == _currentPage ? 1.0 : 0.4;
        }
    }

    private void Provider_Checked(object sender, RoutedEventArgs e)
    {
        if (ApiKeySection is null) return; // designer guard

        var isCopilot = ProviderCopilot.IsChecked == true;
        ApiKeySection.Visibility = isCopilot ? Visibility.Collapsed : Visibility.Visible;
        CopilotNote.Visibility = isCopilot ? Visibility.Visible : Visibility.Collapsed;

        ApiKeySubtitle.Text = SelectedProvider switch
        {
            "anthropic" => "Get yours at console.anthropic.com",
            "gemini" => "Get yours at aistudio.google.com",
            "openrouter" => "Get yours at openrouter.ai/keys",
            "openai-compatible" => "Enter the API key for your provider",
            _ => "Get yours at platform.openai.com",
        };
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        var theme = ThemeLight?.IsChecked == true ? AppTheme.Light : AppTheme.Dark;
        ThemeManager.ApplyTheme(theme);
    }

    private void CaptionClose_Click(object sender, RoutedEventArgs e) => Close();
}
