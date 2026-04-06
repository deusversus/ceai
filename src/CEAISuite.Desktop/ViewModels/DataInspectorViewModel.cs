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

    /// <summary>Update all type interpretations from the given buffer at the given offset.</summary>
    public void Update(byte[] buffer, int offset)
    {
        _lastBuffer = buffer;
        _lastOffset = offset;
        Entries.Clear();

        if (buffer.Length == 0 || offset < 0 || offset >= buffer.Length)
            return;

        var remaining = buffer.Length - offset;
        var span = buffer.AsSpan(offset);

        // 1-byte types (always available)
        var b = span[0];
        Entries.Add(new DataInspectorEntry("Int8", ((sbyte)b).ToString(CultureInfo.InvariantCulture), $"0x{b:X2}"));
        Entries.Add(new DataInspectorEntry("UInt8", b.ToString(CultureInfo.InvariantCulture), $"0x{b:X2}"));
        Entries.Add(new DataInspectorEntry("Binary", Convert.ToString(b, 2).PadLeft(8, '0'), $"0x{b:X2}"));

        // 2-byte types
        if (remaining >= 2)
        {
            var s = span[..2];
            var i16 = IsBigEndian ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s);
            var u16 = IsBigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
            Entries.Add(new DataInspectorEntry("Int16", i16.ToString(CultureInfo.InvariantCulture), $"0x{u16:X4}"));
            Entries.Add(new DataInspectorEntry("UInt16", u16.ToString(CultureInfo.InvariantCulture), $"0x{u16:X4}"));
        }

        // 4-byte types
        if (remaining >= 4)
        {
            var s = span[..4];
            var i32 = IsBigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s);
            var u32 = IsBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
            var f32 = IsBigEndian ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s);
            Entries.Add(new DataInspectorEntry("Int32", i32.ToString(CultureInfo.InvariantCulture), $"0x{u32:X8}"));
            Entries.Add(new DataInspectorEntry("UInt32", u32.ToString(CultureInfo.InvariantCulture), $"0x{u32:X8}"));
            Entries.Add(new DataInspectorEntry("Float", f32.ToString("G9", CultureInfo.InvariantCulture), $"0x{u32:X8}"));
        }

        // 8-byte types
        if (remaining >= 8)
        {
            var s = span[..8];
            var i64 = IsBigEndian ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s);
            var u64 = IsBigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s);
            var f64 = IsBigEndian ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s);
            Entries.Add(new DataInspectorEntry("Int64", i64.ToString(CultureInfo.InvariantCulture), $"0x{u64:X16}"));
            Entries.Add(new DataInspectorEntry("UInt64", u64.ToString(CultureInfo.InvariantCulture), $"0x{u64:X16}"));
            Entries.Add(new DataInspectorEntry("Double", f64.ToString("G17", CultureInfo.InvariantCulture), $"0x{u64:X16}"));
            Entries.Add(new DataInspectorEntry("Pointer", $"0x{u64:X}", $"0x{u64:X16}"));
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
            Entries.Add(new DataInspectorEntry("ASCII", asciiSb.ToString(), ""));

        // UTF-16 — read until null wchar
        if (remaining >= 2)
        {
            var utf16Len = Math.Min(remaining / 2, 32) * 2;
            var utf16Bytes = span[..utf16Len];
            var utf16Sb = new StringBuilder();
            for (var i = 0; i < utf16Bytes.Length - 1; i += 2)
            {
                var wc = IsBigEndian
                    ? BinaryPrimitives.ReadUInt16BigEndian(utf16Bytes[i..])
                    : BinaryPrimitives.ReadUInt16LittleEndian(utf16Bytes[i..]);
                if (wc == 0) break;
                utf16Sb.Append(wc is >= 0x20 and <= 0x7E ? (char)wc : '.');
            }
            if (utf16Sb.Length > 0)
                Entries.Add(new DataInspectorEntry("UTF-16", utf16Sb.ToString(), ""));
        }
    }

    partial void OnIsBigEndianChanged(bool value)
    {
        if (_lastBuffer is not null)
            Update(_lastBuffer, _lastOffset);
    }
}
