using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Desktop;

public partial class MemoryBrowserWindow : Window
{
    private static Brush ThemeBrush(string key) =>
        System.Windows.Application.Current.FindResource(key) as Brush ?? Brushes.Transparent;

    private readonly IEngineFacade _engine;
    private readonly int _processId;
    private readonly string _processName;
    private nuint _currentAddress;
    private byte[]? _previousBytes;
    private System.Windows.Threading.DispatcherTimer? _autoTimer;

    public MemoryBrowserWindow(IEngineFacade engine, int processId, string processName, nuint initialAddress = 0)
    {
        InitializeComponent();
        _engine = engine;
        _processId = processId;
        _processName = processName;
        _currentAddress = initialAddress == 0 ? (nuint)0x00400000 : initialAddress;
        Title = $"CE AI Suite — Memory Browser [{processName} ({processId})]";
        AddressBox.Text = $"0x{_currentAddress:X}";
    }

    private int GetByteCount()
    {
        var item = ByteCountCombo.SelectedItem as System.Windows.Controls.ComboBoxItem;
        return int.TryParse(item?.Content?.ToString(), out var v) ? v : 256;
    }

    private async void GoToAddress(object sender, RoutedEventArgs e)
    {
        var text = AddressBox.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        if (!ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var addr))
        {
            StatusText.Text = "Invalid address format.";
            return;
        }
        _currentAddress = (nuint)addr;
        await ReadAndDisplay();
    }

    private async void PagePrev(object sender, RoutedEventArgs e)
    {
        var size = (nuint)GetByteCount();
        _currentAddress = _currentAddress > size ? _currentAddress - size : 0;
        AddressBox.Text = $"0x{_currentAddress:X}";
        await ReadAndDisplay();
    }

    private async void PageNext(object sender, RoutedEventArgs e)
    {
        _currentAddress += (nuint)GetByteCount();
        AddressBox.Text = $"0x{_currentAddress:X}";
        await ReadAndDisplay();
    }

    private async void RefreshView(object sender, RoutedEventArgs e) => await ReadAndDisplay();

    private void AutoRefreshToggled(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshCheckBox.IsChecked == true)
        {
            _autoTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _autoTimer.Tick += async (_, _) =>
            {
                _autoTimer.Stop();
                await ReadAndDisplay();
                _autoTimer?.Start();
            };
            _autoTimer.Start();
        }
        else
        {
            _autoTimer?.Stop();
            _autoTimer = null;
        }
    }

    private async Task ReadAndDisplay()
    {
        try
        {
            var count = GetByteCount();
            var result = await _engine.ReadMemoryAsync(_processId, _currentAddress, count);
            var bytes = result.Bytes.ToArray();

            FormatHexDump(bytes);

            _previousBytes = bytes;
            StatusText.Text = $"Read {bytes.Length} bytes at 0x{_currentAddress:X} from {_processName} ({_processId})";
        }
        catch (Exception ex)
        {
            HexDisplay.Inlines.Clear();
            HexDisplay.Text = $"Error reading memory: {ex.Message}";
            StatusText.Text = $"Read failed: {ex.Message}";
        }
    }

    private void FormatHexDump(byte[] bytes)
    {
        const int bytesPerLine = 16;
        HexDisplay.Inlines.Clear();

        for (var offset = 0; offset < bytes.Length; offset += bytesPerLine)
        {
            var lineAddr = _currentAddress + (nuint)offset;

            // Address column
            HexDisplay.Inlines.Add(new Run($"{lineAddr:X16}  ")
            {
                Foreground = ThemeBrush("HexAddressForeground")
            });

            // Hex bytes
            var lineEnd = Math.Min(offset + bytesPerLine, bytes.Length);
            for (var i = offset; i < offset + bytesPerLine; i++)
            {
                if (i < lineEnd)
                {
                    var b = bytes[i];
                    var changed = _previousBytes is not null && i < _previousBytes.Length && _previousBytes[i] != b;
                    var fg = changed
                        ? ThemeBrush("HexChangedForeground")
                        : b == 0
                            ? ThemeBrush("HexZeroForeground")
                            : ThemeBrush("HexNormalForeground");

                    HexDisplay.Inlines.Add(new Run($"{b:X2} ") { Foreground = fg });
                }
                else
                {
                    HexDisplay.Inlines.Add(new Run("   "));
                }

                // Extra space between 8-byte groups
                if ((i - offset) == 7) HexDisplay.Inlines.Add(new Run(" "));
            }

            // ASCII column
            HexDisplay.Inlines.Add(new Run(" │ ")
            {
                Foreground = ThemeBrush("HexSeparatorForeground")
            });

            var sb = new StringBuilder(bytesPerLine);
            for (var i = offset; i < lineEnd; i++)
            {
                var c = (char)bytes[i];
                sb.Append(c is >= ' ' and <= '~' ? c : '.');
            }
            HexDisplay.Inlines.Add(new Run(sb.ToString())
            {
                Foreground = ThemeBrush("HexAsciiForeground")
            });

            HexDisplay.Inlines.Add(new Run("\n"));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoTimer?.Stop();
        _autoTimer = null;
        base.OnClosed(e);
    }
}
