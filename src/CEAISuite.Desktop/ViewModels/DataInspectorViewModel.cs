using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CEAISuite.Desktop.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CEAISuite.Desktop.ViewModels;

public partial class DataInspectorViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBigEndian;

    public ObservableCollection<DataInspectorEntry> Entries { get; } = [];

    private byte[]? _lastBuffer;
    private int _lastOffset;

    /// <summary>Update all type interpretations from the given buffer at the given offset (synchronous).</summary>
    public void Update(byte[] buffer, int offset)
    {
        _lastBuffer = buffer;
        _lastOffset = offset;

        if (buffer.Length == 0 || offset < 0 || offset >= buffer.Length)
        {
            Entries.Clear();
            return;
        }

        var entries = BuildEntries(buffer, offset, IsBigEndian);
        Entries.Clear();
        foreach (var entry in entries)
            Entries.Add(entry);
    }

    /// <summary>
    /// Async variant: builds entries off the UI thread, then updates the collection
    /// on the captured synchronization context (UI thread when called from WPF).
    /// </summary>
    public async Task UpdateAsync(byte[] buffer, int offset)
    {
        _lastBuffer = buffer;
        _lastOffset = offset;

        if (buffer.Length == 0 || offset < 0 || offset >= buffer.Length)
        {
            Entries.Clear();
            return;
        }

        var isBigEndian = IsBigEndian;
        var entries = await Task.Run(() => BuildEntries(buffer, offset, isBigEndian)).ConfigureAwait(false);

        // Marshal back to the original sync context (UI thread in WPF, inline in tests)
        if (System.Threading.SynchronizationContext.Current is not null)
        {
            Entries.Clear();
            foreach (var entry in entries)
                Entries.Add(entry);
        }
        else
        {
            // No sync context (test runner or background thread) — try WPF dispatcher if available
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher is { } dispatcher)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    Entries.Clear();
                    foreach (var entry in entries)
                        Entries.Add(entry);
                });
            }
            else
            {
                // Fallback: update directly (safe in tests)
                Entries.Clear();
                foreach (var entry in entries)
                    Entries.Add(entry);
            }
        }
    }

    private static List<DataInspectorEntry> BuildEntries(byte[] buffer, int offset, bool isBigEndian)
    {
        var result = new List<DataInspectorEntry>();
        var remaining = buffer.Length - offset;
        var span = buffer.AsSpan(offset);

        // 1-byte types (always available)
        var b = span[0];
        result.Add(new DataInspectorEntry("Int8", ((sbyte)b).ToString(CultureInfo.InvariantCulture), $"0x{b:X2}"));
        result.Add(new DataInspectorEntry("UInt8", b.ToString(CultureInfo.InvariantCulture), $"0x{b:X2}"));
        result.Add(new DataInspectorEntry("Binary", Convert.ToString(b, 2).PadLeft(8, '0'), $"0x{b:X2}"));

        // 2-byte types
        if (remaining >= 2)
        {
            var s = span[..2];
            var i16 = isBigEndian ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s);
            var u16 = isBigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
            result.Add(new DataInspectorEntry("Int16", i16.ToString(CultureInfo.InvariantCulture), $"0x{u16:X4}"));
            result.Add(new DataInspectorEntry("UInt16", u16.ToString(CultureInfo.InvariantCulture), $"0x{u16:X4}"));
        }

        // 4-byte types
        if (remaining >= 4)
        {
            var s = span[..4];
            var i32 = isBigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s);
            var u32 = isBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
            var f32 = isBigEndian ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s);
            result.Add(new DataInspectorEntry("Int32", i32.ToString(CultureInfo.InvariantCulture), $"0x{u32:X8}"));
            result.Add(new DataInspectorEntry("UInt32", u32.ToString(CultureInfo.InvariantCulture), $"0x{u32:X8}"));
            result.Add(new DataInspectorEntry("Float", f32.ToString("G9", CultureInfo.InvariantCulture), $"0x{u32:X8}"));
        }

        // 8-byte types
        if (remaining >= 8)
        {
            var s = span[..8];
            var i64 = isBigEndian ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s);
            var u64 = isBigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s);
            var f64 = isBigEndian ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s);
            result.Add(new DataInspectorEntry("Int64", i64.ToString(CultureInfo.InvariantCulture), $"0x{u64:X16}"));
            result.Add(new DataInspectorEntry("UInt64", u64.ToString(CultureInfo.InvariantCulture), $"0x{u64:X16}"));
            result.Add(new DataInspectorEntry("Double", f64.ToString("G17", CultureInfo.InvariantCulture), $"0x{u64:X16}"));
            result.Add(new DataInspectorEntry("Pointer", $"0x{u64:X}", $"0x{u64:X16}"));
        }

        // String interpretations
        var maxStringLen = Math.Min(remaining, 64);
        var asciiBytes = span[..maxStringLen];

        // ASCII — read until null or non-printable
        var asciiSb = new StringBuilder();
        foreach (var c in asciiBytes)
        {
            if (c == 0) break;
            asciiSb.Append(c is >= 0x20 and <= 0x7E ? (char)c : '.');
        }
        if (asciiSb.Length > 0)
            result.Add(new DataInspectorEntry("ASCII", asciiSb.ToString(), ""));

        // UTF-16 — read until null wchar
        if (remaining >= 2)
        {
            var utf16Len = Math.Min(remaining / 2, 32) * 2;
            var utf16Bytes = span[..utf16Len];
            var utf16Sb = new StringBuilder();
            for (var i = 0; i < utf16Bytes.Length - 1; i += 2)
            {
                var wc = isBigEndian
                    ? BinaryPrimitives.ReadUInt16BigEndian(utf16Bytes[i..])
                    : BinaryPrimitives.ReadUInt16LittleEndian(utf16Bytes[i..]);
                if (wc == 0) break;
                utf16Sb.Append(wc is >= 0x20 and <= 0x7E ? (char)wc : '.');
            }
            if (utf16Sb.Length > 0)
                result.Add(new DataInspectorEntry("UTF-16", utf16Sb.ToString(), ""));
        }

        return result;
    }

    partial void OnIsBigEndianChanged(bool value)
    {
        if (_lastBuffer is not null)
            Update(_lastBuffer, _lastOffset);
    }
}
