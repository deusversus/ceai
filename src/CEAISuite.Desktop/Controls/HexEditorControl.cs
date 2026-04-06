using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CEAISuite.Desktop.Models;

namespace CEAISuite.Desktop.Controls;

/// <summary>
/// Custom hex editor control using OnRender for pixel-level control.
/// Displays three columns: address gutter, hex pane, ASCII pane.
/// Supports byte selection via mouse and keyboard, and inline hex editing.
/// </summary>
public sealed class HexEditorControl : FrameworkElement
{
    // ── Constants ──

    private const double CharWidth = 7.8;   // Consolas 13pt approximate char width
    private const double LineHeight = 18.0;
    private const double Padding = 8.0;

    // Address column: 16 hex chars + 2 spaces = 18 chars
    private const int AddressChars = 18;
    // Per hex byte: "XX " = 3 chars, plus 1 extra space at midline
    private const int HexExtraGap = 1;
    // Separator: " │ " = 3 chars
    private const int SeparatorChars = 3;

    private static readonly Typeface MonoTypeface = new("Consolas");

    // ── Dependency Properties ──

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(byte[]), typeof(HexEditorControl),
            new FrameworkPropertyMetadata(Array.Empty<byte>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PreviousSourceProperty =
        DependencyProperty.Register(nameof(PreviousSource), typeof(byte[]), typeof(HexEditorControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaseAddressProperty =
        DependencyProperty.Register(nameof(BaseAddress), typeof(ulong), typeof(HexEditorControl),
            new FrameworkPropertyMetadata(0UL, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BytesPerLineProperty =
        DependencyProperty.Register(nameof(BytesPerLine), typeof(int), typeof(HexEditorControl),
            new FrameworkPropertyMetadata(16, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SelectionStartProperty =
        DependencyProperty.Register(nameof(SelectionStart), typeof(int), typeof(HexEditorControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnSelectionChanged));

    public static readonly DependencyProperty SelectionLengthProperty =
        DependencyProperty.Register(nameof(SelectionLength), typeof(int), typeof(HexEditorControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnSelectionChanged));

    public static readonly DependencyProperty CursorOffsetProperty =
        DependencyProperty.Register(nameof(CursorOffset), typeof(int), typeof(HexEditorControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsReadOnlyProperty =
        DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(HexEditorControl),
            new PropertyMetadata(true));

    public static readonly DependencyProperty SearchHitsProperty =
        DependencyProperty.Register(nameof(SearchHits), typeof(IEnumerable<int>), typeof(HexEditorControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BookmarksProperty =
        DependencyProperty.Register(nameof(Bookmarks), typeof(IEnumerable<ulong>), typeof(HexEditorControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public byte[] Source { get => (byte[])GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public byte[]? PreviousSource { get => (byte[]?)GetValue(PreviousSourceProperty); set => SetValue(PreviousSourceProperty, value); }
    public ulong BaseAddress { get => (ulong)GetValue(BaseAddressProperty); set => SetValue(BaseAddressProperty, value); }
    public int BytesPerLine { get => (int)GetValue(BytesPerLineProperty); set => SetValue(BytesPerLineProperty, value); }
    public int SelectionStart { get => (int)GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
    public int SelectionLength { get => (int)GetValue(SelectionLengthProperty); set => SetValue(SelectionLengthProperty, value); }
    public int CursorOffset { get => (int)GetValue(CursorOffsetProperty); set => SetValue(CursorOffsetProperty, value); }
    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public IEnumerable<int>? SearchHits { get => (IEnumerable<int>?)GetValue(SearchHitsProperty); set => SetValue(SearchHitsProperty, value); }
    public IEnumerable<ulong>? Bookmarks { get => (IEnumerable<ulong>?)GetValue(BookmarksProperty); set => SetValue(BookmarksProperty, value); }

    // ── Routed Events ──

    public static readonly RoutedEvent SelectionChangedEvent =
        EventManager.RegisterRoutedEvent(nameof(SelectionChanged), RoutingStrategy.Bubble,
            typeof(RoutedEventHandler), typeof(HexEditorControl));

    public event RoutedEventHandler SelectionChanged
    {
        add => AddHandler(SelectionChangedEvent, value);
        remove => RemoveHandler(SelectionChangedEvent, value);
    }

    public static readonly RoutedEvent ByteEditedEvent =
        EventManager.RegisterRoutedEvent(nameof(ByteEdited), RoutingStrategy.Bubble,
            typeof(EventHandler<ByteEditedEventArgs>), typeof(HexEditorControl));

    public event EventHandler<ByteEditedEventArgs> ByteEdited
    {
        add => AddHandler(ByteEditedEvent, value);
        remove => RemoveHandler(ByteEditedEvent, value);
    }

    private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HexEditorControl ctrl)
            ctrl.RaiseEvent(new RoutedEventArgs(SelectionChangedEvent));
    }

    // ── Mouse + Edit state ──

    private bool _isDragging;
    private int _dragAnchor;

    // Inline editing: user types hex digits to modify a byte
    private bool _isEditing;
    private int _editNibbleIndex; // 0 = high nibble entered, 1 = low nibble pending
    private byte _editValue;

    // Cached per-frame to avoid repeated P/Invoke in DrawText
    private double _cachedPixelsPerDip = 1.0;

    public HexEditorControl()
    {
        Focusable = true;
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    // ── Layout ──

    protected override Size MeasureOverride(Size availableSize)
    {
        var data = Source;
        if (data.Length == 0) return new Size(0, 0);

        var bpl = BytesPerLine;
        var lineCount = (data.Length + bpl - 1) / bpl;
        var charsPerLine = AddressChars + (bpl * 3) + HexExtraGap + SeparatorChars + bpl;
        var width = (charsPerLine * CharWidth) + (Padding * 2);
        var height = (lineCount * LineHeight) + (Padding * 2);

        return new Size(
            double.IsInfinity(availableSize.Width) ? width : Math.Max(width, availableSize.Width),
            double.IsInfinity(availableSize.Height) ? height : Math.Max(height, availableSize.Height));
    }

    // ── Rendering ──

    protected override void OnRender(DrawingContext dc)
    {
        // Cache DPI once per frame (avoids P/Invoke per DrawText call)
        try { _cachedPixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip; }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[HexEditorControl] Failed to get DPI: {ex.Message}"); _cachedPixelsPerDip = 1.0; }

        // Background
        var bg = FindBrush("PanelBackground") ?? Brushes.Black;
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var data = Source;
        if (data.Length == 0)
        {
            RenderPlaceholder(dc);
            return;
        }

        var bpl = BytesPerLine;
        var lineCount = (data.Length + bpl - 1) / bpl;
        var prev = PreviousSource;
        var baseAddr = BaseAddress;
        var selection = new ByteSelection(SelectionStart, SelectionLength);
        var cursor = CursorOffset;

        // Build search hit and bookmark lookup sets
        HashSet<int>? searchHitSet = null;
        if (SearchHits is { } hits)
            searchHitSet = new HashSet<int>(hits);

        HashSet<int>? bookmarkedLines = null;
        if (Bookmarks is { } bms)
        {
            bookmarkedLines = new HashSet<int>();
            foreach (var bmAddr in bms)
            {
                if (bmAddr >= baseAddr && bmAddr < baseAddr + (ulong)data.Length)
                    bookmarkedLines.Add((int)(bmAddr - baseAddr) / bpl);
            }
        }

        var addrBrush = FindBrush("HexAddressForeground") ?? Brushes.CornflowerBlue;
        var normalBrush = FindBrush("HexNormalForeground") ?? Brushes.LightGray;
        var zeroBrush = FindBrush("HexZeroForeground") ?? Brushes.DimGray;
        var changedBrush = FindBrush("HexChangedForeground") ?? Brushes.Red;
        var sepBrush = FindBrush("HexSeparatorForeground") ?? Brushes.DimGray;
        var asciiBrush = FindBrush("HexAsciiForeground") ?? Brushes.SandyBrown;
        var selBg = FindBrush("HexSelectionBackground") ?? Brushes.DarkSlateBlue;
        var selFg = FindBrush("HexSelectionForeground") ?? Brushes.White;
        var cursorBg = FindBrush("HexCursorBackground") ?? Brushes.DimGray;
        var editBg = FindBrush("HexEditingBackground") ?? Brushes.DarkGoldenrod;
        var searchHitBg = FindBrush("HexSearchHitBackground") ?? Brushes.DarkGoldenrod;
        var bookmarkGutter = FindBrush("HexBookmarkGutter") ?? Brushes.Gold;

        for (var line = 0; line < lineCount; line++)
        {
            var y = Padding + (line * LineHeight);
            var lineOffset = line * bpl;
            var lineEnd = Math.Min(lineOffset + bpl, data.Length);

            // Bookmark gutter indicator
            if (bookmarkedLines is not null && bookmarkedLines.Contains(line))
                dc.DrawRectangle(bookmarkGutter, null, new Rect(2, y, 3, LineHeight));

            // Address column
            var addr = baseAddr + (ulong)lineOffset;
            DrawText(dc, $"{addr:X16}  ", Padding, y, addrBrush);

            var hexX = Padding + (AddressChars * CharWidth);

            // Hex bytes
            var midline = bpl / 2;
            for (var i = lineOffset; i < lineOffset + bpl; i++)
            {
                var colInLine = i - lineOffset;
                var x = hexX + (colInLine * 3 * CharWidth) + (colInLine >= midline ? CharWidth : 0);

                if (i < lineEnd)
                {
                    var b = data[i];
                    var isChanged = prev is not null && i < prev.Length && prev[i] != b;
                    var isSelected = selection.Contains(i);
                    var isCursor = i == cursor;
                    var isEditingThisByte = _isEditing && isCursor;
                    var isSearchHit = searchHitSet is not null && searchHitSet.Contains(i);

                    // Background highlight (priority: editing > selection > cursor > search hit)
                    if (isEditingThisByte)
                        dc.DrawRectangle(editBg, null, new Rect(x, y, CharWidth * 2.5, LineHeight));
                    else if (isSelected)
                        dc.DrawRectangle(selBg, null, new Rect(x, y, CharWidth * 2.5, LineHeight));
                    else if (isCursor)
                        dc.DrawRectangle(cursorBg, null, new Rect(x, y, CharWidth * 2.5, LineHeight));
                    else if (isSearchHit)
                        dc.DrawRectangle(searchHitBg, null, new Rect(x, y, CharWidth * 2.5, LineHeight));

                    Brush fg;
                    string hexText;
                    if (isEditingThisByte)
                    {
                        // Show the in-progress edit value
                        hexText = $"{_editValue:X2} ";
                        fg = selFg;
                    }
                    else
                    {
                        hexText = $"{b:X2} ";
                        fg = isSelected ? selFg
                            : isChanged ? changedBrush
                            : b == 0 ? zeroBrush
                            : normalBrush;
                    }

                    DrawText(dc, hexText, x, y, fg);

                    // Draw nibble cursor indicator when editing
                    if (isEditingThisByte)
                    {
                        var nibbleX = x + (_editNibbleIndex * CharWidth);
                        dc.DrawRectangle(null, new Pen(selFg, 1),
                            new Rect(nibbleX, y + LineHeight - 2, CharWidth, 2));
                    }
                }
                else
                {
                    DrawText(dc, "   ", x, y, normalBrush);
                }
            }

            // Separator
            var sepX = hexX + (bpl * 3 * CharWidth) + CharWidth; // +1 for midline gap
            DrawText(dc, " \u2502 ", sepX, y, sepBrush);

            // ASCII pane
            var asciiX = sepX + (SeparatorChars * CharWidth);

            // Draw ASCII with selection highlighting
            for (var i = lineOffset; i < lineEnd; i++)
            {
                var colInLine = i - lineOffset;
                var x = asciiX + (colInLine * CharWidth);
                var c = (char)data[i];
                var ch = c is >= ' ' and <= '~' ? c : '.';
                var isSelected = selection.Contains(i);

                if (isSelected)
                    dc.DrawRectangle(selBg, null, new Rect(x, y, CharWidth, LineHeight));

                DrawText(dc, ch.ToString(), x, y, isSelected ? selFg : asciiBrush);
            }
        }
    }

    private void RenderPlaceholder(DrawingContext dc)
    {
        var fg = FindBrush("SecondaryForeground") ?? Brushes.Gray;
        DrawText(dc, "No data loaded. Attach to a process and navigate to an address.", Padding, Padding, fg);
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, Brush foreground)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, MonoTypeface, 13, foreground, _cachedPixelsPerDip);
        dc.DrawText(ft, new Point(x, y));
    }

    private Brush? FindBrush(string key)
    {
        try { return FindResource(key) as Brush; }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"[HexEditorControl] Resource '{key}' not found: {ex.Message}"); return null; }
    }

    // ── Mouse Input ──

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CancelEdit();

        var offset = HitTestByte(e.GetPosition(this));
        if (offset < 0) return;

        _isDragging = true;
        _dragAnchor = offset;
        CursorOffset = offset;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // Extend selection from current start
            var start = Math.Min(SelectionStart, offset);
            var end = Math.Max(SelectionStart + SelectionLength, offset + 1);
            SelectionStart = start;
            SelectionLength = end - start;
        }
        else
        {
            SelectionStart = offset;
            SelectionLength = 0;
        }

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_isDragging) return;

        var offset = HitTestByte(e.GetPosition(this));
        if (offset < 0) return;

        var start = Math.Min(_dragAnchor, offset);
        var end = Math.Max(_dragAnchor, offset) + 1;
        SelectionStart = start;
        SelectionLength = end - start;
        CursorOffset = offset;

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    // ── Keyboard Input ──

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var data = Source;
        if (data.Length == 0) return;

        var maxOffset = data.Length - 1;
        var bpl = BytesPerLine;
        var extending = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // Handle hex digit input when not read-only
        if (!IsReadOnly && TryGetHexDigit(e.Key, out var digit))
        {
            HandleHexDigitInput(digit);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
                CancelEdit();
                MoveCursor(Math.Max(0, CursorOffset - 1), extending);
                e.Handled = true;
                break;
            case Key.Right:
                CancelEdit();
                MoveCursor(Math.Min(maxOffset, CursorOffset + 1), extending);
                e.Handled = true;
                break;
            case Key.Up:
                CancelEdit();
                MoveCursor(Math.Max(0, CursorOffset - bpl), extending);
                e.Handled = true;
                break;
            case Key.Down:
                CancelEdit();
                MoveCursor(Math.Min(maxOffset, CursorOffset + bpl), extending);
                e.Handled = true;
                break;
            case Key.Home:
                CancelEdit();
                MoveCursor(0, extending);
                e.Handled = true;
                break;
            case Key.End:
                CancelEdit();
                MoveCursor(maxOffset, extending);
                e.Handled = true;
                break;
            case Key.Escape:
                CancelEdit();
                e.Handled = true;
                break;
            case Key.A when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                CancelEdit();
                SelectionStart = 0;
                SelectionLength = data.Length;
                e.Handled = true;
                break;
            case Key.Z when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                RaiseEvent(new ByteEditedEventArgs(ByteEditedEvent, this, 0, 0, 0, isUndo: true));
                e.Handled = true;
                break;
            case Key.Y when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                RaiseEvent(new ByteEditedEventArgs(ByteEditedEvent, this, 0, 0, 0, isRedo: true));
                e.Handled = true;
                break;
        }
    }

    private void HandleHexDigitInput(int digit)
    {
        var data = Source;
        if (data.Length == 0 || CursorOffset >= data.Length) return;

        if (!_isEditing)
        {
            // Start editing: high nibble
            _isEditing = true;
            _editNibbleIndex = 1; // High nibble entered, cursor moves to low
            _editValue = (byte)(digit << 4 | (data[CursorOffset] & 0x0F));
            InvalidateVisual();
        }
        else
        {
            // Low nibble — commit the edit
            _editValue = (byte)((_editValue & 0xF0) | digit);
            var oldByte = data[CursorOffset];
            var newByte = _editValue;
            _isEditing = false;
            _editNibbleIndex = 0;

            // Raise event for ViewModel to write to process memory
            RaiseEvent(new ByteEditedEventArgs(ByteEditedEvent, this,
                CursorOffset, oldByte, newByte));

            // Advance cursor to next byte
            if (CursorOffset < data.Length - 1)
            {
                CursorOffset++;
                SelectionStart = CursorOffset;
                SelectionLength = 0;
            }

            InvalidateVisual();
        }
    }

    private void CancelEdit()
    {
        if (_isEditing)
        {
            _isEditing = false;
            _editNibbleIndex = 0;
            InvalidateVisual();
        }
    }

    private static bool TryGetHexDigit(Key key, out int digit)
    {
        digit = key switch
        {
            Key.D0 or Key.NumPad0 => 0,
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            Key.A => 0xA,
            Key.B => 0xB,
            Key.C => 0xC,
            Key.D => 0xD,
            Key.E => 0xE,
            Key.F => 0xF,
            _ => -1
        };

        // Don't intercept Ctrl+A, Ctrl+C, etc.
        if (digit >= 0 && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            digit = -1;
            return false;
        }

        return digit >= 0;
    }

    private void MoveCursor(int newOffset, bool extendSelection)
    {
        if (extendSelection)
        {
            // Extend or shrink selection toward the new cursor position
            if (SelectionLength == 0)
                _dragAnchor = CursorOffset;

            var start = Math.Min(_dragAnchor, newOffset);
            var end = Math.Max(_dragAnchor, newOffset) + 1;
            SelectionStart = start;
            SelectionLength = end - start;
        }
        else
        {
            SelectionStart = newOffset;
            SelectionLength = 0;
            _dragAnchor = newOffset;
        }

        CursorOffset = newOffset;
    }

    // ── Hit Testing ──

    /// <summary>Determine which byte offset a screen point corresponds to.</summary>
    private int HitTestByte(Point pt)
    {
        var data = Source;
        if (data.Length == 0) return -1;

        var bpl = BytesPerLine;
        var line = (int)((pt.Y - Padding) / LineHeight);
        if (line < 0) return -1;

        var lineCount = (data.Length + bpl - 1) / bpl;
        if (line >= lineCount) return data.Length - 1;

        // Check if click is in hex pane
        var hexStartX = Padding + (AddressChars * CharWidth);
        var hexEndX = hexStartX + (bpl * 3 * CharWidth) + CharWidth;

        if (pt.X >= hexStartX && pt.X < hexEndX)
        {
            // Determine column in hex pane
            var relX = pt.X - hexStartX;
            var midline = bpl / 2;
            // Account for the extra gap at midline
            var col = (int)(relX / (3 * CharWidth));
            if (col >= midline) col = (int)((relX - CharWidth) / (3 * CharWidth));
            col = Math.Clamp(col, 0, bpl - 1);
            var offset = (line * bpl) + col;
            return Math.Min(offset, data.Length - 1);
        }

        // Check if click is in ASCII pane
        var sepX = hexEndX;
        var asciiStartX = sepX + (SeparatorChars * CharWidth);
        if (pt.X >= asciiStartX)
        {
            var col = (int)((pt.X - asciiStartX) / CharWidth);
            col = Math.Clamp(col, 0, bpl - 1);
            var offset = (line * bpl) + col;
            return Math.Min(offset, data.Length - 1);
        }

        // Click in address gutter — select whole line
        var lineStart = line * bpl;
        return Math.Min(lineStart, data.Length - 1);
    }
}

/// <summary>Event args for when a byte is edited in the hex editor.</summary>
public sealed class ByteEditedEventArgs : RoutedEventArgs
{
    public int BufferOffset { get; }
    public byte OldValue { get; }
    public byte NewValue { get; }
    public bool IsUndo { get; }
    public bool IsRedo { get; }

    public ByteEditedEventArgs(RoutedEvent routedEvent, object source,
        int bufferOffset, byte oldValue, byte newValue,
        bool isUndo = false, bool isRedo = false)
        : base(routedEvent, source)
    {
        BufferOffset = bufferOffset;
        OldValue = oldValue;
        NewValue = newValue;
        IsUndo = isUndo;
        IsRedo = isRedo;
    }
}
