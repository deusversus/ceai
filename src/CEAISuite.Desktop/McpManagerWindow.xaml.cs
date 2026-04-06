using System.Windows;
using System.Windows.Controls;
using CEAISuite.Application;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Desktop;

public partial class McpManagerWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private readonly List<McpServerDisplayItem> _servers = [];
    private bool _suppressSelection;

    public bool ServersChanged { get; private set; }

    public McpManagerWindow(AppSettingsService settingsService)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChromeHelper.EnableRoundedCorners(this);
        _settingsService = settingsService;
        LoadServers();
    }

    private void LoadServers()
    {
        _servers.Clear();
        foreach (var entry in _settingsService.Settings.McpServers)
        {
            _servers.Add(new McpServerDisplayItem
            {
                Name = entry.Name,
                Command = entry.Command,
                Arguments = entry.Arguments ?? "",
                Environment = entry.Environment is { Count: > 0 }
                    ? string.Join("\n", entry.Environment.Select(kv => $"{kv.Key}={kv.Value}"))
                    : "",
                Enabled = entry.Enabled,
                AutoConnect = entry.AutoConnect,
                StatusIcon = entry.Enabled ? "\u25CF" : "\u25CB",
            });
        }
        ServerList.ItemsSource = null;
        ServerList.ItemsSource = _servers;
    }

    private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        SaveCurrentSelection(); // Save edits from previous selection

        if (ServerList.SelectedItem is McpServerDisplayItem item)
        {
            EditPanel.Visibility = Visibility.Visible;
            RemoveBtn.IsEnabled = true;
            ServerNameBox.Text = item.Name;
            ServerCommandBox.Text = item.Command;
            ServerArgsBox.Text = item.Arguments;
            ServerEnvBox.Text = item.Environment;
            ServerEnabledCheck.IsChecked = item.Enabled;
            ServerAutoConnectCheck.IsChecked = item.AutoConnect;
            TestStatusText.Text = "";
        }
        else
        {
            EditPanel.Visibility = Visibility.Collapsed;
            RemoveBtn.IsEnabled = false;
        }
    }

    private void SaveCurrentSelection()
    {
        if (ServerList.SelectedItem is not McpServerDisplayItem item) return;
        item.Name = ServerNameBox.Text.Trim();
        item.Command = ServerCommandBox.Text.Trim();
        item.Arguments = ServerArgsBox.Text.Trim();
        item.Environment = ServerEnvBox.Text.Trim();
        item.Enabled = ServerEnabledCheck.IsChecked == true;
        item.AutoConnect = ServerAutoConnectCheck.IsChecked == true;
        item.StatusIcon = item.Enabled ? "\u25CF" : "\u25CB";

        // Refresh the list display
        _suppressSelection = true;
        var idx = ServerList.SelectedIndex;
        ServerList.ItemsSource = null;
        ServerList.ItemsSource = _servers;
        ServerList.SelectedIndex = idx;
        _suppressSelection = false;
    }

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentSelection();
        var newServer = new McpServerDisplayItem
        {
            Name = "New Server",
            Command = "",
            Arguments = "",
            Environment = "",
            Enabled = true,
            AutoConnect = true,
            StatusIcon = "\u25CF",
        };
        _servers.Add(newServer);
        ServerList.ItemsSource = null;
        ServerList.ItemsSource = _servers;
        ServerList.SelectedItem = newServer;
    }

    private void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not McpServerDisplayItem item) return;
        if (MessageBox.Show($"Remove server \"{item.Name}\"?", "Remove Server",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _servers.Remove(item);
        ServerList.ItemsSource = null;
        ServerList.ItemsSource = _servers;
        EditPanel.Visibility = Visibility.Collapsed;
        RemoveBtn.IsEnabled = false;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentSelection();
        if (ServerList.SelectedItem is not McpServerDisplayItem item) return;
        if (string.IsNullOrWhiteSpace(item.Command))
        {
            TestStatusText.Text = "Command is required.";
            return;
        }

        TestBtn.IsEnabled = false;
        TestStatusText.Text = "Connecting...";

        try
        {
            var config = new McpServerConfig
            {
                Name = item.Name,
                Command = item.Command,
                Arguments = string.IsNullOrWhiteSpace(item.Arguments) ? null : item.Arguments,
                Environment = ParseEnvVars(item.Environment),
            };
            var client = new McpClient(config);
            await client.ConnectAsync(CancellationToken.None);
            var tools = await client.DiscoverToolsAsync(CancellationToken.None);
            TestStatusText.Text = $"Connected \u2014 {tools.Count} tool(s) discovered.";
            await client.DisposeAsync();
        }
        catch (Exception ex)
        {
            TestStatusText.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            TestBtn.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentSelection();

        var entries = _servers.Select(s => new McpServerSettingsEntry
        {
            Name = s.Name,
            Command = s.Command,
            Arguments = string.IsNullOrWhiteSpace(s.Arguments) ? null : s.Arguments,
            Environment = ParseEnvVars(s.Environment),
            Enabled = s.Enabled,
            AutoConnect = s.AutoConnect,
        }).ToList();

        _settingsService.Settings.McpServers = entries;
        _settingsService.Save();
        ServersChanged = true;

        MessageBox.Show("MCP server settings saved. Restart or reconnect for changes to take effect.",
            "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = ServersChanged;
        Close();
    }

    private static Dictionary<string, string>? ParseEnvVars(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var dict = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
                dict[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return dict.Count > 0 ? dict : null;
    }

    // ─── Custom Title Bar ────────────────────────────────────────────────
    private void CaptionMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;
    private void CaptionMaximizeRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CaptionClose_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaximizeIcon != null)
            MaximizeIcon.Data = WindowState == WindowState.Maximized
                ? System.Windows.Media.Geometry.Parse("M 0,2 H 8 V 10 H 0 Z M 2,0 H 10 V 8")
                : System.Windows.Media.Geometry.Parse("M 0,0 H 10 V 10 H 0 Z");
        if (CaptionMaximizeButton != null)
            CaptionMaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore Down" : "Maximize";
        RootGrid.Margin = WindowState == WindowState.Maximized
            ? new Thickness(7) : new Thickness(0);
    }
}

public sealed class McpServerDisplayItem
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Environment { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool AutoConnect { get; set; } = true;
    public string StatusIcon { get; set; } = "\u25CF";
}
